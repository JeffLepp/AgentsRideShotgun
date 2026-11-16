using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

internal static class BrowserCaptureDiagnostic
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        ProductContext.Configure("DeskweaveCaptureProbe");
        using var root = WorkspaceStore.UseRootForTests(Path.Combine(output, "w"));
        using var settings = new BrowserFixtureSettings();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        using var desktop = AgentDesktop.Create("ARS-CaptureProbe-" + Guid.NewGuid().ToString("N")[..8]);
        string page = Path.Combine(desktop.Folder!, "blue.html");
        File.WriteAllText(page, "<!doctype html><title>ARS native capture blue</title><style>html,body{margin:0;height:100%;background:rgb(0,90,200);color:white}</style><h1>ARS native capture blue</h1>");
        using var browser = WorkspaceBrowser.Start(desktop, new Uri(Path.Combine(desktop.Folder!, "missing.html")).AbsoluteUri,
            cancel: deadline.Token).GetAwaiter().GetResult() ?? throw new IOException("Browser did not attach.");
        bool navigated = browser.Go(new Uri(page).AbsoluteUri, deadline.Token).GetAwaiter().GetResult();
        desktop.Fill(browser.Window);
        var samples = new List<object>();
        Sample("before-cdp");
        BitmapSource? cdp = browser.Screenshot(deadline.Token).GetAwaiter().GetResult();
        if (cdp is not null) Save(cdp, "cdp.png");
        Sample("after-cdp");
        var tabs = browser.Tabs(deadline.Token).GetAwaiter().GetResult();
        browser.SelectTab(tabs.First().Number, deadline.Token).GetAwaiter().GetResult();
        Sample("after-activate");
        File.WriteAllText(Path.Combine(output, "diagnostic.json"), JsonSerializer.Serialize(new
        {
            executable = WorkspaceBrowser.ChromePath,
            version = FileVersionInfo.GetVersionInfo(WorkspaceBrowser.ChromePath).ProductVersion,
            browser.ProcessId, navigated, browser.Transport, desktop = desktop.Name,
            dom = browser.Evaluate("JSON.stringify({title:document.title,url:location.href,visible:document.visibilityState,width:innerWidth,height:innerHeight})", deadline.Token).GetAwaiter().GetResult(),
            cdpBluePercent = cdp is null ? -1 : BluePercent(cdp), samples,
            engineSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(AgentDesktop).Assembly.Location))),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        void Sample(string name)
        {
            Thread.Sleep(700);
            var windows = desktop.Windows();
            foreach (AgentWindow window in windows)
            {
                BitmapSource? frame = desktop.CaptureWindow(window.Handle);
                Native.GetWindowThreadProcessId(window.Handle, out int pid);
                if (frame is not null) Save(frame, name + "-" + window.Handle + ".png");
                samples.Add(new { stage = name, handle = window.Handle.ToInt64(), pid, window.Title, window.ClassName,
                    window.X, window.Y, window.Width, window.Height, window.Responding,
                    selected = window.Handle == browser.Window, bluePercent = frame is null ? -1 : BluePercent(frame) });
            }
            DesktopFrame composite = desktop.Capture(AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight);
            if (composite.Image is not null) Save(composite.Image, name + "-composite.png");
        }
        void Save(BitmapSource image, string name)
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(output, name)); png.Save(stream);
        }
    }

    static double BluePercent(BitmapSource image)
    {
        var rgb = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int stride = rgb.PixelWidth * 4; byte[] pixels = new byte[stride * rgb.PixelHeight]; rgb.CopyPixels(pixels, stride, 0);
        int matched = 0, count = 0;
        for (int y = rgb.PixelHeight / 3; y < rgb.PixelHeight * 2 / 3; y += 4)
            for (int x = rgb.PixelWidth / 4; x < rgb.PixelWidth * 3 / 4; x += 4)
            { int p = y * stride + x * 4; count++; if (Math.Abs(pixels[p] - 200) <= 12 && Math.Abs(pixels[p + 1] - 90) <= 12 && pixels[p + 2] <= 12) matched++; }
        return count == 0 ? 0 : 100.0 * matched / count;
    }
}
