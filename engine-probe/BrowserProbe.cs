using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HiveMind.AgentWorkspaces;

internal static class BrowserProbe
{
    internal static void Run(WorkspaceRuntime runtime, Program.Client client, Action<bool, string> check,
        string output, List<int> observedProcesses)
    {
        WorkspaceControl control = runtime.Plane!;
        AgentDesktop desktop = runtime.Computer!;
        string folder = desktop.Folder!;
        const string privateDetail = "file:///C:/Users/private-person/token-secret.html";
        string protocolReceipt = WorkspaceBrowser.NavigationFailureReceipt(
            "{\"code\":-32000,\"message\":\"Failed at " + privateDetail + "\",\"data\":\"password-secret\"}");
        check(protocolReceipt.Contains("code -32000", StringComparison.Ordinal)
            && !protocolReceipt.Contains("private-person", StringComparison.Ordinal)
            && !protocolReceipt.Contains("secret", StringComparison.Ordinal),
            "Browser diagnostics preserve numeric protocol codes without echoing message or data secrets");
        check(WorkspaceBrowser.ProtocolFailureSummary("net::ERR_FILE_NOT_FOUND") == "net::ERR_FILE_NOT_FOUND"
            && !WorkspaceBrowser.ProtocolFailureSummary("net::ERR_FAILED " + privateDetail).Contains(privateDetail, StringComparison.Ordinal)
            && !WorkspaceBrowser.ProtocolFailureSummary("script failed at " + privateDetail).Contains(privateDetail, StringComparison.Ordinal),
            "Browser diagnostics retain valid Chromium errors and omit arbitrary page text and paths");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        CancellationToken cancel = deadline.Token;
        string first = Path.Combine(folder, "browser-one.html");
        string second = Path.Combine(folder, "browser-two.html");
        File.WriteAllText(first, """
            <!doctype html><html><head><meta charset="utf-8"><title>Deskweave local browser one</title>
            <style>html,body{margin:0;height:100%;background:rgb(0,90,200);color:white;font:18px sans-serif}</style></head>
            <body><h1>Deskweave first local page</h1><input id="entry" aria-label="Probe entry">
            <button id="apply" onclick="document.querySelector('#result').textContent=document.querySelector('#entry').value">Apply</button>
            <p id="result">Waiting for input</p><a id="next" href="browser-two.html" target="_blank">Open second page</a></body></html>
            """, System.Text.Encoding.UTF8);
        File.WriteAllText(second, """
            <!doctype html><html><head><meta charset="utf-8"><title>Deskweave local browser two</title>
            <style>html,body{margin:0;height:100%;background:rgb(0,160,60);color:white;font:18px sans-serif}</style></head>
            <body><h1>Deskweave second local page</h1><p>The second tab has its own content.</p></body></html>
            """, System.Text.Encoding.UTF8);
        check(File.Exists(WorkspaceBrowser.ChromePath), "A supported installed Chromium browser is available without a download");
        PipeOnly(check, output, observedProcesses);
        string missing = new Uri(Path.Combine(folder, "missing-page.html")).AbsoluteUri;
        JsonElement firstNavigation = client.Tool("browse", new { url = missing });
        File.WriteAllText(Path.Combine(output, "first-browse-response.json"), firstNavigation.GetRawText());
        check(Program.Client.Failed(firstNavigation),
            "First browser navigation to a missing local file reports unconfirmed navigation");
        string failureReceipt = Program.Client.Text(firstNavigation);
        check(failureReceipt.Contains("[navigation]", StringComparison.Ordinal)
            && failureReceipt.Contains("net::ERR_FILE_NOT_FOUND", StringComparison.Ordinal)
            && !failureReceipt.Contains(folder, StringComparison.OrdinalIgnoreCase)
            && !failureReceipt.Contains(missing, StringComparison.Ordinal),
            "Actual failed navigation returns its stage and native Chromium error without the local path");
        WorkspaceBrowser retained = control.Browser ?? throw new InvalidOperationException("Failed navigation discarded the attached browser.");
        bool retainedExited;
        try { using var process = Process.GetProcessById(retained.ProcessId); retainedExited = process.HasExited; }
        catch (ArgumentException) { retainedExited = true; }
        File.WriteAllText(Path.Combine(output, "failed-navigation.json"), JsonSerializer.Serialize(new
        {
            retained.Alive, retained.HasContent, retained.ProcessId, retained.Transport,
            retained.LastProtocolError, retained.InitialNavigationConfirmed, retainedExited,
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(retained.Alive, "Unconfirmed navigation retains a live browser");
        check(retained.HasContent, "Unconfirmed navigation retains its observed-content state");
        check(!Program.Client.Failed(client.Tool("browse", new { url = new Uri(first).AbsoluteUri })),
            "Packaged MCP browse opens the actual browser on a local fixture page");
        WorkspaceBrowser browser = control.Browser ?? throw new InvalidOperationException("Browser is missing after successful browse.");
        check(browser.LastProtocolError is null,
            "Successful navigation clears the previous failed navigation diagnostic");
        int browserPid = browser.ProcessId;
        check(ReferenceEquals(retained, browser) && retained.ProcessId == browserPid,
            "Valid navigation after a failed attempt reuses the attached browser instead of duplicating it");
        check(Program.Client.Text(client.Tool("page")).Contains("Deskweave first local page", StringComparison.Ordinal),
            "MCP page readback contains the requested local page");
        Settled(() => Shows(control, 0, 90, 200, Path.Combine(output, "browser-one.png")),
            "Independent native desktop capture agrees with the first browser page", check, cancel);
        const string typed = "Deskweave caf\u00e9 \u03bb";
        check(!Program.Client.Failed(client.Tool("page", new { action = "type", selector = "#entry", text = typed }))
            && !Program.Client.Failed(client.Tool("page", new { action = "click", selector = "#apply" })),
            "Real browser input accepts Unicode text and activates the page control");
        check(Program.Client.Text(client.Tool("page")).Contains(typed, StringComparison.Ordinal)
            && browser.Evaluate("document.querySelector('#result').textContent", cancel).GetAwaiter().GetResult() == typed,
            "Visible page result and direct DOM oracle agree on exact typed Unicode text");

        string expectedProfile = Path.Combine(folder, "chrome-profile");
        var chrome = new List<BrowserProcess>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WorkspaceBrowser.ChromePath)))
        {
            using (process)
            {
                if (!desktop.OwnsProcess(process.Id)) continue;
                observedProcesses.Add(process.Id);
                try { chrome.Add(Inspect(process)); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                {
                    chrome.Add(new(process.Id, "", -1, false, ex.GetType().Name));
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "browser-processes.json"), JsonSerializer.Serialize(new
        {
            executable = WorkspaceBrowser.ChromePath,
            version = FileVersionInfo.GetVersionInfo(WorkspaceBrowser.ChromePath).ProductVersion,
            expectedProfile,
            transport = browser.Transport,
            processes = chrome,
        }, new JsonSerializerOptions { WriteIndented = true }));
        BrowserProcess broker = chrome.Single(p => p.Pid == browserPid);
        check(broker.CommandLine.Contains("--user-data-dir=\"" + expectedProfile + "\"", StringComparison.OrdinalIgnoreCase),
            "Actual browser process command line points at this workspace's separate profile");
        check(!broker.CommandLine.Contains("--no-sandbox", StringComparison.OrdinalIgnoreCase),
            "Actual browser launch does not disable Chromium's sandbox");
        check(chrome.Any(p => p.CommandLine.Contains("--type=renderer", StringComparison.Ordinal)
                && !p.CommandLine.Contains("--no-sandbox", StringComparison.OrdinalIgnoreCase)
                && p.Integrity >= 0 && p.Integrity < broker.Integrity && p.Restricted),
            "An actual renderer has a restricted token and lower integrity than its browser broker");

        check(!Program.Client.Failed(client.Tool("page", new { action = "click", selector = "#next" })),
            "An actual link requests a new browser tab");
        Settled(() => control.Tabs(cancel).GetAwaiter().GetResult().Count >= 2,
            "The browser exposes both real local tabs", check, cancel);
        check(Program.Client.Text(client.Tool("page")).Contains("Deskweave second local page", StringComparison.Ordinal),
            "Page tools follow the newly active tab");
        Settled(() => Shows(control, 0, 160, 60, Path.Combine(output, "browser-two.png")),
            "Independent native capture agrees with the newly active tab", check, cancel);
        int firstNumber = control.Tabs(cancel).GetAwaiter().GetResult()
            .Single(t => t.Url.Contains("browser-one.html", StringComparison.Ordinal)).Number;
        check(!Program.Client.Failed(client.Tool("tab", new { tab = firstNumber }))
            && Program.Client.Text(client.Tool("page")).Contains(typed, StringComparison.Ordinal),
            "Selecting the original tab restores its retained input result");
        Settled(() => Shows(control, 0, 90, 200, Path.Combine(output, "browser-returned.png")),
            "Native capture confirms that selecting the first tab changed the displayed page", check, cancel);
        check(ReferenceEquals(browser, control.Browser) && control.Browser.ProcessId == browserPid,
            "Tab interaction preserves the same browser process and connection");

        // Reading a page from outside this PC does not stop the agent testing its app here: a refusal
        // only sent it to its own shell, which puts the app on the owner's screen.
        check(!Program.Client.Failed(client.Tool("browse", new { url = "data:text/html,<h1>Outside-page</h1>" }))
            && Program.Client.Text(client.Tool("page")).Contains("Outside-page", StringComparison.Ordinal)
            && !Program.Client.Failed(client.Tool("run", new { command = "echo still-runs", seconds = 20 })),
            "After reading an outside page the agent can still run its app in the workspace");
    }

    static void PipeOnly(Action<bool, string> check, string output, List<int> observedProcesses)
    {
        // A distinct, non-record desktop keeps this profile fresh without changing the two saved
        // workspace identities that the surrounding lifecycle gate verifies. The existing browser
        // override selects Edge for this same gate on a host that also has Chrome installed.
        string name = "PipeProbe-" + Guid.NewGuid().ToString("N")[..8];
        using AgentDesktop desktop = AgentDesktop.Create(name);
        string folder = desktop.Folder!;
        string profile = Path.Combine(folder, "chrome-profile");
        string page = Path.Combine(folder, "pipe-only.html");
        File.WriteAllText(page, """
            <!doctype html><title>Deskweave pipe-only fixture</title>
            <h1>Fresh inherited pipe page</h1><script>window.pipeClicks=0</script>
            <button id="add" onclick="document.querySelector('#result').textContent='pipe-click-'+(++window.pipeClicks)">Add</button>
            <p id="result">No clicks</p>
            """, System.Text.Encoding.UTF8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var elapsed = Stopwatch.StartNew();
        WorkspaceBrowser? browser = null;
        Process? process = null;
        BrowserProcess? broker = null;
        string? failure = null;
        string readback = "", clickCount = "";
        bool exited = false, portFile = false;
        try
        {
            check(!Directory.Exists(profile), "Pipe-only regression starts with a fresh browser profile");
            browser = WorkspaceBrowser.Start(desktop, new Uri(page).AbsoluteUri,
                allowPort: false, cancel: deadline.Token).GetAwaiter().GetResult();
            check(browser is { Transport: "pipe", InitialNavigationConfirmed: true },
                "Windows Chromium attaches and navigates over inherited pipes with port fallback disabled");
            if (browser is null) throw new InvalidOperationException(WorkspaceBrowser.StartFailureReason(name));
            process = Process.GetProcessById(browser.ProcessId);
            _ = process.Handle;
            check(desktop.OwnsProcess(browser.ProcessId)
                && string.Equals(process.MainModule?.FileName, WorkspaceBrowser.ChromePath, StringComparison.OrdinalIgnoreCase),
                "Pipe-only browser has the exact installed executable path and belongs to its workspace job");
            observedProcesses.Add(browser.ProcessId);
            broker = Inspect(process);
            check(broker.CommandLine.Contains("--remote-debugging-pipe ", StringComparison.Ordinal)
                && broker.CommandLine.Contains("--remote-debugging-io-pipes=", StringComparison.Ordinal)
                && broker.CommandLine.Contains("--user-data-dir=\"" + profile + "\"", StringComparison.OrdinalIgnoreCase)
                && !broker.CommandLine.Contains("--remote-debugging-port", StringComparison.Ordinal)
                && !broker.CommandLine.Contains("--no-sandbox", StringComparison.Ordinal),
                "Actual pipe-only launch names its Windows CDP handles and fresh profile without disabling the sandbox or opening a port");
            check(browser.Read(cancel: deadline.Token).GetAwaiter().GetResult()
                    .Contains("Fresh inherited pipe page", StringComparison.Ordinal),
                "Pipe-only page readback contains the requested local content");
            check(browser.Click("#add", deadline.Token).GetAwaiter().GetResult(),
                "Pipe-only browser accepts one trusted page click");
            readback = browser.Read(cancel: deadline.Token).GetAwaiter().GetResult();
            clickCount = browser.Evaluate("window.pipeClicks", deadline.Token).GetAwaiter().GetResult();
            check(readback.Contains("pipe-click-1", StringComparison.Ordinal) && clickCount == "1",
                "Pipe-only page text and DOM independently confirm exactly one click");
            portFile = File.Exists(Path.Combine(profile, "DevToolsActivePort"));
            check(!portFile, "Fresh pipe-only profile has no DevTools port stamp");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { browser?.Abandon(); }
            catch (Exception ex) { failure ??= "Pipe cleanup: " + ex; }
            try { exited = process is not null && process.WaitForExit(5000); }
            catch (Exception ex) { failure ??= "Process cleanup: " + ex; }
            File.WriteAllText(Path.Combine(output, "browser-pipe-only.json"), JsonSerializer.Serialize(new
            {
                executable = WorkspaceBrowser.ChromePath,
                version = FileVersionInfo.GetVersionInfo(WorkspaceBrowser.ChromePath).ProductVersion,
                desktop = name, profile, allowPort = false, transport = browser?.Transport,
                initialNavigationConfirmed = browser?.InitialNavigationConfirmed,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, processId = browser?.ProcessId,
                broker, readback, clickCount, portFile, exited, failure,
                startFailure = WorkspaceBrowser.StartFailureReason(name),
            }, new JsonSerializerOptions { WriteIndented = true }));
            process?.Dispose();
        }
        if (failure is not null) throw new InvalidOperationException("Pipe-only browser regression: " + failure);
        check(exited, "Pipe-only regression closes its exact owned browser after collecting evidence");
    }

    static void Settled(Func<bool> condition, string claim, Action<bool, string> check, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancel.ThrowIfCancellationRequested();
            if (condition()) { check(true, claim); return; }
            Thread.Sleep(150);
        }
        check(false, claim);
    }

    static bool Shows(WorkspaceControl control, byte red, byte green, byte blue, string output)
    {
        BitmapSource? frame = control.Shot(control.BrowserWindow);
        if (frame is null) return false;
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        rgb.Freeze();
        int stride = rgb.PixelWidth * 4;
        byte[] pixels = new byte[stride * rgb.PixelHeight];
        rgb.CopyPixels(pixels, stride, 0);
        int matched = 0, sampled = 0;
        for (int y = rgb.PixelHeight / 3; y < rgb.PixelHeight * 2 / 3; y += 4)
            for (int x = rgb.PixelWidth / 4; x < rgb.PixelWidth * 3 / 4; x += 4)
            {
                int at = y * stride + x * 4;
                sampled++;
                if (Math.Abs(pixels[at + 2] - red) <= 12 && Math.Abs(pixels[at + 1] - green) <= 12
                    && Math.Abs(pixels[at] - blue) <= 12) matched++;
            }
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(output);
        png.Save(stream);
        return sampled > 0 && matched * 100 / sampled > 80;
    }

    sealed record BrowserProcess(int Pid, string CommandLine, int Integrity, bool Restricted, string? ReadError);
    static BrowserProcess Inspect(Process process)
    {
        NtQueryInformationProcess(process.Handle, 60, 0, 0, out int length);
        if (length <= 0 || length > 131072) throw new InvalidOperationException("Unexpected process command-line size.");
        nint buffer = Marshal.AllocHGlobal(length);
        string command;
        try
        {
            if (NtQueryInformationProcess(process.Handle, 60, buffer, length, out _) < 0)
                throw new InvalidOperationException("Process command-line query failed.");
            ushort bytes = (ushort)Marshal.ReadInt16(buffer);
            nint chars = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            command = Marshal.PtrToStringUni(chars, bytes / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (!OpenProcessToken(process.Handle, 8, out nint token)) throw new Win32Exception();
        buffer = 0;
        try
        {
            GetTokenInformation(token, 25, 0, 0, out length);
            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, 25, buffer, length, out _)) throw new Win32Exception();
            string sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            int integrity = int.Parse(sid[(sid.LastIndexOf('-') + 1)..]);
            return new(process.Id, command, integrity, IsTokenRestricted(token), null);
        }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); CloseHandle(token); }
    }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int kind, nint information, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int kind, nint information, int length, out int needed);
    [DllImport("advapi32.dll")] static extern bool IsTokenRestricted(nint token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
