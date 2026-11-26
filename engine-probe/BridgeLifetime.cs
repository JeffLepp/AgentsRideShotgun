using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Runtime.InteropServices;
using Deskweave.AgentWorkspaces;

/// <summary>What the bridge (engine/Bridge/Program.cs) tells an agent; the bridge shares no code with the engine.</summary>
internal static class BridgeTexts
{
    internal const string Restarting = "ARS is restarting. Retry this call in a few seconds. "
        + "Do not do this work with your own desktop, computer-use or browser tools.";
    internal const string Interrupted = "ARS restarted while this call was running, so it may have partly happened. "
        + "Check, then retry it in a few seconds if needed. Do not do this work with your own desktop, computer-use or browser tools.";
    internal const string TurnedOff = "The owner turned ARS off, so it will not come back on its own. Ask the user before doing this work another way.";
    internal const string Stuck = "ARS isn't answering. Retry this call in a few seconds, and ask the user if it keeps failing. "
        + "Do not do this work with your own desktop, computer-use or browser tools.";
    internal const string Busy = "ARS is still working through earlier calls from this session. Retry this call in a few seconds.";
    internal const string Mismatched = "This MCP entry doesn't match the ARS on this PC. Connect the agent again from ARS's Settings.";
}

internal static class BridgeLifetime
{
    internal static int Parent(string ticket, string pidFile)
    {
        // Inherit the fixture's standard handles. The grandparent deliberately keeps stdin
        // open after this parent dies, so EOF cannot accidentally prove the parent-watch test.
        var startup = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>(), dwFlags = 0x100,
            hStdInput = GetStdHandle(-10), hStdOutput = GetStdHandle(-11), hStdError = GetStdHandle(-12) };
        if (!Native.CreateProcessW(WorkspaceConnections.Bridge,
            new StringBuilder("\"" + WorkspaceConnections.Bridge + "\" --workspace \"" + ticket + "\""),
            0, 0, true, 0x08000000, 0, null, ref startup, out Native.ProcessInfo child)) throw new IOException("Fixture CreateProcess failed.");
        Native.CloseHandle(child.hThread); Native.CloseHandle(child.hProcess);
        File.WriteAllText(pidFile, child.dwProcessId.ToString());
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(output, "w"));
        var claims = new List<object>();
        void Check(bool passed, string name, double elapsed = 0)
        {
            if (!passed) throw new InvalidOperationException(name);
            claims.Add(new { name, elapsedMs = elapsed });
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + name + Environment.NewLine);
        }
        string? failure = null;
        try
        {
            WorkspaceRouter.Start();
            WorkspaceAccessStore.MarkQuit();
            using (var staleMark = Start(WorkspaceAccessStore.RouterTicket))
            {
                staleMark.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":90,\"method\":\"ping\"}");
                Check(Reply(staleMark, 90), "An answering controller authenticates despite an old quit mark");
                Thread.Sleep(400);
                staleMark.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":91,\"method\":\"ping\"}");
                Check(Reply(staleMark, 91), "The quit watcher preserves a live controller with an unchanged stale mark");
                staleMark.StandardInput.Close();
                Check(staleMark.WaitForExit(2000), "The stale-mark fixture bridge still exits on stdin EOF");
            }
            WorkspaceAccessStore.ClearQuit();
            using (var bridge = Start(WorkspaceAccessStore.RouterTicket))
            {
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                Check(Reply(bridge, 1), "Live bridge answers before EOF test");
                var elapsed = Stopwatch.StartNew();
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000) && bridge.ExitCode == 0, "Live MCP stdin EOF exits within two seconds", elapsed.Elapsed.TotalMilliseconds);
            }
            // A served pipe that never finishes the handshake used to hide EOF until startup timed out.
            string name = "Deskweave.Workspace." + Guid.NewGuid().ToString("N");
            using (var stalled = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                string ticket = Path.Combine(output, "stalled.json");
                File.WriteAllText(ticket, JsonSerializer.Serialize(new { schema = 1, pipe = name, capability = "fixture" }));
                using var bridge = Start(ticket);
                Check(stalled.WaitForConnectionAsync().Wait(5000), "Bridge reaches stalled startup handshake");
                var elapsed = Stopwatch.StartNew();
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000), "EOF interrupts a blocked startup handshake", elapsed.Elapsed.TotalMilliseconds);
            }
            string pidFile = Path.Combine(output, "orphan.pid");
            var parentStart = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            // When invoked through dotnet, child modes need the probe DLL first.
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                parentStart.ArgumentList.Add(typeof(BridgeLifetime).Assembly.Location);
            parentStart.ArgumentList.Add("--bridge-parent"); parentStart.ArgumentList.Add(WorkspaceAccessStore.RouterTicket); parentStart.ArgumentList.Add(pidFile);
            using (var parent = Process.Start(parentStart)!)
            {
                Check(SpinWait.SpinUntil(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Length > 0, 5000), "Fixture starts an MCP bridge with an independently held stdin");
                using var orphan = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                try
                {
                    // Read a real reply to establish that the bridge captured its parent's identity.
                    parent.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
                    Check(Reply(parent, 2), "Inherited stdin remains connected before parent death");
                    var elapsed = Stopwatch.StartNew();
                    parent.Kill(); parent.WaitForExit();
                    Check(orphan.WaitForExit(2000), "Parent death exits bridge while client stdin is still held open", elapsed.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); }
                    if (!orphan.HasExited) { orphan.Kill(); orphan.WaitForExit(); }
                }
            }
            // The bridge lives as long as its client. The router's own replies are what it must
            // repeat by itself while ARS is away.
            var seen = new List<string>();
            JsonElement liveHello, liveTools;
            using (var live = Start(WorkspaceAccessStore.RouterTicket))
            {
                Send(live, "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Bridge lifetime probe\",\"version\":\"1\"}}}");
                liveHello = Answer(live, 10, seen)?.GetProperty("result") ?? throw new InvalidOperationException("The live router did not initialize.");
                Send(live, "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/list\"}");
                liveTools = Answer(live, 11, seen)?.GetProperty("result") ?? throw new InvalidOperationException("The live router did not list its tools.");
                // A line past 1 MB in characters, and one under it in characters but over it in bytes.
                Send(live, "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"run\",\"arguments\":{\"command\":\""
                    + new string('a', 1_100_000) + "\"}}}");
                JsonElement? big = Answer(live, 12, seen);
                Send(live, "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"run\",\"arguments\":{\"command\":\""
                    + new string('é', 600_000) + "\"}}}");
                JsonElement? wide = Answer(live, 13, seen);
                Send(live, "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? after = Answer(live, 14, seen);
                Check(big is { } b && LiveRouter.Failed(b) && LiveRouter.Text(b).Contains("larger than ARS accepts", StringComparison.Ordinal)
                    && wide is { } w && LiveRouter.Failed(w) && LiveRouter.Text(w).Contains("larger than ARS accepts", StringComparison.Ordinal)
                    && after is { } a && !LiveRouter.Failed(a) && LiveRouter.Text(a).StartsWith("No workspace yet", StringComparison.Ordinal)
                    && !live.HasExited,
                    "Oversized request lines are refused for their own ids and the same bridge and connection keep serving");
                live.StandardInput.Close();
                Check(live.WaitForExit(2000), "The bridge that refused oversized lines still exits on client EOF");
            }
            using (var bridge = Start(WorkspaceAccessStore.RouterTicket))
            {
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
                Check(Reply(bridge, 3), "Live bridge answers before owner quit");
                // What the app's Quit does: the mark first, then the router closes.
                WorkspaceAccessStore.MarkQuit();
                WorkspaceRouter.Stop();
                Thread.Sleep(2500);
                Check(!bridge.HasExited, "Owner quit leaves the bridge running while its client stays connected");
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? off = Answer(bridge, 30, seen);
                Check(off is { } o && LiveRouter.Failed(o) && LiveRouter.Text(o) == BridgeTexts.TurnedOff && !bridge.HasExited,
                    "After owner quit a tool call is answered with the owner-turned-off text and the bridge stays up");
                // The owner opens ARS again: its start clears the mark and publishes a new router.
                WorkspaceAccessStore.ClearQuit();
                WorkspaceRouter.Start();
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? back = Answer(bridge, 31, seen);
                Check(back is { } r && !LiveRouter.Failed(r) && LiveRouter.Text(r).StartsWith("No workspace yet", StringComparison.Ordinal),
                    "When the owner opens ARS again the same bridge reaches it on the next call, without an MCP refresh");
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000), "The bridge kept through a quit still exits on client EOF");
            }
            // Per-workspace connection tickets live one directory below the shared quit mark.
            string direct = Path.Combine(WorkspaceAccessStore.Root, "direct-fixture", "connection.json");
            Directory.CreateDirectory(Path.GetDirectoryName(direct)!);
            File.Copy(WorkspaceAccessStore.RouterTicket, direct);
            using (var bridge = Start(direct))
            {
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}");
                Check(Reply(bridge, 4), "Per-workspace ticket bridge is live before quit");
                WorkspaceAccessStore.MarkQuit();
                WorkspaceRouter.Stop();
                Thread.Sleep(1000);
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":40,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? off = Answer(bridge, 40, seen);
                Check(off is { } o && LiveRouter.Failed(o) && LiveRouter.Text(o) == BridgeTexts.TurnedOff && !bridge.HasExited,
                    "A per-workspace bridge finds the shared quit mark one folder up, stays up, and says the owner turned ARS off");
                WorkspaceAccessStore.ClearQuit();
                WorkspaceRouter.Start();
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000), "A per-workspace bridge kept through a quit still exits on client EOF");
            }

            // ARS closed (not quit) when the agent starts: the session still starts.
            WorkspaceRouter.Stop();
            using (var bridge = Start(WorkspaceAccessStore.RouterTicket))
            {
                // Startup waits about two seconds on a fixture ticket, so 16 of these wait and 4 are refused.
                for (int id = 100; id < 120; id++) Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"ping\"}");
                var answers = new Dictionary<int, JsonElement>();
                for (int id = 100; id < 120; id++)
                    if (Answer(bridge, id, seen, answers) is { } got) answers[id] = got;
                int busy = answers.Values.Count(a => a.TryGetProperty("error", out JsonElement e)
                    && e.GetProperty("message").GetString() == BridgeTexts.Busy);
                Check(answers.Count == 20 && busy == 4 && !bridge.HasExited,
                    "A backlog past 16 requests is refused per request and the bridge keeps serving");
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":120,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Bridge lifetime probe\",\"version\":\"1\"}}}");
                JsonElement? hello = Answer(bridge, 120, seen);
                Check(hello is { } h && h.TryGetProperty("result", out JsonElement helloResult)
                    && JsonNode.DeepEquals(JsonNode.Parse(helloResult.GetRawText()), JsonNode.Parse(liveHello.GetRawText())),
                    "With ARS closed at startup the bridge answers initialize with the router's own reply and instructions");
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":121,\"method\":\"tools/list\"}");
                JsonElement? tools = Answer(bridge, 121, seen);
                Check(tools is { } t && t.TryGetProperty("result", out JsonElement toolsResult)
                    && JsonNode.DeepEquals(JsonNode.Parse(toolsResult.GetRawText()), JsonNode.Parse(liveTools.GetRawText())),
                    "With ARS closed at startup the bridge lists exactly the router's tools");
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":122,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? down = Answer(bridge, 122, seen);
                Check(down is { } d && LiveRouter.Failed(d) && LiveRouter.Text(d) == BridgeTexts.Restarting && !bridge.HasExited,
                    "While ARS is closed a tool call gets the retry text and the bridge does not exit");
                WorkspaceRouter.Start();
                int before = seen.Count;
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":123,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? up = Answer(bridge, 123, seen);
                Check(up is { } u && !LiveRouter.Failed(u) && LiveRouter.Text(u).StartsWith("No workspace yet", StringComparison.Ordinal)
                    && seen.Skip(before).Any(line => line.Contains("notifications/tools/list_changed", StringComparison.Ordinal)),
                    "When ARS comes back the next call succeeds and the bridge announces the real tool list");
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000), "A bridge started while ARS was closed exits on client EOF");
            }
            Check(seen.Count > 0 && !seen.Any(line => line.Contains("Carry on with your own tools", StringComparison.Ordinal)),
                "No bridge reply invites the agent to carry on with its own tools");
            string cached = WorkspaceBridgeHost.Materialize(Path.GetDirectoryName(WorkspaceConnections.PackagedBridge)!, Path.Combine(output, "cache"));
            Check(File.Exists(cached) && !cached.StartsWith(Path.GetDirectoryName(WorkspaceConnections.PackagedBridge)!, StringComparison.OrdinalIgnoreCase),
                "Content-addressed bridge host resides outside the replaceable payload");
            Check(WorkspaceBridgeHost.Materialize(Path.GetDirectoryName(WorkspaceConnections.PackagedBridge)!, Path.Combine(output, "cache")) == cached,
                "Independent host readback reuses the identical verified runtime");
            using (var bridge = Start(WorkspaceAccessStore.RouterTicket, cached))
            {
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}");
                Check(Reply(bridge, 5), "Detached cached bridge serves its MCP client");
                using (File.Open(Path.ChangeExtension(WorkspaceConnections.PackagedBridge, ".dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                    Check(true, "A live detached bridge does not lock the install bridge assembly");
                // A restart, crash or update: the router goes without a quit mark.
                WorkspaceRouter.Stop();
                Thread.Sleep(500);
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? down = Answer(bridge, 6, seen);
                Check(down is { } d && LiveRouter.Failed(d) && LiveRouter.Text(d) == BridgeTexts.Restarting && !bridge.HasExited,
                    "While ARS restarts a tool call gets the retry text and the detached bridge stays up");
                WorkspaceRouter.Start();
                Send(bridge, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
                JsonElement? up = Answer(bridge, 7, seen);
                Check(up is { } u && !LiveRouter.Failed(u) && LiveRouter.Text(u).StartsWith("No workspace yet", StringComparison.Ordinal)
                    && !bridge.HasExited,
                    "The same detached bridge reconnects to the controller's replacement ticket on the next call");
                bridge.StandardInput.Close();
                Check(bridge.WaitForExit(2000), "Detached bridge also exits promptly on client EOF");
            }
            Check(WorkspaceRuntime.BeginMaintenance(), "Empty controller enters maintenance atomically");
            StoredWorkspace workspace = WorkspaceStore.Create("maintenance fixture");
            bool refused = false;
            try { WorkspaceRuntime.Start(workspace); } catch (InvalidOperationException) { refused = true; }
            Check(refused && !WorkspaceRuntime.AnyRunning, "A racing workspace cannot start during maintenance");
            WorkspaceRuntime.CancelMaintenance();
            WorkspaceRouter.Stop();
            LiveRouter.Run(CheckRouter, output);
            void CheckRouter(bool passed, string name) => Check(passed, name);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { WorkspaceRuntime.CancelMaintenance(); WorkspaceAccessStore.ClearQuit(); WorkspaceRouter.Stop(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        { status = failure is null ? "passed" : "failed", claims, failure, engine = typeof(WorkspaceRuntime).Assembly.Location, bridge = WorkspaceConnections.Bridge },
            new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    static Process Start(string ticket, string? executable = null)
    {
        var start = new ProcessStartInfo(executable ?? WorkspaceConnections.Bridge)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(ticket);
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = new UTF8Encoding(false);
        return Process.Start(start)!;
    }
    static void Send(Process process, string json)
    {
        process.StandardInput.WriteLine(json);
        process.StandardInput.Flush();
    }
    /// <summary>The reply to one id, reading past notifications and other replies (kept in seen and, when
    /// given, in earlier), or null when none comes within ten seconds or the bridge closes.</summary>
    static JsonElement? Answer(Process process, int id, List<string> seen, Dictionary<int, JsonElement>? earlier = null)
    {
        if (earlier is not null && earlier.TryGetValue(id, out JsonElement known)) return known;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                string? line = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line is null) return null;
                seen.Add(line);
                using var reply = JsonDocument.Parse(line);
                if (!reply.RootElement.TryGetProperty("id", out JsonElement got) || got.ValueKind != JsonValueKind.Number) continue;
                if (got.GetInt32() == id) return reply.RootElement.Clone();
                earlier?.TryAdd(got.GetInt32(), reply.RootElement.Clone());
            }
        }
        catch (Exception error) when (error is OperationCanceledException or JsonException) { return null; }
    }
    static bool Reply(Process process, int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            string? line = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (line is null) return false;
            using var reply = JsonDocument.Parse(line);
            return reply.RootElement.GetProperty("id").GetInt32() == id && reply.RootElement.TryGetProperty("result", out _);
        }
        catch (Exception error) when (error is OperationCanceledException or JsonException or KeyNotFoundException) { return false; }
    }
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int handle);
}
