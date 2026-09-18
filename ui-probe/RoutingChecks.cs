using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

internal static class RoutingChecks
{
    internal static async Task Run(string output)
    {
        string project = Path.Combine(output, "route-projects", "Shop");
        string other = Path.Combine(output, "route-projects", "Trader");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(other, ".git"));
        WorkspaceRouter.Start();
        var before = WorkspaceRuntime.Running.Select(r => r.Id).ToHashSet();
        try
        {
            using var claude = new Session(Path.Combine(project, "src"));
            using var codex = new Session(project);
            await claude.Hello("claude-code");
            await codex.Hello("codex-mcp-client");
            int records = WorkspaceStore.All().Count;
            await claude.Call("tools/list", new { });
            await claude.Tool("status");
            Program.Check(WorkspaceStore.All().Count == records,
                "A project handshake, tool list and status create no workspace");
            await claude.Tool("acquire");
            string id = await claude.Workspace();
            WorkspaceRuntime runtime = WorkspaceRuntime.Of(id)!;
            Task<JsonElement> waiting = codex.Tool("acquire");
            await Task.Delay(200);
            Program.Check(!waiting.IsCompleted, "A second real project session waits while the first controls its screen");
            await claude.Tool("release");
            await waiting;
            Program.Check(await codex.Workspace() == id && WorkspaceStore.All().Count == records + 1
                && ReferenceEquals(runtime, WorkspaceRuntime.Of(id)),
                "Claude in a subfolder and Codex at the root share one project record and the same desktop");
            JsonElement here = await codex.Tool("run", new { command = "cd", seconds = 20 });
            Program.Check(here.GetProperty("content")[0].GetProperty("text").GetString()!
                    .Contains(JsonSerializer.Serialize(project).Trim('"'), StringComparison.OrdinalIgnoreCase),
                "A connected agent's workspace command starts in that agent's own project folder");
            JsonElement picture = await codex.Tool("computer", new { screenshot = true });
            Program.Check(picture.GetProperty("content").EnumerateArray().Any(c => c.GetProperty("type").GetString() == "image"),
                "The routed project returns an actual desktop screenshot");
            await codex.Tool("release");
            runtime.Dispose();
            await claude.Tool("acquire");
            Program.Check(await claude.Workspace() == id && WorkspaceStore.All().Count == records + 1,
                "A later tool call restarts the same stopped project workspace without another record");
            await claude.Tool("release");
            WorkspaceRuntime.Of(id)!.Dispose();

            using var trader = new Session(other);
            await trader.Hello("codex-mcp-client");
            await trader.Tool("acquire");
            string traderId = await trader.Workspace();
            Program.Check(traderId != id && WorkspaceStore.Find(traderId)?.Name == "Trader",
                "A different project gets a different automatically named workspace");
            await trader.Tool("release");
            WorkspaceRuntime.Of(traderId)!.Dispose();

            using var home = new Session(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await home.Hello("claude-code");
            await home.Tool("acquire");
            string scratch = await home.Workspace();
            await home.Tool("release");
            using var desktop = new Session(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            await desktop.Hello("codex-mcp-client");
            await desktop.Tool("acquire");
            Program.Check(await desktop.Workspace() == scratch && WorkspaceStore.Find(scratch)?.Agents == WorkspaceHome.Scratch
                && WorkspaceStore.All().Count(w => w.Agents == WorkspaceHome.Scratch) == 1,
                "Real sessions from home and Desktop reuse the one Scratch workspace");

            // Sleep (MVP_SPEC): a quiet agent lets go, a quiet workspace sleeps, the next call wakes it.
            WorkspaceExternalAccess.IdleHandoverMs = 1000;
            await desktop.Tool("computer", new { screenshot = true });
            WorkspaceRuntime sleeper = WorkspaceRuntime.Of(scratch)!;
            await Task.Delay(2500);
            Program.Check(sleeper.Access?.HasDriver == false && sleeper.Quiet is not null,
                "An agent that goes quiet without releasing lets go of its workspace by itself");
            Program.Check(WorkspaceHome.DisplayName(sleeper.Access!.LastController) == "Codex",
                "The corner and the hub still name the agent after it lets go");
            WorkspaceRuntime.SleepAfter = TimeSpan.FromMilliseconds(500);
            WorkspaceRuntime.Doze();
            Program.Check(WorkspaceRuntime.Of(scratch) is null && File.Exists(WorkspaceStore.LastFrameOf(scratch))
                && DateTimeOffset.Now - WorkspaceStore.Find(scratch)!.LastUsed < TimeSpan.FromMinutes(1),
                "A quiet workspace sleeps, keeping its last picture and when it was last used");
            await desktop.Tool("acquire");
            Program.Check(await desktop.Workspace() == scratch && WorkspaceRuntime.Of(scratch) is not null,
                "The same agent session's next call wakes the sleeping workspace");

            await desktop.Tool("release");
            int cap = WorkspaceRuntime.Running.Count;
            WorkspaceRouter.MaxRunning = cap;
            await trader.Tool("acquire");
            Program.Check(await trader.Workspace() == traderId && WorkspaceRuntime.Running.Count == cap,
                "At the PC's limit a new project's call puts the quietest workspace to sleep instead of failing");
            await trader.Tool("release");
        }
        finally
        {
            WorkspaceExternalAccess.IdleHandoverMs = 30_000;
            WorkspaceRuntime.SleepAfter = TimeSpan.FromMinutes(30);
            WorkspaceRouter.MaxRunning = (int)Math.Clamp(WorkspaceLimits.PhysicalMemory() / (3UL << 30), 1, 10);
            WorkspaceRouter.Stop();
            foreach (var runtime in WorkspaceRuntime.Running.Where(r => !before.Contains(r.Id)).ToArray()) runtime.Dispose();
        }
    }

    sealed class Session : IDisposable
    {
        readonly Process _bridge;
        readonly Task<string> _errors;
        int _sequence;
        internal Session(string folder)
        {
            var start = new ProcessStartInfo(WorkspaceConnections.Bridge)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment.Remove("CLAUDE_PROJECT_DIR");
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(WorkspaceAccessStore.RouterTicket);
            _bridge = Process.Start(start)!;
            _errors = _bridge.StandardError.ReadToEndAsync();
        }
        internal async Task Hello(string name)
        {
            await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name, version = "1" } });
            await _bridge.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await _bridge.StandardInput.FlushAsync();
        }
        internal Task<JsonElement> Tool(string name, object? arguments = null) =>
            Call("tools/call", new { name, arguments = arguments ?? new { } });
        internal async Task<string> Workspace()
        {
            JsonElement status = await Tool("status");
            using var state = JsonDocument.Parse(status.GetProperty("content")[0].GetProperty("text").GetString()!);
            return state.RootElement.GetProperty("workspace").GetString()!;
        }
        internal async Task<JsonElement> Call(string method, object parameters)
        {
            int id = ++_sequence;
            await _bridge.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _bridge.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (true)
            {
                string line = await _bridge.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new IOException("Bridge closed: " + await _errors);
                using var reply = JsonDocument.Parse(line);
                if (!reply.RootElement.TryGetProperty("id", out var got) || got.GetInt32() != id) continue;
                JsonElement result = reply.RootElement.GetProperty("result");
                if (result.TryGetProperty("isError", out var error) && error.GetBoolean()) throw new IOException(result.ToString());
                return result.Clone();
            }
        }
        public void Dispose()
        {
            _bridge.StandardInput.Close();
            if (!_bridge.WaitForExit(3000)) _bridge.Kill(true);
            _bridge.Dispose();
        }
    }
}
