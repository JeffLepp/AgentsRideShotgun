using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

// No Window is constructed or shown. The only console child is on an alternate workspace desktop.
internal static class RouterStartup
{
    static readonly object Hello = new { protocolVersion = "2025-06-18", capabilities = new { },
        clientInfo = new { name = "Router startup regression", version = "1" } };

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(output, "store"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(output, "settings.json"));
        using var watchdog = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Router startup probe exceeded 60 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        object? owned = null;
        string? failure = null;
        double coldMilliseconds = 0, refusedMilliseconds = 0;
        using var ui = new FixtureDispatcher();
        try
        {
            ui.Call(WorkspaceRouter.Start);
            Check(!WorkspaceRuntime.AnyRunning && WorkspaceRouter.Pipe is not null,
                "The fixture router is listening before any workspace or application window exists");
            using (var blocked = ui.Block())
            {
                var watch = Stopwatch.StartNew();
                using var bridge = new Bridge(WorkspaceAccessStore.RouterTicket);
                JsonElement hello = bridge.Request("initialize", Hello);
                JsonElement tools = bridge.Request("tools/list", new { });
                coldMilliseconds = watch.Elapsed.TotalMilliseconds;
                Check(hello.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "deskweave"
                    && tools.GetProperty("result").GetProperty("tools").GetArrayLength() == WorkspaceMcp.ExternalToolSchemas.Length,
                    "A real bridge initializes and lists tools while the fixture WPF dispatcher is blocked");
                Check(blocked.StillBlocked && coldMilliseconds < 4000 && !WorkspaceRuntime.AnyRunning,
                    "Metadata answers before the blocked dispatcher is released, without starting a workspace");
            }

            StoredWorkspace record = WorkspaceStore.Create("Router admission fixture");
            WorkspaceAccessStore.Write(record.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
            WorkspaceRuntime? runtime = null;
            ui.Call(() => runtime = WorkspaceRuntime.Start(record));
            Check(runtime?.Computer is not null, "An isolated workspace exists for occupied-registry ownership checks");
            using (var blocked = ui.Block())
            {
                var watch = Stopwatch.StartNew();
                bool rejected = WorkspaceRouter.InsideAWorkspace(Environment.ProcessId);
                refusedMilliseconds = watch.Elapsed.TotalMilliseconds;
                Check(rejected && refusedMilliseconds < 2000 && blocked.StillBlocked,
                    "A busy occupied-registry dispatcher refuses admission within its bound instead of skipping ownership");
                using var bridge = new Bridge(WorkspaceAccessStore.RouterTicket);
                Check(bridge.WaitForRefusal() && blocked.StillBlocked,
                    "A real bridge fails closed while ownership cannot be checked on the blocked dispatcher");
            }
            using (var bridge = new Bridge(WorkspaceAccessStore.RouterTicket))
                Check(bridge.Request("initialize", Hello).TryGetProperty("result", out _),
                    "A fresh external bridge initializes once the occupied dispatcher is responsive again");

            string childReport = Path.Combine(output, "owned-client.json");
            int child = runtime!.Computer!.Launch(Environment.ProcessPath!, "--router-owned-client "
                + Quote(WorkspaceAccessStore.RouterTicket) + " " + Quote(childReport));
            Check(child > 0 && runtime.Computer.OwnsProcess(child),
                "The rejection fixture is an actual child owned by the workspace job");
            Check(Wait(() => Complete(childReport), 10000), "The workspace-owned bridge wrote its refusal receipt");
            using (var receipt = JsonDocument.Parse(File.ReadAllText(childReport)))
            {
                owned = receipt.RootElement.Clone();
                Check(receipt.RootElement.GetProperty("refused").GetBoolean()
                    && receipt.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null,
                    "A bridge launched by a workspace-owned child remains rejected with the UI responsive");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { ui.Call(() => { WorkspaceRouter.Stop(); WorkspaceRuntime.Rest(); }); }
            catch (Exception ex) { failure ??= "Cleanup: " + ex; }
        }
        string engine = typeof(WorkspaceRuntime).Assembly.Location;
        File.WriteAllText(Path.Combine(output, "router-startup.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            engine, engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine))),
            claims, failure, coldMilliseconds, refusedMilliseconds, owned,
            globalInputEventsSent = 0, ownerWindowsCreated = 0, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException(claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    internal static int OwnedClient(string ticket, string output)
    {
        bool refused = false;
        string? error = null;
        int bridgePid = 0;
        try
        {
            using var bridge = new Bridge(ticket);
            bridgePid = bridge.Id;
            refused = bridge.WaitForRefusal();
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(output, JsonSerializer.Serialize(new { pid = Environment.ProcessId, bridgePid, refused, error }));
        return refused && error is null ? 0 : 1;
    }

    static string Quote(string value) => "\"" + value + "\"";
    static bool Complete(string path)
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); return true; }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }
    static bool Wait(Func<bool> condition, int milliseconds)
    {
        long until = Environment.TickCount64 + milliseconds;
        do { if (condition()) return true; Thread.Sleep(25); } while (Environment.TickCount64 < until);
        return false;
    }

    sealed class FixtureDispatcher : IDisposable
    {
        readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Thread _thread;
        readonly Dispatcher _dispatcher;
        internal FixtureDispatcher()
        {
            _thread = new Thread(() =>
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                _ready.SetResult(app.Dispatcher);
                Dispatcher.Run();
            }) { IsBackground = true, Name = "Router fixture UI" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _dispatcher = _ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        internal void Call(Action action) => _dispatcher.Invoke(action, DispatcherPriority.Send,
            CancellationToken.None, TimeSpan.FromSeconds(15));
        internal Blocked Block() => new(_dispatcher);
        public void Dispose()
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    sealed class Blocked : IDisposable
    {
        readonly ManualResetEventSlim _entered = new();
        readonly ManualResetEventSlim _release = new();
        readonly ManualResetEventSlim _done = new();
        internal bool StillBlocked => _entered.IsSet && !_done.IsSet;
        internal Blocked(Dispatcher dispatcher)
        {
            dispatcher.BeginInvoke(() =>
            {
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
                _done.Set();
            });
            if (!_entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Fixture dispatcher did not enter its blocked section.");
        }
        public void Dispose()
        {
            _release.Set();
            if (!_done.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Fixture dispatcher did not leave its blocked section.");
            _entered.Dispose(); _release.Dispose(); _done.Dispose();
        }
    }

    sealed class Bridge : IDisposable
    {
        readonly Process _process;
        readonly Task<string> _errors;
        int _id;
        internal int Id => _process.Id;
        internal Bridge(string ticket)
        {
            var start = new ProcessStartInfo(WorkspaceConnections.Bridge) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(ticket)! };
            start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(ticket);
            _process = Process.Start(start) ?? throw new IOException("Fixture bridge did not start.");
            _errors = _process.StandardError.ReadToEndAsync();
        }
        internal JsonElement Request(string method, object parameters)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++_id, method, @params = parameters }));
            _process.StandardInput.Flush();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            string line = _process.StandardOutput.ReadLineAsync(bound.Token).AsTask().GetAwaiter().GetResult()
                ?? throw new IOException("Fixture bridge closed before its response.");
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }
        internal bool WaitForRefusal()
        {
            if (!_process.WaitForExit(5000)) return false;
            string errors = _errors.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            string stdout = _process.StandardOutput.ReadToEnd();
            return _process.ExitCode == 1 && stdout.Length == 0
                && errors.StartsWith("Deskweave isn't answering.", StringComparison.Ordinal);
        }
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
    }
}
