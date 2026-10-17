using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deskweave.AgentWorkspaces;

/// <summary>Real Chromium, harmless local session cookie, no access to the owner's browser data.</summary>
internal static class BrowserSessionProbe
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new Timer(_ => Environment.Exit(2), null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "bs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixture);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(fixture, "settings.json"));
        var checks = new List<object>();
        string? failure = null;
        void Check(bool passed, string name, object? detail = null)
        {
            checks.Add(new { passed, name, detail });
            File.AppendAllText(Path.Combine(output, "progress.log"), (passed ? "PASS " : "FAIL ") + name + "\n");
            if (!passed) throw new InvalidOperationException(name);
        }
        try { RunBrowser(fixture, Check).GetAwaiter().GetResult(); }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            passed = failure is null, checks, failure,
            chromiumVersion = FileVersionInfo.GetVersionInfo(WorkspaceBrowser.ChromePath).ProductVersion,
            scope = "Isolated Chromium process on a hidden test desktop; local pages and a harmless fixture cookie only. "
                + "The actual owner browser's debugging permission flow was not enabled or approved.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    static async Task RunBrowser(string fixture, Action<bool, string, object?> check)
    {
        check(!new AppSettings().SeparateBrowserProfile && !AppSettingsStore.Current.SeparateBrowserProfile,
            "Separate browser profile defaults off, including a missing setting", null);
        check(WorkspaceBrowser.OwnerEndpoint("54321\n/devtools/browser/abc-123\n")?.Host == "127.0.0.1"
            && WorkspaceBrowser.OwnerEndpoint("0\n/devtools/browser/abc") is null
            && WorkspaceBrowser.OwnerEndpoint("123\n/devtools/browser/abc?secret=1") is null
            && WorkspaceBrowser.OwnerEndpoint("123\n//external.example/devtools/browser/abc") is null,
            "Only validated loopback browser endpoint metadata is accepted", null);
        using var site = new LocalSite();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        CancellationToken cancel = deadline.Token;
        using AgentDesktop fixtureBrowser = AgentDesktop.Create("session-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        using AgentDesktop workspace = AgentDesktop.Create("session-work-" + Guid.NewGuid().ToString("N")[..8]);
        using var plane = new WorkspaceControl(workspace);
        File.WriteAllBytes(Path.Combine(workspace.Folder!, WorkspaceBrowser.UsedMark), []);
        await plane.Prewarm(cancel);
        check(plane.Browser is null, "Default-off prewarm never requests owner browser permission", null);
        string profile = Path.Combine(fixture, "shared-fixture");
        int pid = fixtureBrowser.LaunchBrowser(WorkspaceBrowser.ChromePath,
            $"--no-first-run --no-default-browser-check --mute-audio --enable-logging --log-file=\"{Path.Combine(fixture, "chrome.log")}\" --remote-debugging-port=0 --user-data-dir=\"{profile}\" -- \"{site.Url}original\"");
        check(pid > 0, "The test starts its own Chromium without changing the owner's browser", pid);
        Uri? endpoint = null;
        string stamp = Path.Combine(profile, "DevToolsActivePort");
        for (int wait = 0; wait < 200 && endpoint is null; wait++)
        {
            await Task.Delay(100, cancel);
            if (File.Exists(stamp)) endpoint = WorkspaceBrowser.OwnerEndpoint(await File.ReadAllTextAsync(stamp, cancel));
        }
        check(endpoint is not null, "The fixture publishes its dynamic browser endpoint", null);
        using var oracle = new Cdp();
        await oracle.Connect(endpoint!, cancel);
        JsonArray initial = (await oracle.Call("Target.getTargets", null, cancel))!["targetInfos"]!.AsArray();
        string[] initialPages = initial.Where(t => t?["type"]?.ToString() == "page")
            .Select(t => t!["targetId"]!.ToString()).ToArray();
        string original = initial.First(t => t?["type"]?.ToString() == "page")!["targetId"]!.ToString();
        string originalSession = (await oracle.Call("Target.attachToTarget", new JsonObject
            { ["targetId"] = original, ["flatten"] = true }, cancel))!["sessionId"]!.ToString();
        for (int wait = 0; wait < 100 && await oracle.Eval(originalSession, "document.title", cancel) != "Original fixture"; wait++)
            await Task.Delay(100, cancel);
        check(await oracle.Eval(originalSession, "document.cookie", cancel) == "fixture_session=harmless",
            "The original fixture tab establishes its own harmless session", null);
        using WorkspaceBrowser? browser = await WorkspaceBrowser.ConnectOwnerEndpoint(workspace, endpoint!, site.Url + "normal", cancel);
        check(browser is { UsesOwnerSession: true, Alive: true, InitialNavigationConfirmed: true }
            && browser.Window == 0 && browser.ProcessId == 0,
            "ARS attaches live and navigates its own tab without adopting a native owner window", null);
        WorkspaceBrowser live = browser!;
        typeof(WorkspaceControl).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plane, live);
        check(await live.Evaluate("document.cookie", cancel) == "fixture_session=harmless",
            "ARS's new tab uses the existing browser context's session with no cookie copy", null);
        check(!Directory.Exists(Path.Combine(workspace.Folder!, "chrome-profile")),
            "Live session mode creates no ARS browser profile", null);
        IReadOnlyList<BrowserTab> tabs = await live.Tabs(cancel);
        check(tabs.Count == 1 && tabs.All(t => t.TargetId != original),
            "Only ARS-created tabs are listed or selectable", null);
        check(!await live.SelectTab(2, cancel), "Existing user tabs cannot be selected through ARS tab numbers", null);
        check(await oracle.Eval(originalSession, "document.title", cancel) == "Original fixture"
            && await oracle.Eval(originalSession, "document.visibilityState", cancel) == "visible",
            "Navigation does not change or activate the original foreground tab", null);
        string read = await live.Read(cancel: cancel);
        check(read.Contains("Session available", StringComparison.Ordinal) && !read.Contains("probe-password-hidden", StringComparison.Ordinal),
            "Normal page reads preserve usable content and redact password values and value selectors", null);
        var screenshot = await live.Screenshot(cancel);
        check(screenshot is { PixelWidth: > 0, PixelHeight: > 0 }, "A background ARS tab has a CDP screenshot in CSS pixels", null);
        check(plane.AgentTakes(), "The fixture can acquire an agent input lease", null);
        using var native = new ToolHangs.WindowThread(workspace.Name, "Browser presentation fixture");
        check((await plane.OpenBrowserWithReceipt(site.Url + "normal", cancel)).Confirmed,
            "A browser request selects the live browser page beside an existing native window", null);
        var frame = plane.Frame();
        check(frame is { PixelWidth: > 0, PixelHeight: > 0 } && !plane.ScreenState.Stale && plane.UsesBrowserViewport
            && plane.DisplayingBrowserViewport && plane.IsBrowserViewport(frame),
            "The workspace frame shows the live browser page with a current receipt", null);
        check(plane.Arrange(native.Top, WindowArrangement.Front), "An explicit native window action selects the native screen", null);
        var nativeFrame = plane.Frame();
        check(nativeFrame is not null && !plane.UsesBrowserViewport && !plane.DisplayingBrowserViewport
            && !plane.IsBrowserViewport(nativeFrame) && plane.IsBrowserViewport(frame!),
            "Each viewer frame keeps its own coordinate provenance across a native/browser presentation change", null);
        check((await plane.OpenBrowserWithReceipt(site.Url + "normal", cancel)).Confirmed && plane.UsesBrowserViewport,
            "The next browse automatically returns the workspace screen to the live browser page", null);
        string location = await live.Evaluate("(() => {const b=document.querySelector('#click');const r=b.getBoundingClientRect();return JSON.stringify([r.x+r.width/2,r.y+r.height/2]);})()", cancel);
        double[] point = JsonSerializer.Deserialize<double[]>(location)!;
        // Computer() presents a normalized model screenshot, so map CSS coordinates through it.
        (int width, int height) = await live.Viewport(cancel);
        double scale = WorkspaceMarks.Fit(width, height, 1280, 720).Scale;
        var input = await plane.Computer(new ComputerRequest("desktop", true,
            [new("click", (int)(point[0] * scale), (int)(point[1] * scale))]), cancel);
        check(input.Target == "browser" && input.Receipt.Status == "completed"
            && await live.Evaluate("document.querySelector('#count').textContent", cancel) == "1",
            "Default desktop computer input matches the mirrored browser screenshot", input.Receipt.Status);
        plane.Release();
        plane.OwnerTakes();
        CancellationToken lease = plane.OwnerInputToken;
        bool pressed = await plane.OwnerBrowserPointer("mousePressed", (int)point[0], (int)point[1], false, false, lease);
        bool released = await plane.OwnerBrowserPointer("mouseReleased", (int)point[0], (int)point[1], false, false, lease);
        check(pressed && released && await live.Evaluate("document.querySelector('#count').textContent", cancel) == "2",
            "Owner press and release act on the background mirrored page", null);
        const string ownerSecret = "owner-password-probe-do-not-persist";
        await live.Evaluate("document.querySelector('#password').focus()", cancel);
        check(await plane.OwnerBrowserAction(new("keypress", Keys: ["CTRL", "A"]), lease)
            && await plane.OwnerBrowserAction(new("type", Text: ownerSecret), lease)
            && await live.Evaluate("document.querySelector('#password').value", cancel) == ownerSecret,
            "Owner keyboard shortcuts and text act on the mirrored password field", null);
        check(!(await live.Read(cancel: cancel)).Contains(ownerSecret, StringComparison.Ordinal)
            && !File.ReadAllText(plane.Evidence.LogPath).Contains(ownerSecret, StringComparison.Ordinal),
            "Owner password input stays out of page readback and the ARS evidence log", null);
        check(await plane.OwnerBrowserAction(new("scroll", (int)point[0], (int)point[1], ScrollY: 360), lease),
            "Owner wheel input reaches the mirrored browser page", null);
        for (int wait = 0; wait < 20 && await live.Evaluate("window.scrollY > 0", cancel) != "true"; wait++)
            await Task.Delay(50, cancel);
        check(await live.Evaluate("window.scrollY > 0", cancel) == "true",
            "The browser's actual scroll position confirms owner wheel input", null);
        plane.Release();
        plane.OwnerTakes();
        check(!await plane.OwnerBrowserPointer("mousePressed", (int)point[0], (int)point[1], false, false, lease),
            "An expired owner lease cannot replay input after another takeover", null);
        plane.Release();
        var clock = Stopwatch.StartNew();
        bool ready = await live.Go(site.Url + "held", cancel);
        check(ready && clock.Elapsed < TimeSpan.FromSeconds(4)
            && await live.Evaluate("document.readyState", cancel) == "interactive",
            "A usable page with a never-ending image request confirms navigation without false timeout", clock.Elapsed.TotalMilliseconds);
        clock.Restart();
        bool failed = await live.Go(site.Url + "hung-navigation", cancel);
        check(!failed && clock.Elapsed < TimeSpan.FromSeconds(14)
            && live.LastProtocolError == "The navigation did not become usable within 12 seconds.",
            "A main-document navigation that never responds is bounded across all CDP calls", clock.Elapsed.TotalMilliseconds);
        check(await live.Go(site.Url + "normal", cancel), "A timed-out navigation is not replayed and an explicit new request can recover", null);
        using var closedPort = new TcpListener(IPAddress.Loopback, 0);
        closedPort.Start();
        int unavailable = ((IPEndPoint)closedPort.LocalEndpoint).Port;
        closedPort.Stop();
        check(!await live.Go($"http://127.0.0.1:{unavailable}/", cancel)
            && live.LastProtocolError?.StartsWith("net::ERR_", StringComparison.Ordinal) == true,
            "True Chromium network failures remain failures with a safe diagnostic", live.LastProtocolError);
        check(await live.NewTab(site.Url + "normal", cancel) && (await live.Tabs(cancel)).Count == 2,
            "Further ARS tabs stay in the same live session", null);
        string[] arsPages = (await live.Tabs(cancel)).Select(t => t.TargetId).ToArray();
        live.Abandon();
        JsonArray remaining = (await oracle.Call("Target.getTargets", null, cancel))!["targetInfos"]!.AsArray();
        // closeTarget acknowledges the request before the UI removes its tab (especially Edge).
        // Assert ownership, allowing asynchronous closure and unrelated browser-created tabs.
        for (int wait = 0; wait < 20 && remaining.Any(t => arsPages.Contains(t?["targetId"]?.ToString())); wait++)
        {
            await Task.Delay(100, cancel);
            remaining = (await oracle.Call("Target.getTargets", null, cancel))!["targetInfos"]!.AsArray();
        }
        check(initialPages.All(id => remaining.Any(t => t?["targetId"]?.ToString() == id))
            && !remaining.Any(t => arsPages.Contains(t?["targetId"]?.ToString()))
            && await oracle.Eval(originalSession, "document.title", cancel) == "Original fixture",
            "Disposal closes only ARS tabs, preserves the original tab and leaves the browser running",
            new { initialPages = initialPages.Length, arsPages = arsPages.Length,
                remainingPages = remaining.Count(t => t?["type"]?.ToString() == "page") });
        using Process process = Process.GetProcessById(pid);
        check(!process.HasExited, "Abandon never kills the live-session browser process", null);
    }

    sealed class Cdp : IDisposable
    {
        readonly ClientWebSocket _socket = new();
        int _id;
        internal Task Connect(Uri endpoint, CancellationToken cancel) => _socket.ConnectAsync(endpoint, cancel);
        internal async Task<JsonNode?> Call(string method, JsonObject? parameters, CancellationToken cancel, string session = "")
        {
            int id = ++_id;
            var request = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            if (session.Length > 0) request["sessionId"] = session;
            await _socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, cancel);
            var bytes = new byte[65536];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(bytes.AsMemory(), cancel);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Fixture CDP closed.");
                    message.Write(bytes, 0, received.Count);
                } while (!received.EndOfMessage);
                JsonNode parsed = JsonNode.Parse(message.ToArray())!;
                if (parsed["id"]?.GetValue<int>() != id) continue;
                if (parsed["error"] is { } error) throw new IOException(error.ToJsonString());
                return parsed["result"];
            }
        }
        internal async Task<string> Eval(string session, string expression, CancellationToken cancel) =>
            (await Call("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true }, cancel, session))?
                ["result"]?["value"]?.ToString() ?? string.Empty;
        public void Dispose() => _socket.Dispose();
    }

    sealed class LocalSite : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        internal string Url { get; }
        internal LocalSite()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Serve();
        }
        async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Respond(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                string request = await reader.ReadLineAsync(_stop.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                string path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                if (path == "/hung-navigation") { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                if (path == "/held-image")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 100000\r\n\r\n"), _stop.Token);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }
                string html = path == "/original"
                    ? "<!doctype html><title>Original fixture</title><script>document.cookie='fixture_session=harmless;path=/';</script><h1>Original fixture</h1>"
                    : "<!doctype html><title>ARS session fixture</title><h1>Session available</h1>"
                        + "<input id=password type=password value=probe-password-hidden><button id=click style='width:220px;height:100px' onclick=\"document.querySelector('#count').textContent=++window.clicks\">Click</button>"
                        + "<p id=count>0</p><script>window.clicks=0</script><div style='height:2400px'></div>"
                        + (path == "/held" ? "<img src='/held-image'>" : "");
                byte[] body = Encoding.UTF8.GetBytes(html);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
