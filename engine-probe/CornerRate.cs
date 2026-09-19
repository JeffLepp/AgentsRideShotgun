using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

/// <summary>
/// How fast and how expensive the corner view's own capture loop (<see cref="WorkspacePeekCapture"/>)
/// really is, paced at the same rate <see cref="WorkspacePeekHost"/> uses for Balanced and Battery saver,
/// idle and in use, against one real workspace with real windows on its desktop
/// (design/WAVE1B.md, Corner window item 6). Three 15-second samples per case.
/// Run: Deskweave.Probe.exe --corner-rate "C:\absolute\output"
/// </summary>
internal static class CornerRate
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "r-" + Guid.NewGuid().ToString("N")[..8]);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Corner rate probe exceeded its 450-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(450), Timeout.InfiniteTimeSpan);
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        StoredWorkspace workspace = WorkspaceStore.Create("Rate");
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false });
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        var result = new Dictionary<string, object?>();
        try
        {
            AgentDesktop desktop = runtime.Computer!;
            string system = Environment.SystemDirectory;
            // Two ordinary windows, one of them never idle, so a real front-window capture has real
            // (small) content changes to draw - the same kind of desktop the corner view watches.
            desktop.Launch(Path.Combine(system, "notepad.exe"));
            desktop.Launch(Path.Combine(system, "cmd.exe"), "/k ping -t 127.0.0.1");
            var waited = Stopwatch.StartNew();
            while (desktop.Windows().Count < 2 && waited.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);
            Thread.Sleep(500);
            result["screen"] = $"{AgentDesktop.ScreenWidth}x{AgentDesktop.ScreenHeight}";
            result["windows"] = desktop.Windows().Select(w => new { w.Title, w.ClassName, w.Width, w.Height }).ToArray();

            foreach (PreviewSmoothness smoothness in new[] { PreviewSmoothness.Balanced, PreviewSmoothness.BatterySaver })
            {
                foreach (bool inUse in new[] { false, true })
                {
                    RateSample[] runs = Enumerable.Range(0, 3).Select(_ => Measure(desktop, smoothness, inUse)).ToArray();
                    result[smoothness + (inUse ? "InUse" : "Idle")] = new
                    {
                        median = runs.OrderBy(r => r.Fps).ElementAt(1),
                        runs,
                    };
                }
            }
        }
        catch (Exception failure) { result["failure"] = failure.ToString(); }
        finally { runtime.Dispose(); }
        File.WriteAllText(Path.Combine(output, "corner-rate.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.ContainsKey("failure") ? 1 : 0;
    }

    /// <summary>Fifteen seconds of the exact loop WorkspacePeekHost's own timer runs, paced to the real
    /// interval for this smoothness and mode, reporting the fps it actually reaches and what it costs:
    /// this process (the capture and its pixel copy) and the workspace's own processes (the windows
    /// drawing themselves when PrintWindow asks).</summary>
    readonly record struct RateSample(double TargetFps, double Seconds, int Frames, int Empty, double Fps,
        double AverageMs, double P95Ms, double CaptureCpuPercentOfOneCore, double WorkspaceCpuPercentOfOneCore);

    static RateSample Measure(AgentDesktop desktop, PreviewSmoothness smoothness, bool inUse)
    {
        TimeSpan interval = inUse ? WorkspacePeekHost.InUseIntervalFor(smoothness) : WorkspacePeekHost.IdleIntervalFor(smoothness);
        double targetFps = 1.0 / interval.TotalSeconds;
        WorkspacePeekCapture.Cached? background = null;
        Dictionary<int, TimeSpan> before = Owned(desktop);
        TimeSpan self = Process.GetCurrentProcess().TotalProcessorTime;
        var times = new List<double>();
        int frames = 0, empty = 0;
        TimeSpan span = TimeSpan.FromSeconds(15);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < span)
        {
            long start = Stopwatch.GetTimestamp();
            WorkspacePeekCapture.Frame frame = WorkspacePeekCapture.Take(desktop, desktop.Name, background, inUse);
            background = frame.Cache;
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame.Background is not null) { frames++; times.Add(ms); } else empty++;
            double wait = 1000 / targetFps - ms;
            if (wait > 0) Thread.Sleep(TimeSpan.FromMilliseconds(wait));
        }
        double seconds = clock.Elapsed.TotalSeconds;
        double selfCpu = (Process.GetCurrentProcess().TotalProcessorTime - self).TotalSeconds;
        double workspaceCpu = Owned(desktop).Sum(p => (p.Value - before.GetValueOrDefault(p.Key)).TotalSeconds);
        times.Sort();
        return new RateSample(
            Math.Round(targetFps, 2), Math.Round(seconds, 2), frames, empty,
            Math.Round(frames / seconds, 2), times.Count > 0 ? Math.Round(times.Average(), 1) : 0,
            times.Count > 0 ? Math.Round(times[Math.Max(0, (int)Math.Ceiling(times.Count * 0.95) - 1)], 1) : 0,
            Math.Round(selfCpu / seconds * 100, 1), Math.Round(workspaceCpu / seconds * 100, 1));
    }

    static Dictionary<int, TimeSpan> Owned(AgentDesktop desktop)
    {
        var owned = new Dictionary<int, TimeSpan>();
        foreach (Process process in Process.GetProcesses())
            using (process)
            {
                try { if (desktop.OwnsProcess(process.Id)) owned[process.Id] = process.TotalProcessorTime; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        return owned;
    }
}
