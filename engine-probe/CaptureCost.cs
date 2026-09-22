using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

/// <summary>
/// What one whole-screen capture actually costs, stage by stage, and what the same picture would
/// cost composed straight into a canvas the size the caller asked for - the corner card or the
/// agent's 1280x720 cap - instead of at full screen size and shrunk afterwards.
/// <see cref="AgentDesktop.ScreenWidth"/> is the primary monitor in physical pixels, so every
/// capture composes a full-screen DIB and copies it into a bitmap source, however small the
/// picture a viewer wanted.
///
/// The downscale is prototyped here rather than in the engine: <see cref="Compose"/> is
/// <c>AgentDesktop.CaptureOnPump</c>'s body with StretchBlt in place of BitBlt, so the two can be
/// timed side by side before anything ships. Three real windows on a real workspace desktop.
/// Run: Deskweave.Probe.exe --capture-cost "C:\absolute\output"
/// Wire it with one line in Program.cs, next to --capture-spike and --corner-rate:
///   if (args.Length == 2 &amp;&amp; args[0] == "--capture-cost" &amp;&amp; Path.IsPathFullyQualified(args[1]))
///       return CaptureCost.Run(Path.GetFullPath(args[1]));
/// </summary>
internal static class CaptureCost
{
    const int Halftone = 4, ColorOnColor = 3, BitsPixel = 12, DesktopHorzRes = 118, DesktopVertRes = 117;

    [DllImport("gdi32.dll")]
    static extern bool StretchBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint deviceContext, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(nint deviceContext, int x, int y, nint previous);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(nint deviceContext, int index);

    static byte[] _pixels = [];

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Capture cost probe exceeded its 300-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(300), Timeout.InfiniteTimeSpan);

        var result = new Dictionary<string, object?>();
        nint screenDc = Native.GetDC(0);
        result["screen"] = new
        {
            systemMetrics = $"{AgentDesktop.ScreenWidth}x{AgentDesktop.ScreenHeight}",
            physical = $"{GetDeviceCaps(screenDc, DesktopHorzRes)}x{GetDeviceCaps(screenDc, DesktopVertRes)}",
            bitsPerPixel = GetDeviceCaps(screenDc, BitsPixel),
            processors = Environment.ProcessorCount,
            os = Environment.OSVersion.VersionString,
        };
        Native.ReleaseDC(0, screenDc);
        // The wall is painted under every frame and its cost depends on this, so it is recorded
        // rather than assumed. The probe only reads the setting; it never writes one.
        result["agentScreenLook"] = AppSettingsStore.Current.AgentScreen.ToString();

        // No workspace, no store, no bridge: this measures the capture path, so it takes the
        // desktop on its own and gives it back in the finally.
        string name = "cost-" + Guid.NewGuid().ToString("N")[..8];
        AgentDesktop? desktop = null;
        Pump? pump = null;
        try
        {
            desktop = AgentDesktop.Create(name);
            string system = Environment.SystemDirectory;
            desktop.Launch(Path.Combine(system, "notepad.exe"));
            desktop.Launch(Path.Combine(system, "notepad.exe"));
            desktop.Launch(Path.Combine(system, "cmd.exe"), "/k ping -t 127.0.0.1");
            var waited = Stopwatch.StartNew();
            while (desktop.Windows().Count < 3 && waited.Elapsed < TimeSpan.FromSeconds(25)) Thread.Sleep(200);
            Thread.Sleep(1500); // first paints
            IReadOnlyList<AgentWindow> windows = desktop.Windows();
            result["windows"] = windows.Select(w => new { w.Title, w.ClassName, w.X, w.Y, w.Width, w.Height }).ToArray();

            pump = new Pump(name);
            int width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
            // The wall builds on a pool thread the first time it sees a size; no measured frame
            // should pay for that one-off.
            foreach ((int w, int h) in Sizes(width, height)) pump.Run(() => Warm(w, h));
            Thread.Sleep(1500);

            var stages = new Dictionary<string, object?>();
            foreach ((int w, int h) in Sizes(width, height))
                stages[$"{w}x{h}" + (w == width ? " full, as it ships" : " StretchBlt HALFTONE")]
                    = Stages(desktop, pump, w, h, 12);
            result["stages"] = stages;
            result["shippingCaptureEndToEnd"] = EndToEnd(desktop, width, height, 10);
            result["compositionOnly"] = Blits(pump, windows, width, height, 30);

            var bytes = new Dictionary<string, object?>();
            foreach ((int w, int h) in Sizes(width, height)) bytes[$"{w}x{h}"] = Retained(pump, windows, w, h, 8);
            result["bytesPerFrame"] = bytes;

            result["idleAt2.5fps"] = Cadence(desktop, 2.5, false, 16);
            result["inUseAt12fps"] = Cadence(desktop, 12, true, 16);
            result["afterCaptureForTheAgent"] = AfterCapture(pump, windows, width, height, 8);
        }
        catch (Exception failure) { result["failure"] = failure.ToString(); }
        finally
        {
            pump?.Dispose();
            desktop?.Dispose();
        }
        File.WriteAllText(Path.Combine(output, "capture-cost.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.ContainsKey("failure") ? 1 : 0;
    }

    /// <summary>Full screen, the agent's cap, and a 344 DIP card at 200% and at 100% - always the
    /// screen's own shape, because the owner's clicks are mapped back through the screen's aspect
    /// and not the picture's.</summary>
    static IEnumerable<(int Width, int Height)> Sizes(int width, int height)
    {
        yield return (width, height);
        yield return (WorkspaceMarks.ActionWidth, WorkspaceMarks.ActionHeight);
        yield return (688, Math.Max(1, (int)Math.Round(688.0 * height / width)));
        yield return (344, Math.Max(1, (int)Math.Round(344.0 * height / width)));
    }

    /// <summary>A thread bound to the workspace desktop. The engine's own pump is private, and
    /// every user32/gdi32 call below is desktop-affine, so the probe keeps its own.</summary>
    sealed class Pump : IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
        readonly Thread _thread;

        internal Pump(string desktopName)
        {
            _thread = new Thread(() =>
            {
                nint handle = Native.OpenDesktopW(desktopName, 0, false, Native.GenericAll);
                if (handle == 0 || !Native.SetThreadDesktop(handle))
                    throw new InvalidOperationException("The probe thread could not bind to " + desktopName + ".");
                foreach (Action work in _work.GetConsumingEnumerable()) work();
                Native.CloseDesktop(handle);
            })
            { IsBackground = true, Name = "capture-cost" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        internal T Run<T>(Func<T> work)
        {
            T result = default!;
            Exception? failure = null;
            using var done = new ManualResetEventSlim();
            _work.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (failure is not null) throw new InvalidOperationException("The probe pump failed.", failure);
            return result;
        }

        public void Dispose() { _work.CompleteAdding(); _thread.Join(TimeSpan.FromSeconds(5)); }
    }

    static int Warm(int width, int height)
    {
        nint screenDc = Native.GetDC(0);
        nint canvasDc = Native.CreateCompatibleDC(screenDc);
        nint canvas = Native.CreateCompatibleBitmap(screenDc, width, height);
        nint previous = Native.SelectObject(canvasDc, canvas);
        WorkspaceWall.Paint(canvasDc, screenDc, width, height);
        Native.SelectObject(canvasDc, previous);
        Native.DeleteObject(canvas);
        Native.DeleteDC(canvasDc);
        Native.ReleaseDC(0, screenDc);
        return 0;
    }

    readonly record struct Shot(double WallMs, double PrintMs, double ConvertMs, double ReadMs, BitmapSource? Image);

    /// <summary>
    /// One composed frame. At full size this is <c>AgentDesktop.CaptureOnPump</c>'s own body; at any
    /// smaller size it is the proposed change - HALFTONE StretchBlt straight into the smaller canvas
    /// - so the two are timed through the same code with the same handle discipline.
    /// </summary>
    static Shot Compose(IReadOnlyList<AgentWindow> windows, int width, int height,
        int screenWidth, int screenHeight, int mode = Halftone)
    {
        bool downscale = width != screenWidth || height != screenHeight;
        double sx = (double)width / screenWidth, sy = (double)height / screenHeight;
        nint screenDc = Native.GetDC(0);
        nint canvasDc = 0, canvas = 0, previousCanvas = 0;
        double wallMs = 0, printMs = 0, convertMs = 0, readMs = 0;
        BitmapSource? image = null;
        try
        {
            canvasDc = Native.CreateCompatibleDC(screenDc);
            if (canvasDc == 0) return default;
            canvas = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (canvas == 0) return default;
            previousCanvas = Native.SelectObject(canvasDc, canvas);
            if (downscale) { SetStretchBltMode(canvasDc, mode); SetBrushOrgEx(canvasDc, 0, 0, 0); }

            long start = Stopwatch.GetTimestamp();
            WorkspaceWall.Paint(canvasDc, screenDc, width, height);
            wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                AgentWindow window = windows[i];
                if (!window.Responding) continue;
                nint windowDc = Native.CreateCompatibleDC(screenDc);
                if (windowDc == 0) continue;
                nint bitmap = 0, previous = 0;
                try
                {
                    bitmap = Native.CreateCompatibleBitmap(screenDc, window.Width, window.Height);
                    if (bitmap == 0) continue;
                    previous = Native.SelectObject(windowDc, bitmap);
                    if (!Native.PrintWindow(window.Handle, windowDc, Native.PwRenderFullContent)) continue;
                    if (!downscale)
                        Native.BitBlt(canvasDc, window.X, window.Y, window.Width, window.Height,
                            windowDc, 0, 0, Native.SrcCopy);
                    else
                        StretchBlt(canvasDc, (int)Math.Round(window.X * sx), (int)Math.Round(window.Y * sy),
                            Math.Max(1, (int)Math.Round(window.Width * sx)),
                            Math.Max(1, (int)Math.Round(window.Height * sy)),
                            windowDc, 0, 0, window.Width, window.Height, Native.SrcCopy);
                }
                finally
                {
                    if (previous != 0) Native.SelectObject(windowDc, previous);
                    if (bitmap != 0) Native.DeleteObject(bitmap);
                    Native.DeleteDC(windowDc);
                }
            }
            printMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Native.SelectObject(canvasDc, previousCanvas);
            previousCanvas = 0;
            start = Stopwatch.GetTimestamp();
            BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(canvas, 0, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            raw.Freeze();
            BitmapSource converted = new FormatConvertedBitmap(raw, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            convertMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            image = converted;

            // What a viewer pays to look at the frame: one copy of every pixel.
            start = Stopwatch.GetTimestamp();
            Read(converted);
            readMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        finally
        {
            if (previousCanvas != 0) Native.SelectObject(canvasDc, previousCanvas);
            if (canvas != 0) Native.DeleteObject(canvas);
            if (canvasDc != 0) Native.DeleteDC(canvasDc);
            Native.ReleaseDC(0, screenDc);
        }
        return new Shot(wallMs, printMs, convertMs, readMs, image);
    }

    static void Read(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        if (_pixels.Length < stride * image.PixelHeight) _pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(_pixels, stride, 0);
    }

    /// <summary>Every stage of one capture, separately, so it is clear which one dominates.</summary>
    static object Stages(AgentDesktop desktop, Pump pump, int width, int height, int runs)
    {
        int screenWidth = AgentDesktop.ScreenWidth, screenHeight = AgentDesktop.ScreenHeight;
        List<double> list() => new(runs);
        List<double> enumerate = list(), wall = list(), print = list(), convert = list(), read = list(), whole = list();
        int pixels = 0;
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
        for (int i = 0; i < runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<AgentWindow> windows = desktop.Windows();
            enumerate.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Shot shot = pump.Run(() => Compose(windows, width, height, screenWidth, screenHeight));
            whole.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            wall.Add(shot.WallMs);
            print.Add(shot.PrintMs);
            convert.Add(shot.ConvertMs);
            read.Add(shot.ReadMs);
            pixels = shot.Image is { } image ? image.PixelWidth * image.PixelHeight : 0;
        }
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        return new
        {
            runs,
            enumerateWindowsMs = Summary(enumerate),
            wallPaintMs = Summary(wall),
            printWindowsMs = Summary(print),
            convertToBitmapSourceMs = Summary(convert),
            readEveryPixelMs = Summary(read),
            wholeCaptureMs = Summary(whole),
            imagePixels = pixels,
            cpuMsPerFrame = Math.Round(cpuMs / runs, 1),
        };
    }

    /// <summary>The shipping path end to end, as a cross-check on the stages above.</summary>
    static object EndToEnd(AgentDesktop desktop, int width, int height, int runs)
    {
        var times = new List<double>(runs);
        long before = GC.GetTotalAllocatedBytes(true);
        int frames = 0;
        for (int i = 0; i < runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            DesktopFrame frame = desktop.Capture(width, height);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (frame.Image is not null) frames++;
        }
        return new
        {
            runs,
            frames,
            captureMs = Summary(times),
            managedBytesPerFrame = (GC.GetTotalAllocatedBytes(true) - before) / runs,
        };
    }

    /// <summary>
    /// The composition on its own: every window printed once and reused, so only the wall paint and
    /// the blits are timed. PrintWindow is the applications drawing themselves and varies by tens of
    /// milliseconds between frames, which would otherwise hide the difference being measured.
    /// </summary>
    static object Blits(Pump pump, IReadOnlyList<AgentWindow> windows, int screenWidth, int screenHeight, int runs)
        => pump.Run(() =>
    {
        nint screenDc = Native.GetDC(0);
        var prints = new List<(nint Dc, nint Bitmap, AgentWindow Window)>();
        var cases = new Dictionary<string, object?>();
        try
        {
            foreach (AgentWindow window in windows.Where(w => w.Responding))
            {
                nint dc = Native.CreateCompatibleDC(screenDc);
                if (dc == 0) continue;
                nint bitmap = Native.CreateCompatibleBitmap(screenDc, window.Width, window.Height);
                if (bitmap == 0) { Native.DeleteDC(dc); continue; }
                Native.SelectObject(dc, bitmap);
                if (Native.PrintWindow(window.Handle, dc, Native.PwRenderFullContent)) prints.Add((dc, bitmap, window));
                else { Native.DeleteObject(bitmap); Native.DeleteDC(dc); }
            }
            foreach ((int width, int height) in Sizes(screenWidth, screenHeight))
            {
                int[] modes = width == screenWidth ? [0] : [Halftone, ColorOnColor];
                foreach (int mode in modes)
                {
                    var times = new List<double>(runs);
                    double sx = (double)width / screenWidth, sy = (double)height / screenHeight;
                    for (int i = 0; i < runs; i++)
                    {
                        nint canvasDc = Native.CreateCompatibleDC(screenDc);
                        nint canvas = Native.CreateCompatibleBitmap(screenDc, width, height);
                        nint previous = Native.SelectObject(canvasDc, canvas);
                        if (mode != 0) { SetStretchBltMode(canvasDc, mode); SetBrushOrgEx(canvasDc, 0, 0, 0); }
                        long start = Stopwatch.GetTimestamp();
                        WorkspaceWall.Paint(canvasDc, screenDc, width, height);
                        foreach ((nint dc, _, AgentWindow window) in prints)
                        {
                            if (mode == 0)
                                Native.BitBlt(canvasDc, window.X, window.Y, window.Width, window.Height,
                                    dc, 0, 0, Native.SrcCopy);
                            else
                                StretchBlt(canvasDc, (int)Math.Round(window.X * sx), (int)Math.Round(window.Y * sy),
                                    Math.Max(1, (int)Math.Round(window.Width * sx)),
                                    Math.Max(1, (int)Math.Round(window.Height * sy)),
                                    dc, 0, 0, window.Width, window.Height, Native.SrcCopy);
                        }
                        times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        Native.SelectObject(canvasDc, previous);
                        Native.DeleteObject(canvas);
                        Native.DeleteDC(canvasDc);
                    }
                    cases[$"{width}x{height} " + mode switch
                    {
                        0 => "BitBlt 1:1, as it ships",
                        Halftone => "StretchBlt HALFTONE",
                        _ => "StretchBlt COLORONCOLOR",
                    }] = Summary(times);
                }
            }
        }
        finally
        {
            foreach ((nint dc, nint bitmap, _) in prints) { Native.DeleteDC(dc); Native.DeleteObject(bitmap); }
            Native.ReleaseDC(0, screenDc);
        }
        return (object)new { windowsPrinted = prints.Count, runs, compositionMs = cases };
    });

    /// <summary>
    /// What frames of this size hold on to, measured rather than computed. The canvas is a device
    /// bitmap and lives in the session pool; what the process keeps is the copy
    /// CreateBitmapSourceFromHBitmap makes, which is unmanaged WIC memory rather than managed heap -
    /// so private bytes is the honest measure and the large object heap is not where it lands.
    /// </summary>
    static object Retained(Pump pump, IReadOnlyList<AgentWindow> windows, int width, int height, int count)
    {
        int screenWidth = AgentDesktop.ScreenWidth, screenHeight = AgentDesktop.ScreenHeight;
        Settle();
        Process self = Process.GetCurrentProcess();
        self.Refresh();
        long managed = GC.GetTotalMemory(true), priv = self.PrivateMemorySize64, loh = Loh();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        var held = new List<BitmapSource>(count);
        for (int i = 0; i < count; i++)
            if (pump.Run(() => Compose(windows, width, height, screenWidth, screenHeight)).Image is { } image)
                held.Add(image);
        Settle();
        self.Refresh();
        int frames = Math.Max(1, held.Count);
        var measured = new
        {
            frames = held.Count,
            managedHeapBytesPerFrame = (GC.GetTotalMemory(true) - managed) / frames,
            privateBytesPerFrame = (self.PrivateMemorySize64 - priv) / frames,
            largeObjectHeapBytesPerFrame = (Loh() - loh) / frames,
            computedCanvasBytes = (long)width * height * 4,
            gen0Collections = GC.CollectionCount(0) - gen0,
            gen2Collections = GC.CollectionCount(2) - gen2,
        };
        GC.KeepAlive(held);
        return measured;
    }

    static long Loh()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        return info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
    }

    static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// The corner's own tick at a real cadence, counting how many of those ticks are whole-screen
    /// captures: in use, the background only refreshes once a second underneath the window patch,
    /// so twelve ticks a second is nothing like twelve whole screens a second.
    /// </summary>
    static object Cadence(AgentDesktop desktop, double fps, bool inUse, double seconds)
    {
        WorkspacePeekCapture.Cached? cache = null;
        Settle();
        long allocated = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
        Dictionary<int, TimeSpan> before = Owned(desktop);
        var times = new List<double>();
        int frames = 0, empty = 0, wholeScreens = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            long start = Stopwatch.GetTimestamp();
            WorkspacePeekCapture.Frame frame = WorkspacePeekCapture.Take(desktop, desktop.Name, cache, inUse);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame.Cache is { } taken && (cache is not { } was || !ReferenceEquals(was.Image, taken.Image)))
                wholeScreens++;
            cache = frame.Cache;
            if (frame.Background is not null) { frames++; times.Add(ms); } else empty++;
            if (1000 / fps - ms is > 0 and var wait) Thread.Sleep(TimeSpan.FromMilliseconds(wait));
        }
        double ran = clock.Elapsed.TotalSeconds;
        double cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds;
        double workspaceCpu = Owned(desktop).Sum(p => (p.Value - before.GetValueOrDefault(p.Key)).TotalSeconds);
        return new
        {
            targetFps = fps,
            inUse,
            seconds = Math.Round(ran, 2),
            frames,
            empty,
            wholeScreenCapturesPerMinute = Math.Round(wholeScreens / ran * 60, 1),
            tickMs = Summary(times),
            managedBytesPerMinute = (long)((GC.GetTotalAllocatedBytes(true) - allocated) / ran * 60),
            gen0PerMinute = Math.Round((GC.CollectionCount(0) - gen0) / ran * 60, 1),
            gen2PerMinute = Math.Round((GC.CollectionCount(2) - gen2) / ran * 60, 1),
            captureCpuPercentOfOneCore = Math.Round(cpuSeconds / ran * 100, 1),
            workspaceCpuPercentOfOneCore = Math.Round(workspaceCpu / ran * 100, 1),
        };
    }

    /// <summary>
    /// What happens to a frame after it is captured, for an action group: today it is marked at full
    /// size onto a full-size RenderTargetBitmap and then shrunk with a TransformedBitmap; if the
    /// capture were already at the cap, the marks would be drawn at the capture's own scale on a
    /// picture a third of the size and there would be nothing left to shrink.
    /// </summary>
    static object AfterCapture(Pump pump, IReadOnlyList<AgentWindow> windows, int screenWidth, int screenHeight, int runs)
    {
        (int width, int height, double scale) = WorkspaceMarks.Fit(screenWidth, screenHeight,
            WorkspaceMarks.ActionWidth, WorkspaceMarks.ActionHeight);
        var elements = new List<WorkspaceElement>();
        for (int i = 0; i < 24; i++)
            elements.Add(new WorkspaceElement(i + 1, "Button", "control " + i, "id" + i,
                40 + i % 6 * 300, 60 + i / 6 * 240, 180, 60, true, "press", 2));
        var today = new List<double>(runs);
        var proposed = new List<double>(runs);
        for (int i = 0; i < runs; i++)
        {
            if (pump.Run(() => Compose(windows, screenWidth, screenHeight, screenWidth, screenHeight)).Image is { } full)
            {
                long start = Stopwatch.GetTimestamp();
                Read(WorkspaceMarks.Resize(WorkspaceMarks.Draw(full, elements, new Point(0, 0), 1), width, height));
                today.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            if (pump.Run(() => Compose(windows, width, height, screenWidth, screenHeight)).Image is { } capped)
            {
                long start = Stopwatch.GetTimestamp();
                Read(WorkspaceMarks.Draw(capped, elements, new Point(0, 0), scale));
                proposed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        return new
        {
            cap = $"{width}x{height}",
            scale = Math.Round(scale, 4),
            todayMarkThenResizeMs = Summary(today),
            capturedAtTheCapMarkOnlyMs = Summary(proposed),
            note = "the capture itself is in the stages block; this is only what the frame costs afterwards",
        };
    }

    static object Summary(List<double> values)
    {
        if (values.Count == 0) return new { average = 0.0, median = 0.0, p95 = 0.0, max = 0.0 };
        List<double> sorted = [.. values.Order()];
        return new
        {
            average = Math.Round(values.Average(), 2),
            median = Math.Round(sorted[sorted.Count / 2], 2),
            // Nearest rank: the smallest time at least 95% of frames are no slower than.
            p95 = Math.Round(sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)], 2),
            max = Math.Round(sorted[^1], 2),
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
}
