using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Wave 0 spike (design/BUILD_PLAYBOOK.md): how fast a workspace screen can be composited with
/// PrintWindow, and what it costs. The corner window wants 10+ fps while the owner uses it and
/// under 15% of one core. Four real windows on a real workspace desktop: two Notepads, a console
/// that never stops printing and a browser running an animation.
/// Run: Deskweave.Probe.exe --capture-spike "C:\absolute\output"
/// </summary>
internal static class CaptureSpike
{
    const string Page = "<!doctype html><title>Capture spike</title><style>body{margin:0;font:18px Segoe UI;background:#fff}" +
        "h1{margin:24px}i{position:absolute;top:120px;width:220px;height:220px;border-radius:16px;background:#2E6BF6;" +
        "animation:m 1.6s ease-in-out infinite alternate}@keyframes m{from{left:20px}to{left:900px}}</style><h1>Capture spike</h1><i></i>";

    static byte[] _pixels = [];

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "s-" + Guid.NewGuid().ToString("N")[..8]);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Capture spike exceeded its 180-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        StoredWorkspace workspace = WorkspaceStore.Create("Spike");
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false });
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        var result = new Dictionary<string, object?>();
        try
        {
            AgentDesktop desktop = runtime.Computer!;
            string system = Environment.SystemDirectory;
            Directory.CreateDirectory(fixture);
            string page = Path.Combine(fixture, "page.html");
            File.WriteAllText(page, Page);
            desktop.Launch(Path.Combine(system, "notepad.exe"));
            desktop.Launch(Path.Combine(system, "notepad.exe"));
            desktop.Launch(Path.Combine(system, "cmd.exe"), "/k ping -t 127.0.0.1");
            string browser = WorkspaceBrowser.ChromePath;
            if (browser.Length > 0)
                desktop.Launch(browser, $"--user-data-dir=\"{Path.Combine(fixture, "b")}\" --no-first-run --no-default-browser-check " +
                    $"--window-position=60,40 --window-size=1280,720 \"{new Uri(page).AbsoluteUri}\"");
            int expected = browser.Length > 0 ? 4 : 3;
            var waited = Stopwatch.StartNew();
            while (desktop.Windows().Count < expected && waited.Elapsed < TimeSpan.FromSeconds(40)) Thread.Sleep(250);
            Thread.Sleep(3000); // the browser's first paints
            IReadOnlyList<AgentWindow> windows = desktop.Windows();
            result["screen"] = $"{AgentDesktop.ScreenWidth}x{AgentDesktop.ScreenHeight}";
            result["cpu"] = Environment.ProcessorCount + " logical processors";
            result["browser"] = browser;
            result["windows"] = windows.Select(w => new { w.Title, w.ClassName, w.Width, w.Height }).ToArray();

            int width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
            Func<bool> whole = () => Show(desktop.Capture(width, height).Image);
            Func<bool> small = () => Show(desktop.Capture(1280, 720).Image);
            nint front = windows.Count > 0 ? windows[0].Handle : 0;
            Func<bool> one = () => Show(desktop.CaptureWindow(front));

            result["idle"] = Measure(desktop, TimeSpan.FromSeconds(5), null, 0);
            result["wholeScreenFlatOut"] = Measure(desktop, TimeSpan.FromSeconds(8), whole, 0);
            result["wholeScreenAt10fps"] = Measure(desktop, TimeSpan.FromSeconds(8), whole, 10);
            result["wholeScreenAt2.5fps"] = Measure(desktop, TimeSpan.FromSeconds(8), whole, 2.5);
            result["canvas1280x720FlatOut"] = Measure(desktop, TimeSpan.FromSeconds(8), small, 0);
            result["frontWindowFlatOut"] = Measure(desktop, TimeSpan.FromSeconds(8), one, 0);
            result["frontWindowAt15fps"] = Measure(desktop, TimeSpan.FromSeconds(8), one, 15);
            if (desktop.Capture(width, height).Image is { } sample) Save(sample, Path.Combine(output, "sample.png"));
        }
        catch (Exception failure) { result["failure"] = failure.ToString(); }
        finally { runtime.Dispose(); }
        File.WriteAllText(Path.Combine(output, "spike.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.ContainsKey("failure") ? 1 : 0;
    }

    /// <summary>
    /// Captures for a while, flat out or paced, and reports frame rate, frame time and CPU: this
    /// process (the capture and the pixel copy a view would make) and the workspace's own
    /// processes (the drawing each window does when it is asked to print itself).
    /// </summary>
    static object Measure(AgentDesktop desktop, TimeSpan span, Func<bool>? capture, double fps)
    {
        Dictionary<int, TimeSpan> before = Owned(desktop);
        TimeSpan self = Process.GetCurrentProcess().TotalProcessorTime;
        var times = new List<double>();
        int frames = 0, empty = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < span)
        {
            if (capture is null) { Thread.Sleep(100); continue; }
            long start = Stopwatch.GetTimestamp();
            bool got = capture();
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // Frame times describe frames; a refused or empty capture is only counted.
            if (got) { frames++; times.Add(ms); } else empty++;
            if (fps > 0 && 1000 / fps - ms is > 0 and var wait) Thread.Sleep(TimeSpan.FromMilliseconds(wait));
        }
        double seconds = clock.Elapsed.TotalSeconds;
        double selfCpu = (Process.GetCurrentProcess().TotalProcessorTime - self).TotalSeconds;
        double workspaceCpu = Owned(desktop).Sum(p => (p.Value - before.GetValueOrDefault(p.Key)).TotalSeconds);
        times.Sort();
        return new
        {
            seconds = Math.Round(seconds, 2),
            frames,
            empty,
            fps = Math.Round(frames / seconds, 2),
            averageMs = times.Count > 0 ? Math.Round(times.Average(), 1) : 0,
            // Nearest rank: the smallest frame time at least 95% of frames are no slower than.
            p95Ms = times.Count > 0 ? Math.Round(times[Math.Max(0, (int)Math.Ceiling(times.Count * 0.95) - 1)], 1) : 0,
            maxMs = times.Count > 0 ? Math.Round(times[^1], 1) : 0,
            captureCpuPercentOfOneCore = Math.Round(selfCpu / seconds * 100, 1),
            workspaceCpuPercentOfOneCore = Math.Round(workspaceCpu / seconds * 100, 1),
        };
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

    // What a view pays to draw the frame: the conversion and one copy of every pixel.
    static bool Show(BitmapSource? image)
    {
        if (image is null) return false;
        int stride = image.PixelWidth * 4;
        if (_pixels.Length < stride * image.PixelHeight) _pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(_pixels, stride, 0);
        return true;
    }

    static void Save(BitmapSource image, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
