using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using Deskweave.AgentWorkspaces;

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
            using (var bridge = Start(WorkspaceAccessStore.RouterTicket))
            {
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
                Check(Reply(bridge, 3), "Live bridge answers before normal quit");
                var elapsed = Stopwatch.StartNew();
                WorkspaceAccessStore.MarkQuit();
                Check(bridge.WaitForExit(2000) && bridge.ExitCode == 0, "Normal quit leaves no attached bridge even with stdin open", elapsed.Elapsed.TotalMilliseconds);
                WorkspaceAccessStore.ClearQuit();
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
                Check(bridge.WaitForExit(2000), "Normal quit also closes a per-workspace bridge with stdin open");
                WorkspaceAccessStore.ClearQuit();
            }
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
                WorkspaceRouter.Stop(); WorkspaceRouter.Start();
                bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"ping\"}");
                bool reconnected = Reply(bridge, 6);
                if (!reconnected)
                {
                    Thread.Sleep(200); // A broken connection may answer the first safe ping with an error.
                    bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
                    reconnected = Reply(bridge, 7);
                }
                Check(reconnected && !bridge.HasExited,
                    "The same detached bridge reconnects to the controller's replacement ticket");
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
        return Process.Start(start)!;
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
