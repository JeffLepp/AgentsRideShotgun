using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// The one connection every outside agent uses stays live for as long as ARS is open, and an
/// agent that cannot reach it hears so in one line rather than hanging. Covers the packaged bridge
/// and the router's tickets against the fixture's own store: a router pipe that died after enough
/// clients dropped before the handshake, a ticket that went missing or stale while the app was
/// open, tickets a crashed ARS left behind, and a session that outlives an ARS restart.
///
/// No model is called and no provider configuration is read or written here.
/// </summary>
internal static class LiveRouter
{
    internal static void Run(Action<bool, string> Check, string? outputDirectory = null)
    {
        string ticket = WorkspaceAccessStore.RouterTicket;
        WorkspaceRouter.RetryEvery = TimeSpan.FromSeconds(1);
        try
        {
            // What a crashed or killed ARS leaves: tickets naming pipes that nobody serves.
            string dead = "Deskweave.Workspace." + Guid.NewGuid().ToString("N");
            string crashed = Path.Combine(WorkspaceAccessStore.Root, "crashed-workspace", "connection.json");
            Directory.CreateDirectory(Path.GetDirectoryName(crashed)!);
            File.WriteAllText(crashed, StaleTicket(dead));
            File.WriteAllText(ticket, StaleTicket(dead));
            WorkspaceRouter.Start();
            string pipe = WorkspaceRouter.Pipe ?? throw new InvalidOperationException("The router did not start.");
            Check(!File.Exists(crashed) && WorkspaceAccessStore.TicketPipe(ticket) == pipe,
                "Starting ARS clears tickets a crashed ARS left and publishes a live router ticket");

            // Before the fix one dropped client ended the accept loop somewhere between 30 and 300
            // of them, and the app went on advertising a pipe nobody answered.
            for (int i = 0; i < 400; i++)
            {
                using var dropped = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                dropped.Connect(5000);
            }
            using (var session = new Bridge(ticket))
            {
                JsonElement hello = session.Request("initialize", Hello);
                JsonElement tools = session.Request("tools/list", new { });
                Check(hello.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "ars"
                    && tools.GetProperty("result").GetProperty("tools").GetArrayLength() == WorkspaceMcp.ExternalToolSchemas.Length,
                    "After 400 clients drop before the handshake, the packaged bridge still initializes and lists the tools");
                Check(Text(session.Tool("status")).StartsWith("No workspace yet", StringComparison.Ordinal),
                    "A status call through the router answers without starting a workspace");
                int records = WorkspaceStore.All().Count, running = WorkspaceRuntime.Running.Count;
                object[] malformed = [new { }, new { name = "no_such_tool" },
                    new { name = "click", arguments = new { } },
                    new { name = "browse", arguments = "invalid" },
                    new { name = "click", arguments = new { x = "invalid", y = 1 } },
                    new { name = "computer", arguments = new { actions = new[] { new { type = "click", x = 1 } } } },
                    new { name = "batch", arguments = new { actions = new[] { new { action = "press", control = "invalid" } } } }];
                Check(malformed.All(parameters => session.Request("tools/call", parameters)
                        .GetProperty("error").GetProperty("code").GetInt32() == -32602)
                    && WorkspaceStore.All().Count == records && WorkspaceRuntime.Running.Count == running
                    && Text(session.Tool("status")).StartsWith("No workspace yet", StringComparison.Ordinal),
                    "Unknown tools and malformed arguments are rejected before routing creates a record, desktop or client");

                // ARS quits under a connected agent, then opens again.
                WorkspaceRouter.Stop();
                Check(!File.Exists(ticket), "Quitting ARS withdraws its own router ticket");
                Thread.Sleep(500);   // the bridge hears the pipe close; a call racing that is answered as "closed"
                var waited = Stopwatch.StartNew();
                JsonElement closed = session.Tool("status");
                Check(Failed(closed) && Text(closed) == "ARS isn't open. Ask the owner to open ARS, then try again."
                    && waited.Elapsed < TimeSpan.FromSeconds(5),
                    "A tool call while ARS is closed is answered in one line within five seconds, and the bridge stays up");
                WorkspaceRouter.Start();
                Check(WorkspaceRouter.Pipe is { } again && again != pipe && Text(session.Tool("status")).StartsWith("No workspace yet", StringComparison.Ordinal),
                    "Once ARS is open again the same agent session reaches the new router without reconnecting");
                pipe = WorkspaceRouter.Pipe!;
                Check(session.Close() == 0, "Closing the agent's end still ends the bridge");
            }

            File.Delete(ticket);
            Check(Until(() => WorkspaceAccessStore.TicketPipe(ticket) == pipe),
                "A router ticket deleted while ARS is open is put back");
            File.WriteAllText(ticket, StaleTicket(dead));
            Check(Until(() => WorkspaceAccessStore.TicketPipe(ticket) == pipe),
                "A router ticket overwritten with a dead pipe while ARS is open is replaced");

            // Two copies of ARS on one account, one per Windows session: neither takes the other's ticket.
            using (var other = new WorkspacePipeServer((_, _) => Task.FromResult<string?>(null)))
            {
                File.WriteAllText(ticket, StaleTicket(other.Name));
                Thread.Sleep(1500);
                Check(WorkspaceAccessStore.TicketPipe(ticket) == other.Name,
                    "A ticket naming another live ARS is left alone rather than fought over");
                WorkspaceRouter.Stop();
                Check(WorkspaceAccessStore.TicketPipe(ticket) == other.Name,
                    "Quitting never withdraws a ticket that another live ARS published");
            }
            File.Delete(ticket);

            // Nothing is open: a missing ticket, a ticket from a crash, and one from another version.
            (int code, string errors, string output, TimeSpan took) = Once(ticket, "missing", outputDirectory);
            Check(code == 1 && errors.Trim() == "ARS isn't open. Open ARS, then reconnect this MCP server."
                && output.Length == 0 && took < TimeSpan.FromSeconds(5),
                "With no ticket the bridge exits in under five seconds with one line and nothing on the MCP stream");
            File.WriteAllText(ticket, StaleTicket(dead));
            (code, errors, output, took) = Once(ticket, "stale", outputDirectory);
            Check(code == 1 && errors.Trim() == "ARS isn't open. Open ARS, then reconnect this MCP server."
                && output.Length == 0 && took < TimeSpan.FromSeconds(5),
                "A ticket naming a pipe nobody serves fails in under five seconds instead of waiting ten on it");
            File.WriteAllText(ticket, "{\"schema\":2}");
            (code, errors, output, took) = Once(ticket, "old-schema", outputDirectory);
            Check(code == 2 && errors.Trim().StartsWith("This MCP entry doesn't match the ARS on this PC.", StringComparison.Ordinal)
                && output.Length == 0 && took < TimeSpan.FromSeconds(2),
                "A ticket from another version says to connect the agent again, at once and not silently");
            File.Delete(ticket);

            // A call that is running when ARS goes is answered, not left for the client to time out.
            using (var slow = new WorkspacePipeServer(async (body, cancel) =>
            {
                if (body.Contains("\"id\"", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, cancel);
                return null;
            }))
            {
                WorkspaceAccessStore.Publish("in-flight", slow);
                using var session = new Bridge(WorkspaceAccessStore.Connection("in-flight"));
                Task<JsonElement> running = Task.Run(() => session.Tool("wait"));
                Thread.Sleep(500);
                slow.Dispose();
                bool answered = running.Wait(TimeSpan.FromSeconds(5));
                Check(answered && Failed(running.Result)
                    && Text(running.Result) == "ARS closed while this was running. Ask the owner to open ARS, then try again.",
                    "A tool call in flight when ARS closes is answered at once with one line");
            }
            WorkspaceAccessStore.Withdraw("in-flight");
            CheckQueuedReplies(Check);
            CheckQueuedDisconnect(Check);
            CheckRevokedRoute(Check);
        }
        finally
        {
            WorkspaceRouter.Stop();
            WorkspaceRouter.RetryEvery = TimeSpan.FromSeconds(5);
        }
    }

    static void CheckQueuedReplies(Action<bool, string> check)
    {
        using var server = new WorkspacePipeServer(async (request, cancel) =>
        {
            if (request == "first") await Task.Delay(100, cancel);
            return request;
        });
        using var pipe = new NamedPipeClientStream(".", server.Name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        pipe.ConnectAsync(bound.Token).GetAwaiter().GetResult();
        WorkspacePipeProtocol.Write(pipe, server.Capability, 256, bound.Token).GetAwaiter().GetResult();
        if (WorkspacePipeProtocol.Read(pipe, 256, bound.Token).GetAwaiter().GetResult() != "workspace-pipe/1")
            throw new IOException("Queued-replies probe did not authenticate.");
        string[] requests = ["first", "second", "third"];
        foreach (string request in requests)
            WorkspacePipeProtocol.Write(pipe, request, 256, bound.Token).GetAwaiter().GetResult();
        check(requests.All(request => WorkspacePipeProtocol.Read(pipe, 256, bound.Token).GetAwaiter().GetResult() == request),
            "Pipelined requests each receive one reply in the original order");
    }

    static void CheckQueuedDisconnect(Action<bool, string> check)
    {
        using var started = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        using var server = new WorkspacePipeServer(() => new(async (_, cancel) =>
        {
            started.Set();
            await Task.Delay(Timeout.Infinite, cancel);
            return null;
        }, closed.Set));
        using (var pipe = new NamedPipeClientStream(".", server.Name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            pipe.ConnectAsync(bound.Token).GetAwaiter().GetResult();
            WorkspacePipeProtocol.Write(pipe, server.Capability, 256, bound.Token).GetAwaiter().GetResult();
            if (WorkspacePipeProtocol.Read(pipe, 256, bound.Token).GetAwaiter().GetResult() != "workspace-pipe/1")
                throw new IOException("Queued-disconnect probe did not authenticate.");
            WorkspacePipeProtocol.Write(pipe, "first", 256, bound.Token).GetAwaiter().GetResult();
            if (!started.Wait(TimeSpan.FromSeconds(2))) throw new IOException("The first request did not start.");
            WorkspacePipeProtocol.Write(pipe, "queued", 256, bound.Token).GetAwaiter().GetResult();
            Thread.Sleep(100); // let the server consume its read-ahead frame before the client exits
        }
        check(closed.Wait(TimeSpan.FromSeconds(3)),
            "Bridge disconnect cancels the running request and releases its peer even with another request queued");
    }

    static void CheckRevokedRoute(Action<bool, string> check)
    {
        string project = Directory.CreateTempSubdirectory("Deskweave-revoked-route-").FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        StoredWorkspace workspace = WorkspaceStore.Create("Revoked route probe");
        WorkspaceStore.Update(workspace.Id, w => w with { Agents = WorkspaceHome.Folder(project) });
        var disabled = new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false };
        WorkspaceAccessStore.Write(workspace.Id, disabled);
        int running = WorkspaceRuntime.Running.Count;
        try
        {
            var first = WorkspaceRouter.Place(project, "codex");
            var reconnect = WorkspaceRouter.Place(project, "codex");
            check(first.Runtime is null && reconnect.Runtime is null
                && first.Why?.StartsWith("Agent access is off", StringComparison.Ordinal) == true
                && reconnect.Why == first.Why && WorkspaceAccessStore.Read(workspace.Id) == disabled
                && WorkspaceRuntime.Running.Count == running,
                "Routing and reconnect both preserve a disabled workspace policy without starting its desktop");
        }
        finally
        {
            WorkspaceStore.Delete(workspace.Id);
            Directory.Delete(project, recursive: true);
        }
    }

    static readonly object Hello = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Deskweave engine probe", version = "1" } };

    static string StaleTicket(string pipe) => JsonSerializer.Serialize(new
    {
        schema = 1, pipe, capability = Convert.ToBase64String(new byte[32]),
    });

    static bool Until(Func<bool> ready)
    {
        var waited = Stopwatch.StartNew();
        while (!ready() && waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        return ready();
    }

    static bool Failed(JsonElement reply) => reply.TryGetProperty("error", out _)
        || reply.GetProperty("result").TryGetProperty("isError", out JsonElement error) && error.GetBoolean();

    static string Text(JsonElement reply) => reply.TryGetProperty("error", out JsonElement error)
        ? error.GetProperty("message").GetString()!
        : reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>Starts the bridge as an agent would, sends initialize, and waits for it to end.</summary>
    static (int Code, string Errors, string Output, TimeSpan Took) Once(string ticket, string scenario, string? outputDirectory)
    {
        var waited = Stopwatch.StartNew();
        using var bridge = new Bridge(ticket);
        bridge.Send("initialize", Hello);
        int code = bridge.Exit(TimeSpan.FromSeconds(20));
        TimeSpan exited = waited.Elapsed;
        string errors = bridge.Errors, output = bridge.Output;
        TimeSpan took = waited.Elapsed;
        // Keep the existing end-to-end timing bound. Distinguish process startup/exit from stream
        // drain delays so a heavily loaded test run leaves enough evidence to diagnose a failure.
        if (outputDirectory is not null)
            File.AppendAllText(Path.Combine(outputDirectory, "router-bridge-exits.jsonl"), JsonSerializer.Serialize(new
            {
                scenario, observedAt = DateTimeOffset.UtcNow, code, stdout = output, stderr = errors,
                exitedMs = exited.TotalMilliseconds, elapsedMs = took.TotalMilliseconds,
            }) + Environment.NewLine);
        return (code, errors, output, took);
    }

    /// <summary>Repeats only the router/transport gate, with isolated stores and no visible windows.</summary>
    internal static int RunStandalone(string output)
    {
        Directory.CreateDirectory(output);
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(output, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(output, "settings.json"));
        var claims = new List<string>();
        string? failure = null;
        try
        {
            Run((passed, claim) =>
            {
                if (!passed) throw new InvalidOperationException("Failed: " + claim);
                claims.Add(claim);
                File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
            }, output);
        }
        catch (Exception error) { failure = error.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            bridge = WorkspaceConnections.Bridge, claims, failure, globalInputEventsSent = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    /// <summary>The packaged bridge on a ticket, driven over its standard streams like an MCP client.</summary>
    sealed class Bridge : IDisposable
    {
        readonly Process _process;
        readonly Task<string> _errors;
        Task<string>? _output;
        int _sequence;

        internal Bridge(string ticket)
        {
            var start = new ProcessStartInfo(WorkspaceConnections.Bridge)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(ticket);
            _process = Process.Start(start) ?? throw new IOException("The bridge did not start.");
            _errors = _process.StandardError.ReadToEndAsync();
        }

        internal string Errors => _errors.GetAwaiter().GetResult();
        internal string Output => (_output ??= _process.StandardOutput.ReadToEndAsync()).GetAwaiter().GetResult();

        internal void Send(string method, object parameters)
        {
            try
            {
                _process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _sequence), method, @params = parameters }));
                _process.StandardInput.Flush();
            }
            catch (IOException) { }   // it already left, which is what some checks expect
        }

        internal JsonElement Request(string method, object parameters)
        {
            int id;
            lock (_process)
            {
                Send(method, parameters);
                id = _sequence;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                string reply = _process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()
                    ?? throw new IOException("The bridge closed before replying: " + Errors);
                using var json = JsonDocument.Parse(reply);
                if (json.RootElement.TryGetProperty("id", out JsonElement got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
                    return json.RootElement.Clone();
            }
        }

        internal JsonElement Tool(string name) => Request("tools/call", new { name, arguments = new { } });

        internal int Close()
        {
            _process.StandardInput.Close();
            return Exit(TimeSpan.FromSeconds(10));
        }

        internal int Exit(TimeSpan patience)
        {
            if (!_process.WaitForExit(patience)) { _process.Kill(entireProcessTree: true); return -1; }
            _process.WaitForExit();
            return _process.ExitCode;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch (IOException) { }
                if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
    }
}
