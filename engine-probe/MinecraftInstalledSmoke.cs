using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

internal static class MinecraftInstalledSmoke
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARSApp", "current", "Deskweave.AgentWorkspaces.dll");
        string loadedHash = Hash(typeof(AgentDesktop).Assembly.Location);
        if (Hash(installed) != loadedHash) throw new IOException("This probe must load the exact installed engine bytes.");
        ProductContext.Configure("DeskweaveMinecraftProbe");
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(output, "w"));
        string java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages/Microsoft.4297127D64EC6_8wekyb3d8bbwe/LocalCache/Local/runtime/java-runtime-epsilon/windows-x64/java-runtime-epsilon/bin/javaw.exe");
        string mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        string bridge = "C:/Users/jeffe/Desktop/code/bot/diagnostics/bridge/minecraft-bridge-agent.jar";
        if (!File.Exists(java) || !File.Exists(bridge)) throw new IOException("Existing test JDK/bridge missing.");
        using var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(mc, "versions/26.3/26.3.json")));
        var metadata = version.RootElement;
        var classpath = new List<string>();
        foreach (JsonElement library in metadata.GetProperty("libraries").EnumerateArray())
        {
            bool allowed = !library.TryGetProperty("rules", out JsonElement rules);
            if (rules.ValueKind == JsonValueKind.Array)
                foreach (JsonElement rule in rules.EnumerateArray())
                    if (!rule.TryGetProperty("features", out _) && (!rule.TryGetProperty("os", out JsonElement os)
                        || (!os.TryGetProperty("name", out JsonElement name) || name.GetString() == "windows")
                        && (!os.TryGetProperty("arch", out JsonElement arch) || arch.GetString() is "amd64" or "x86_64")))
                        allowed = rule.GetProperty("action").GetString() == "allow";
            if (allowed && library.GetProperty("downloads").TryGetProperty("artifact", out JsonElement artifact))
                classpath.Add(Path.Combine(mc, "libraries", artifact.GetProperty("path").GetString()!));
        }
        classpath.Add(Path.Combine(mc, "versions/26.3/26.3.jar"));
        if (classpath.Any(p => !File.Exists(p))) throw new IOException("Existing Minecraft libraries missing.");
        int gamePid = 0;
        var evidence = new Dictionary<string, object?>();
        bool passed = false;
        string? failure = null;
        using (var desktop = AgentDesktop.Create("ARS-MinecraftVerify-" + Guid.NewGuid().ToString("N")[..8]))
        try
        {
            string game = Path.Combine(desktop.Folder!, "game"); Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "options.txt"), "fullscreen:false\nrenderDistance:4\nsimulationDistance:4\npauseOnLostFocus:true\n");
            string natives = Path.Combine(game, "natives");
            var args = new[] { "-Xmx2G", "-XX:StackShadowPages=32", "-XX:+EnableDynamicAgentLoading", "--enable-native-access=ALL-UNNAMED",
                "--add-exports", "java.base/jdk.internal.misc=ALL-UNNAMED", "-Djava.library.path=" + Path.Combine(natives, "java"),
                "-Djna.tmpdir=" + Path.Combine(natives, "jna"), "-Dorg.lwjgl.system.SharedLibraryExtractPath=" + Path.Combine(natives, "lwjgl"),
                "-Dio.netty.native.workdir=" + Path.Combine(natives, "netty"), "-Dminecraft.launcher.brand=ARS-preview-smoke",
                "-Dminecraft.launcher.version=WO05A", "-cp", string.Join(';', classpath), metadata.GetProperty("mainClass").GetString()!,
                "--username", "ARSPreviewTest", "--version", "26.3", "--gameDir", game, "--assetsDir", Path.Combine(mc, "assets"),
                "--assetIndex", metadata.GetProperty("assetIndex").GetProperty("id").GetString()!,
                "--uuid", "cc2becfc543c3221ba4311401b353e63", "--accessToken", "0", "--versionType", "release", "--width", "960", "--height", "540" };
            string argfile = Path.Combine(desktop.Folder!, "game-arguments.txt");
            File.WriteAllLines(argfile, args.Select(JavaArgument));
            gamePid = desktop.Launch(java, "@\"" + argfile + "\"");
            if (gamePid <= 0 || !desktop.OwnsProcess(gamePid)) throw new IOException("Game did not launch in the private workspace job.");
            var timer = Stopwatch.StartNew();
            AgentWindow? window = null;
            while (timer.Elapsed < TimeSpan.FromSeconds(90))
            {
                window = desktop.Windows().FirstOrDefault(w => { Native.GetWindowThreadProcessId(w.Handle, out int pid); return pid == gamePid && w.Title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase); });
                if (window is not null && timer.Elapsed > TimeSpan.FromSeconds(20)) break;
                using var process = Process.GetProcessById(gamePid);
                if (process.HasExited) throw new IOException("Test game exited during startup.");
                Thread.Sleep(500);
            }
            if (window is null) throw new IOException("Test game has no private workspace window.");
            string descriptor = Path.Combine(desktop.Folder!, "minecraft-bridge.json");
            string attachFile = Path.Combine(desktop.Folder!, "attach-arguments.txt");
            File.WriteAllLines(attachFile, new[] { "--add-modules", "jdk.attach,jdk.httpserver", "-cp", bridge, "BridgeAttach", gamePid.ToString(), bridge, descriptor }.Select(JavaArgument));
            string attachLog = Path.Combine(output, "attach.log");
            string javaConsole = Path.Combine(Path.GetDirectoryName(java)!, "java.exe");
            int attachPid = desktop.Launch(Environment.GetEnvironmentVariable("COMSPEC")!, "/d /c \"\"" + javaConsole + "\" @\"" + attachFile + "\" >\"" + attachLog + "\" 2>&1\"");
            if (attachPid <= 0) throw new IOException("Private bridge attach helper did not launch.");
            using (var attach = Process.GetProcessById(attachPid))
            { _ = attach.Handle; if (!attach.WaitForExit(30000) || attach.ExitCode != 0) throw new IOException("Private bridge attach failed."); }
            var connection = MinecraftPreview.Parse(File.ReadAllText(descriptor));
            if (connection.GamePid != gamePid) throw new IOException("Provider descriptor points to the wrong game PID.");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
            byte[]? jpeg = null; double? age = null;
            for (int i = 0; i < 30; i++)
            {
                using var response = http.GetAsync("http://127.0.0.1:" + connection.Port + "/frame").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    if (response.Headers.GetValues("X-Game-Pid").Single() != gamePid.ToString()) throw new IOException("Frame PID does not match.");
                    age = double.Parse(response.Headers.GetValues("X-Frame-Age-Ms").Single(), System.Globalization.CultureInfo.InvariantCulture);
                    jpeg = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(); break;
                }
                Thread.Sleep(500);
            }
            if (jpeg is null || age > MinecraftPreview.MaximumAgeMs) throw new IOException("No fresh game preview frame.");
            using (var stream = new MemoryStream(jpeg))
                if (Colors(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)) <= 32)
                    throw new IOException("The game feed itself is blank.");
            File.WriteAllBytes(Path.Combine(output, "game-feed.jpg"), jpeg);
            DesktopFrame screen = new(null, 0, 0, false);
            BitmapSource? captured = null;
            for (int i = 0; i < 80; i++)
            {
                captured = desktop.CaptureWindow(window.Handle);
                screen = desktop.Capture(AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight);
                if (captured is not null && screen.Complete && screen.Image is not null && Colors(captured) > 32) break;
                Thread.Sleep(100);
            }
            if (captured is null || !screen.Complete || screen.Image is null || Colors(captured) <= 32) throw new IOException("Installed provider did not produce a complete nonblank game capture.");
            Save(captured, Path.Combine(output, "game-window.png")); Save(screen.Image, Path.Combine(output, "game-composite.png"));
            evidence["desktop"] = desktop.Name; evidence["gamePid"] = gamePid; evidence["gameTitle"] = window.Title;
            evidence["frameAgeMs"] = age; evidence["frameBytes"] = jpeg.Length;
            evidence["captureWidth"] = captured.PixelWidth; evidence["captureHeight"] = captured.PixelHeight;
            evidence["compositeComplete"] = screen.Complete; evidence["sampledColors"] = Colors(captured);
            passed = true;
        }
        catch (Exception e) { failure = e.ToString(); }
        bool exited;
        try { if (gamePid <= 0) exited = true; else { using var game = Process.GetProcessById(gamePid); exited = game.WaitForExit(5000); } }
        catch (ArgumentException) { exited = true; }
        evidence["success"] = passed && exited; evidence["gameExited"] = exited; evidence["failure"] = failure;
        evidence["engine"] = typeof(AgentDesktop).Assembly.Location; evidence["installedEngineSha256"] = loadedHash;
        evidence["bridgeJarSha256"] = Hash(bridge);
        evidence["scope"] = "Real Minecraft 26.3 private title/menu-screen preview; fresh game directory, offline test identity, no owner profile/world or input, no account credentials. Exact installed engine bytes.";
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return passed && exited ? 0 : 1;
    }
    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    // Java argument files accept quoted backslash escapes, not JSON's Unicode escapes (e.g. '+' as \u002B).
    static string JavaArgument(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static int Colors(BitmapSource image)
    {
        var rgb = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0); int stride = rgb.PixelWidth * 4;
        byte[] bytes = new byte[stride * rgb.PixelHeight]; rgb.CopyPixels(bytes, stride, 0); var colors = new HashSet<int>();
        for (int y = rgb.PixelHeight / 5; y < rgb.PixelHeight * 4 / 5; y += 7)
            for (int x = rgb.PixelWidth / 5; x < rgb.PixelWidth * 4 / 5; x += 7)
        { int p = y * stride + x * 4; colors.Add(bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16); }
        return colors.Count;
    }
    static void Save(BitmapSource image, string path)
    { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file); }
}
