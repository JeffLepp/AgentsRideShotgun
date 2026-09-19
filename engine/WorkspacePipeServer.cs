using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HiveMind.AgentWorkspaces;

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
    /// ticket naming a pipe that is gone belongs to a Deskweave that stopped without withdrawing it.
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

        Task<string?> next = WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxRequestBytes, connection.Token);
        while (await next.ConfigureAwait(false) is { } request)
        {
            // Read ahead while a tool is running so killing its bridge also cancels waits/batches.
            next = WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxRequestBytes, connection.Token);
            Task<string?> response = Task.Run(() => peer.Handle(request, connection.Token), connection.Token);
            try
            {
                if (await Task.WhenAny(response, next).ConfigureAwait(false) == next
                    && await next.ConfigureAwait(false) is null)
                {
                    connection.Cancel();
                    return;
                }
                await WorkspacePipeProtocol.Write(pipe, await response.ConfigureAwait(false),
                    WorkspacePipeProtocol.MaxResponseBytes, connection.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!response.IsCompleted)
                {
                    connection.Cancel();
                    try { await response.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }
        }
        // InvalidDataException is a SystemException, not an IOException: a malformed length prefix or
        // an oversized response ends this connection, as it does for the bridge, and never faults the
        // handler task - an unobserved fault here closed the pipe and told the agent Deskweave had
        // closed while its request was running, about an app that was running perfectly well.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException or System.Text.DecoderFallbackException) { }
        finally { connection.Cancel(); peer?.Closed(); }
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
