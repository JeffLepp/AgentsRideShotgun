using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--grandchild")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { pid = Environment.ProcessId, desktop = DesktopName() }));
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--child")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--grandchild");
            start.ArgumentList.Add(args[1] + ".grandchild");
            using var grandchild = Process.Start(start)!;
            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                desktop = DesktopName(),
                grandchild = grandchild.Id,
                currentDirectory = Environment.CurrentDirectory,
            }));
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
        }
        // Standing in for claude.exe / codex.exe so the connection checks never run the owner's
        // real agent command. Writes only inside the isolated root the caller names in the
        // environment, in each app's own format, and starts no model.
        if (args.Length > 0 && args[0] == "mcp") return FirstRunConnections.Cli(args);
        // ponytail: one probe at a time on this PC (see ui-probe); child modes above never wait for it.
        using var turn = new Mutex(false, @"Local\Deskweave.Probe.Turn");
        try { turn.WaitOne(); } catch (AbandonedMutexException) { }
        if (args.Length == 2 && args[0] == "--capture-spike" && Path.IsPathFullyQualified(args[1]))
            return CaptureSpike.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--corner-rate" && Path.IsPathFullyQualified(args[1]))
            return CornerRate.Run(Path.GetFullPath(args[1]));
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        return Run(Path.GetFullPath(args[0]));
    }

    static int Run(string output)
    {
        Directory.CreateDirectory(output);
        // Chrome creates deep profile subdirectories. Keep the fixture beside the report folder
        // within artifacts rather than nesting another long identity beneath its descriptive name.
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "f-" + Guid.NewGuid().ToString("N")[..8]);
        var claims = new List<string>();
        var observedProcesses = new List<int>();
        string? failure = null;
        string module = typeof(WorkspaceRuntime).Assembly.Location;
        string bridge = WorkspaceConnections.Bridge;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Engine probe exceeded its 240-second wall-clock bound.");
            Environment.Exit(2); // Owned workspace jobs close with this process.
        }, null, TimeSpan.FromSeconds(240), Timeout.InfiniteTimeSpan);
        try
        {
            Check(ProductContext.FolderName == "Deskweave", "Copied product context defaults to Deskweave");
            Check(Path.GetFileName(ProductContext.LocalRoot) == "Deskweave"
                && Path.GetFileName(ProductContext.RoamingRoot) == "Deskweave", "Local and roaming product roots are independent of HiveMind");
            Check(WorkspaceStore.Root == ProductContext.Local("agent-workspaces"), "Production workspace root is owned by Deskweave");
            Check(File.Exists(bridge) && Path.GetFileName(bridge) == "Deskweave.WorkspaceBridge.exe", "Deskweave bridge is packaged beside the copied engine");
            string originalName = "HiveMind-";
            Check(AgentDesktop.NameFor("probe") == "Deskweave-probe"
                && !AgentDesktop.NameFor("probe").StartsWith(originalName), "Desktop namespace is independent of HiveMind");
            using var scope = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
            Check(WorkspaceAccessStore.Root == WorkspaceStore.Root + ".access"
                && WorkspaceAccessStore.ResultsRoot == WorkspaceStore.Root + ".results", "Fixture scopes workspace, access, and result stores together");
            string nonce = Guid.NewGuid().ToString("N")[..8];
            StoredWorkspace first = WorkspaceStore.Create("A" + nonce);
            StoredWorkspace second = WorkspaceStore.Create("B" + nonce);
            // Refusals below are checked for their outcome, not for sitting out the full wait each time.
            WorkspaceExternalAccess.AcquireWait = TimeSpan.FromSeconds(1);
            var policy = new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false };
            WorkspaceAccessStore.Write(first.Id, policy);
            WorkspaceAccessStore.Write(second.Id, policy with { Enabled = false });
            string firstFolder = WorkspaceStore.FolderOf(first.Id);
            string secondFolder = WorkspaceStore.FolderOf(second.Id);
            Check(first.Id != second.Id && firstFolder != secondFolder, "Two fixture workspaces have separate persistent identities and folders");
            Task.WaitAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                WorkspaceStore.Update(first.Id, current => current with { UsageRuns = current.UsageRuns + 1 }))).ToArray());
            first = WorkspaceStore.All().Single(workspace => workspace.Id == first.Id);
            Check(first.UsageRuns == 32, "Concurrent workspace updates retain all 32 independent counter increments");
            string config = JsonSerializer.Serialize(WorkspaceConnections.Configuration(first.Id));
            Check(WorkspaceConnections.Name(first.Id) == "deskweave_workspace_" + first.Id
                && config.Contains("Deskweave.WorkspaceBridge.exe", StringComparison.Ordinal)
                && config.Contains("--workspace", StringComparison.Ordinal), "Generated MCP connection names the Deskweave bridge and its workspace ticket");

            WorkspaceRuntime one = WorkspaceRuntime.Start(first);
            WorkspaceRuntime two = WorkspaceRuntime.Start(second);
            Check(ReferenceEquals(one, WorkspaceRuntime.Start(first)) && WorkspaceRuntime.Running.Count == 2,
                "Reopening a workspace returns its existing runtime without duplicating a desktop");
            Check(one.Computer!.Name != two.Computer!.Name && one.Computer.Folder == firstFolder
                && two.Computer.Folder == secondFolder, "Real runtimes own distinct Windows desktops and correct folders");
            Check(!one.Plane!.BrowserAlive && !two.Plane!.BrowserAlive, "Browser prewarm is disabled for these no-network fixtures");
            Check(!File.Exists(WorkspaceAccessStore.Connection(second.Id)), "Agent access remains off for the second workspace");
            using (var ticket = JsonDocument.Parse(File.ReadAllText(WorkspaceAccessStore.Connection(first.Id))))
                Check(ticket.RootElement.GetProperty("pipe").GetString()!.StartsWith("Deskweave.Workspace.", StringComparison.Ordinal),
                    "Enabled access publishes a Deskweave named-pipe ticket");

            string childResult = Path.Combine(firstFolder, "owned-child.json");
            string childResultTwo = Path.Combine(secondFolder, "owned-child.json");
            int childOne = one.Computer.Launch(Environment.ProcessPath!, "--child " + Arg(childResult));
            int childTwo = two.Computer.Launch(Environment.ProcessPath!, "--child " + Arg(childResultTwo));
            Check(childOne > 0 && childTwo > 0, "Both desktops launch native fixture processes");
            WaitFor(() => CompleteJson(childResult) && CompleteJson(childResultTwo)
                && CompleteJson(childResult + ".grandchild") && CompleteJson(childResultTwo + ".grandchild"),
                "Fixture children and grandchildren report their Windows desktops");
            using (var a = JsonDocument.Parse(File.ReadAllText(childResult)))
            using (var b = JsonDocument.Parse(File.ReadAllText(childResultTwo)))
            using (var ag = JsonDocument.Parse(File.ReadAllText(childResult + ".grandchild")))
            using (var bg = JsonDocument.Parse(File.ReadAllText(childResultTwo + ".grandchild")))
            {
                Check(a.RootElement.GetProperty("desktop").GetString() == one.Computer.Name
                    && ag.RootElement.GetProperty("desktop").GetString() == one.Computer.Name
                    && b.RootElement.GetProperty("desktop").GetString() == two.Computer.Name
                    && bg.RootElement.GetProperty("desktop").GetString() == two.Computer.Name,
                    "Independent child and grandchild Win32 oracles confirm the exact intended desktop");
                observedProcesses.AddRange([childOne, childTwo, a.RootElement.GetProperty("grandchild").GetInt32(), b.RootElement.GetProperty("grandchild").GetInt32()]);
                Check(observedProcesses.All(pid => one.Computer.OwnsProcess(pid) != two.Computer.OwnsProcess(pid)),
                    "Each observed process belongs to exactly one workspace job");
            }

            using (var client = new Client(first.Id))
            using (var competitor = new Client(first.Id))
            {
                Check(client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Deskweave engine probe", version = "1" } }).TryGetProperty("result", out _),
                    "Packaged Deskweave bridge initializes as standard MCP");
                competitor.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Competing probe", version = "1" } });
                string file = Path.Combine(firstFolder, "retained-output.txt");
                Check(!Client.Failed(client.Tool("save", new { path = file, text = "taken" })) && File.ReadAllText(file) == "taken",
                    "An external client's first action takes control by itself");
                Check(!Client.Failed(client.Tool("acquire")), "External client holds its workspace lease");
                Check(Client.Failed(competitor.Tool("acquire")), "A second MCP client cannot acquire the same workspace concurrently");
                Check(Client.Failed(competitor.Tool("save", new { path = file, text = "competing" })) && File.ReadAllText(file) == "taken",
                    "A second MCP client cannot write while the first holds control");
                const string expected = "Deskweave native command result";
                var command = client.Tool("run", new
                {
                    command = "[IO.File]::WriteAllText('" + file.Replace("'", "''") + "', '" + expected + "'); Write-Output 'native-command-complete'",
                    shell = "powershell", seconds = 20,
                });
                Check(!Client.Failed(command) && Client.Text(command).Contains("native-command-complete", StringComparison.Ordinal)
                    && File.ReadAllText(file) == expected, "Real workspace PowerShell execution has a matching independent final-file oracle");
                Check(Client.Text(client.Tool("file", new { path = file })).Contains(expected, StringComparison.Ordinal),
                    "MCP file readback agrees with the filesystem oracle");
                BrowserProbe.Run(one, client, Check, output, observedProcesses);
                one.Plane.OwnerTakes();
                Check(Client.Failed(client.Tool("save", new { path = file, text = "must not overwrite" })) && File.ReadAllText(file) == expected,
                    "Owner takeover revokes external writes without replay or file modification");
                Check(Client.Failed(client.Tool("acquire")), "External client cannot reacquire while the owner holds control");
                one.Access!.Configure(policy with { Enabled = false });
                Check(!File.Exists(WorkspaceAccessStore.Connection(first.Id)), "Turning access off withdraws its connection ticket");
            }

            WorkspaceRuntime.Rest();
            Check(!WorkspaceRuntime.AnyRunning && WorkspaceRuntime.Running.Count == 0,
                "App shutdown removes both live runtime registrations");
            WaitFor(() => observedProcesses.All(Exited), "Closing workspace jobs terminates every observed child and grandchild");
            Check(File.ReadAllText(Path.Combine(firstFolder, "retained-output.txt")) == "Deskweave native command result"
                && WorkspaceStore.All().Count == 2, "Shutdown retains the resulting file and both stored workspace records");
            WorkspaceRuntime restarted = WorkspaceRuntime.Start(WorkspaceStore.All().Single(w => w.Id == first.Id));
            Check(restarted.Computer!.Folder == firstFolder && File.Exists(Path.Combine(firstFolder, "retained-output.txt")),
                "Restart opens the same saved workspace folder with the result intact");
            WorkspaceRuntime.Rest();
            FirstRunConnections.Run(Path.Combine(fixture, "agents"), Check);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { WorkspaceRuntime.Rest(); }
            catch (Exception ex) { failure ??= "Cleanup: " + ex; }
        }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed",
            observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(),
            modulePath = module,
            moduleSha256 = Hash(module),
            bridgePath = bridge,
            bridgeSha256 = Hash(bridge),
            bridgeManagedSha256 = Hash(Path.ChangeExtension(bridge, ".dll")),
            modelCalls = 0,
            browserNavigationScope = "Local file fixtures only; no provider or remote website requested. Browser background traffic was not measured.",
            globalInputEventsSent = 0,
            fixture,
            observedProcesses,
            claims,
            failure,
            limits = "This PC only; local browser fixtures, no downloads/provider mission, foreground interaction, public package, or broad Windows compatibility certification.",
        }, Json));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException("Failed: " + claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
        void WaitFor(Func<bool> predicate, string claim)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(50);
            Check(predicate(), claim);
        }
    }

    static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    static bool CompleteJson(string path)
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); return true; }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }
    static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    static string Arg(string path) => "\"" + path + "\"";
    static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _))
            throw new InvalidOperationException("Could not read the current thread desktop.");
        return name.ToString();
    }
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);

    internal sealed class Client : IDisposable
    {
        readonly Process _process;
        readonly Task<string> _errors;
        int _sequence;
        internal Client(string id)
        {
            var start = new ProcessStartInfo(WorkspaceConnections.Bridge) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(WorkspaceAccessStore.Connection(id));
            _process = Process.Start(start) ?? throw new IOException("Bridge failed to start.");
            _errors = _process.StandardError.ReadToEndAsync();
        }
        internal JsonElement Request(string method, object parameters)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++_sequence, method, @params = parameters }));
            _process.StandardInput.Flush();
            string reply = _process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()
                ?? throw new IOException("Bridge disconnected before replying.");
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.GetProperty("id").GetInt32() != _sequence) throw new IOException("Mismatched MCP response.");
            return json.RootElement.Clone();
        }
        internal JsonElement Tool(string name, object? args = null) => Request("tools/call", new { name, arguments = args ?? new { } });
        internal static bool Failed(JsonElement reply) => reply.TryGetProperty("error", out _)
            || reply.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
        internal static string Text(JsonElement reply) => reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
            }
            _process.WaitForExit(3000);
            _process.Dispose();
            _ = _errors.GetAwaiter().GetResult();
        }
    }
}
