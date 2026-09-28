using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Work that would never have put a window on the owner's desktop puts none on the workspace either,
/// so the corner window has no reason to come up for it: a run command gets no console window and
/// still hands back its output and exit code, and only a real window counts as something to show.
///
/// Everything happens on a workspace desktop of its own. No model is called.
///
/// Run: Deskweave.Probe.exe --quiet-work "C:\absolute\output"
/// </summary>
internal static class QuietWork
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "qw-" + Guid.NewGuid().ToString("N")[..8]);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(fixture, "settings.json"));
        using var watchdog = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Quiet-work probe exceeded 90 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        string? failure = null;
        try { Run(Check); }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { WorkspaceRuntime.Rest(); }
            catch (Exception ex) { failure ??= "Cleanup: " + ex; }
        }
        File.WriteAllText(Path.Combine(output, "quiet-work.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), fixture, claims, failure, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException("Failed: " + claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    /// <summary>The checks alone, inside a store the caller already scoped.</summary>
    internal static void Run(Action<bool, string> check)
    {
        StoredWorkspace workspace = WorkspaceStore.Create("Quiet " + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false });
        using WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        WorkspaceControl plane = runtime.Plane!;
        check(plane.AgentTakes(), "The agent holds its workspace");
        check(plane.HasSomethingToShow() == false, "An empty workspace has nothing to show");

        using var commands = new WorkspaceCommands(plane);
        CommandJob job = commands.Start("echo quiet-work& ping -n 4 127.0.0.1 >nul& exit /b 7", false, 0, out string? error)
            ?? throw new InvalidOperationException("run did not start: " + error);
        bool console = false, shown = false;
        while (job.Running && job.Seconds < 20)
        {
            console |= plane.Windows().Any(w => w.ClassName is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS");
            shown |= plane.HasSomethingToShow() == true;
            Thread.Sleep(100);
        }
        job.Finished.Task.Wait(TimeSpan.FromSeconds(10));
        check(!console, "A run command opens no console window on the workspace");
        check(!shown, "A run command that opens nothing leaves nothing to show");
        check(job.ExitCode == 7, "A windowless run command keeps its real exit code");
        check(commands.Read(job, 0, 4096).Text.Contains("quiet-work", StringComparison.Ordinal),
            "A windowless run command still hands back what it printed");

        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        commands.Start("start \"\" \"" + notepad + "\"", false, 0, out error);
        long until = Environment.TickCount64 + 15000;
        while (plane.HasSomethingToShow() != true && Environment.TickCount64 < until) Thread.Sleep(100);
        check(plane.HasSomethingToShow() == true, "A window a run command opens lands on the workspace and is something to show");
    }
}
