using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

internal static class MinecraftLivePreview
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var desktop = AgentDesktop.Current();
        if (desktop.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run this probe through ARS in the private workspace.");
        var windows = desktop.Windows();
        var connection = MinecraftPreview.Parse(File.ReadAllText(Path.Combine(
            WorkspaceSandbox.FolderFor(desktop.Name), "minecraft-bridge.json")));
        var game = windows.Single(window =>
        {
            Native.GetWindowThreadProcessId(window.Handle, out int pid);
            return pid == connection.GamePid;
        });
        // Warm up the bounded background provider by polling from this probe thread,
        // never by sleeping or waiting for HTTP on the desktop pump.
        var warmup = Stopwatch.StartNew();
        DesktopFrame screen;
        do
        {
            desktop.CaptureWindow(game.Handle);
            screen = desktop.Capture(AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight);
            if (screen.Image is not null && screen.Complete) break;
            Thread.Sleep(50);
        } while (warmup.Elapsed < TimeSpan.FromSeconds(5));
        warmup.Stop();
        if (screen.Image is null || !screen.Complete) throw new InvalidOperationException("Preview did not warm to a complete composite within five seconds.");
        var frame = desktop.CaptureWindow(game.Handle) ?? throw new InvalidOperationException("No warmed game preview.");
        Save(frame, Path.Combine(output, "window.png"));
        Save(screen.Image, Path.Combine(output, "workspace.png"));

        const int requestedSamples = 20, requestedStartIntervalMs = 50;
        var measurement = Stopwatch.StartNew();
        var durations = new List<double>();
        var samples = new List<object>();
        int returnedImages = 0;
        for (int index = 0; index < requestedSamples && measurement.Elapsed < TimeSpan.FromSeconds(10); index++)
        {
            double startedAtMs = measurement.Elapsed.TotalMilliseconds;
            var call = Stopwatch.StartNew();
            var observed = desktop.CaptureWindow(game.Handle);
            double durationMs = call.Elapsed.TotalMilliseconds;
            durations.Add(durationMs);
            if (observed is not null) returnedImages++;
            samples.Add(new
            {
                index, startedAtMs, durationMs, imageReturned = observed is not null,
                width = observed?.PixelWidth, height = observed?.PixelHeight,
            });
            if (index + 1 < requestedSamples)
            {
                // Maintain at least the requested start interval without queuing a
                // catch-up burst when an individual capture exceeds that interval.
                double waitMs = requestedStartIntervalMs - call.Elapsed.TotalMilliseconds;
                if (waitMs > 0) Thread.Sleep(TimeSpan.FromMilliseconds(waitMs));
            }
        }
        double measurementDurationMs = measurement.Elapsed.TotalMilliseconds;
        double[] ordered = durations.Order().ToArray();
        double? median = ordered.Length == 0 ? null : ordered.Length % 2 == 0
            ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2
            : ordered[ordered.Length / 2];
        double? p95 = ordered.Length == 0 ? null : ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        double? maximum = ordered.Length == 0 ? null : ordered[^1];
        var finalScreen = desktop.Capture(AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            desktop = desktop.Name, game.Title, game.Width, game.Height,
            window = new { frame.PixelWidth, frame.PixelHeight }, complete = screen.Complete,
            warmupMs = warmup.Elapsed.TotalMilliseconds,
            captureWindowLatency = new
            {
                requestedSamples, requestedStartIntervalMs, collectedSamples = durations.Count,
                returnedImages, measurementDurationMs, stoppedEarly = durations.Count != requestedSamples,
                callerLatencyMs = new { median, p95, maximum }, samples,
                finalCompositeComplete = finalScreen.Complete, finalScreen.Unavailable,
                caveat = "Caller-side CaptureWindow latency after warmup; returned images can reuse the latest cached frame. This is not fresh-frame FPS, input latency, game FPS, or an improvement comparison.",
            },
            engine = typeof(AgentDesktop).Assembly.Location,
            scope = "Compiled engine capture in existing private workspace; installed controller unchanged",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = new FileStream(path, FileMode.CreateNew);
        encoder.Save(file);
    }
}
