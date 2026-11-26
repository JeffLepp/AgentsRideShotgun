using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Deskweave.AgentWorkspaces;

internal sealed record WorkspacePipePeer(Func<string, CancellationToken, Task<string?>> Handle, Action Closed);

/// <summary>Bounded same-user connections, serial within each connection. No TCP listener.</summary>
internal sealed class WorkspacePipeServer : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly Func<WorkspacePipePeer> _connect;
    readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    readonly Task _server;
    readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    readonly NamedPipeServerStream _first;
    readonly int _limit;
    readonly Func<int, bool>? _rejectClientProcess;
    int _disposed;

    internal string Name { get; } = "Deskweave.Workspace." + Guid.NewGuid().ToString("N");
    internal string Capability => Convert.ToBase64String(_key);

    internal WorkspacePipeServer(Func<string, CancellationToken, Task<string?>> handle,
        Func<int, bool>? rejectClientProcess = null)
        : this(() => new(handle, () => { }), 1, rejectClientProcess) { }

    internal WorkspacePipeServer(Func<WorkspacePipePeer> connect, int maxClients = 8,
        Func<int, bool>? rejectClientProcess = null)
    {
        _connect = connect;
        _limit = Math.Clamp(maxClients, 1, 32);
        _rejectClientProcess = rejectClientProcess;
        // Reserve the name synchronously. Startup cannot report ready before its pipe exists.
        _first = Create(first: true);
        _server = Task.Run(Serve);
    }

    NamedPipeServerStream Create(bool first = false) => new(Name, PipeDirection.InOut, _limit + 2,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 4096, 4096);

    /// <summary>
    /// Whether a pipe by this name is served right now, without connecting to it. A connection
    /// ticket naming a pipe that is gone belongs to an ARS that stopped without withdrawing it.
    /// </summary>
    internal static bool Exists(string name)
    {
        if (Native.WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
        // Every instance busy still means someone serves it; only "not found" means nobody does.
        int error = Marshal.GetLastWin32Error();
        return error is not (2 or 123 or 161);   // ERROR_FILE_NOT_FOUND, ERROR_INVALID_NAME, ERROR_BAD_PATHNAME
    }

    async Task Serve()
    {
        NamedPipeServerStream? listener = _first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await listener.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
                catch (IOException) when (!_stop.IsCancellationRequested)
                {
                    // A client that left before it was accepted: a bridge killed while connecting, a
                    // health check. It used to end this loop, and the app went on advertising a pipe
                    // nobody answered. The next instance goes up before this one comes down, so the
                    // name never disappears from under a bridge checking for it.
                    var dropped = listener;
                    try { listener = await Replacement().ConfigureAwait(false); }
                    finally { dropped.Dispose(); }
                    continue;
                }
                var accepted = listener;
                try { listener = await Replacement().ConfigureAwait(false); }
                catch { accepted.Dispose(); throw; }
                if (_clients.Count >= _limit) { accepted.Dispose(); continue; }
                _clients[accepted] = Task.CompletedTask;
                Task client = Task.Run(() => Talk(accepted));
                _clients[accepted] = client;
                _ = client.ContinueWith(_ => _clients.TryRemove(accepted, out Task? ignored), TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            listener?.Dispose();
            foreach (var client in _clients.Keys) client.Dispose();
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        }
    }

    /// <summary>The next listening instance. A refusal from Windows is waited out, not fatal.</summary>
    async Task<NamedPipeServerStream> Replacement()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return Create(); }
            catch (IOException) when (!_stop.IsCancellationRequested)
            {
                await Task.Delay(attempt < 50 ? 100 : 1000, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    async Task Talk(NamedPipeServerStream pipe)
    {
        WorkspacePipePeer? peer = null;
        using (pipe)
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        try
        {
        if (_rejectClientProcess is not null
            && (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid)
                || clientPid > int.MaxValue || _rejectClientProcess((int)clientPid)))
            return;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            string? supplied = await WorkspacePipeProtocol.Read(pipe, 256, handshake.Token).ConfigureAwait(false);
            byte[] bytes = new byte[32];
            if (supplied is null || !Convert.TryFromBase64String(supplied, bytes, out int count)
                || count != _key.Length || !CryptographicOperations.FixedTimeEquals(bytes, _key)) return;
            peer = _connect();
            await WorkspacePipeProtocol.Write(pipe, "workspace-pipe/1", 256, handshake.Token).ConfigureAwait(false);
        }

        // A queued frame is either a request still to run, or (Request null) one already answered:
        // refused because too many were waiting. Only its short reply is kept, never the request.
        var queued = new Queue<(string? Request, string? Answer)>();
        int waiting = 0;                                                 // queued frames that will run
        var dropped = new HashSet<string>(StringComparer.Ordinal);     // cancelled before they started
        Task<string?> next = WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxRequestBytes, connection.Token);
        while (true)
        {
            string? request;
            if (queued.Count > 0)
            {
                (request, string? answer) = queued.Dequeue();
                if (request is null)
                {
                    await WorkspacePipeProtocol.Write(pipe, answer, WorkspacePipeProtocol.MaxResponseBytes, connection.Token)
                        .ConfigureAwait(false);
                    continue;
                }
                waiting--;
            }
            else
            {
                request = await next.ConfigureAwait(false);
                if (request is null) break;
                next = WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxRequestBytes, connection.Token);
            }
            if (dropped.Count > 0 && RequestId(request) is { } skipped && dropped.Remove(skipped))
            {
                await WorkspacePipeProtocol.Write(pipe, null, WorkspacePipeProtocol.MaxResponseBytes, connection.Token)
                    .ConfigureAwait(false);
                continue;
            }
            // Each call has its own token, so the client's notifications/cancelled can end that one
            // call without closing the connection. Requests stay one at a time; only the read-ahead
            // below already sees a cancellation while the call it names is running.
            using var call = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            Task<string?> response = Task.Run(() => peer.Handle(request, call.Token), call.Token);
            try
            {
                // Keep reading after a queued call too. Stopping at the first complete frame hid
                // bridge EOF behind that frame and left the running tool holding its lease.
                // Bound pipelining so a client cannot retain an unlimited request backlog.
                while (!response.IsCompleted && await Task.WhenAny(response, next).ConfigureAwait(false) == next)
                {
                    string? pending = await next.ConfigureAwait(false);
                    if (pending is null) { connection.Cancel(); return; }
                    // Only a client that ignores every reply gets this far; it is not an agent session.
                    if (queued.Count >= MaxAnswered) throw new InvalidDataException("Too many queued workspace requests.");
                    if (CancelledId(pending) is { } cancelled)
                    {
                        if (cancelled == RequestId(request)) call.Cancel();
                        else if (queued.Any(frame => frame.Request is { } queuedRequest && RequestId(queuedRequest) == cancelled))
                            dropped.Add(cancelled);
                    }
                    // Still queued, cancellation and refusal included: every frame gets its one reply, in
                    // order. Past the bound a request is answered with an error instead of ending the
                    // connection, which would have failed every call before it too.
                    if (waiting >= MaxWaiting) queued.Enqueue((null, Refuse(pending, Busy)));
                    else { queued.Enqueue((pending, null)); waiting++; }
                    next = WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxRequestBytes, connection.Token);
                }
                string? reply;
                try { reply = await response.ConfigureAwait(false); }
                // Anything else the call throws, a time limit inside a tool included, is that call's
                // failure: it is answered, and the connection and the calls queued behind it carry on.
                // The connection's own end still ends it.
                catch (Exception error) when (!connection.IsCancellationRequested)
                {
                    reply = call.IsCancellationRequested ? null
                        : Refuse(request, error is OperationCanceledException ? WorkspaceMcp.TimedOut
                            : "ARS hit an unexpected error on this call (" + error.GetType().Name + "). Check with status, then retry it if needed.");
                }
                // A cancelled request gets no answer (MCP), even when the tool caught the cancellation
                // and returned something; the empty frame only keeps the bridge's replies in order.
                if (call.IsCancellationRequested && !connection.IsCancellationRequested) reply = null;
                await WorkspacePipeProtocol.Write(pipe, reply,
                    WorkspacePipeProtocol.MaxResponseBytes, connection.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!response.IsCompleted)
                {
                    connection.Cancel();
                    try { await response.ConfigureAwait(false); }
                    catch (Exception) { }   // the connection is ending; its reply has nowhere to go
                }
            }
        }
        }
        // InvalidDataException is a SystemException, not an IOException: a malformed length prefix or
        // an oversized response ends this connection, as it does for the bridge, and never faults the
        // handler task - an unobserved fault here closed the pipe and told the agent ARS had
        // closed while its request was running, about an app that was running perfectly well.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException or System.Text.DecoderFallbackException) { }
        finally { connection.Cancel(); peer?.Closed(); }
    }

    /// <summary>Requests that may wait behind the running one; more are answered with <see cref="Busy"/>.</summary>
    internal const int MaxWaiting = 16;
    const int MaxAnswered = 1024;
    internal const string Busy = "ARS is still working through earlier calls from this session. Retry this call in a few seconds.";

    /// <summary>
    /// The one reply to a request ARS will not run: an isError result for a tool call, so the agent
    /// reads it as that tool's answer, a JSON-RPC error for anything else, nothing for a notification.
    /// </summary>
    internal static string? Refuse(string frame, string text)
    {
        string method = "";
        try
        {
            using var json = JsonDocument.Parse(frame);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("method", out JsonElement m)
                && m.ValueKind == JsonValueKind.String) method = m.GetString() ?? "";
        }
        catch (JsonException) { }
        if (RequestId(frame) is not { } id) return null;
        string encoded = System.Text.Json.JsonEncodedText.Encode(text).ToString();
        return method == "tools/call"
            ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + encoded + "\"}],\"isError\":true}}"
            : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32000,\"message\":\"" + encoded + "\"}}";
    }

    /// <summary>A JSON-RPC request's id as written, or null for a notification or anything else.</summary>
    static string? RequestId(string frame)
    {
        try
        {
            using var json = JsonDocument.Parse(frame);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("id", out JsonElement id)
                && id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id.GetRawText() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The request id a notifications/cancelled names, as written, or null for any other frame.</summary>
    static string? CancelledId(string frame)
    {
        if (!frame.Contains("notifications/cancelled", StringComparison.Ordinal)) return null;
        try
        {
            using var json = JsonDocument.Parse(frame);
            JsonElement root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("method", out JsonElement method) && method.ValueKind == JsonValueKind.String && method.ValueEquals("notifications/cancelled")
                && root.TryGetProperty("params", out JsonElement given) && given.ValueKind == JsonValueKind.Object
                && given.TryGetProperty("requestId", out JsonElement id)
                && id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id.GetRawText() : null;
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _first.Dispose();
        foreach (var client in _clients.Keys) client.Dispose();
        _ = _server.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
