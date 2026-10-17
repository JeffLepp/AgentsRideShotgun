using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// Layer 3: ARS's page in Chromium, driven through its DevTools protocol, so an agent reads the
/// real page instead of a picture of it - exact text, links, forms, and everything below the fold.
///
/// Separate profile mode has two transports, in this order, chosen against collision rather than
/// attack. First an inherited pipe: nothing binds, nothing on the PC can reach it, and no program of
/// theirs can take it. If the pipe does not survive our CreateProcessAsUser launch, a port the
/// operating system chooses, on loopback, recorded in the workspace record. Never a fixed port.
///
/// By default, connects to a browser-authorized live owner session and drives only ARS-created tabs.
/// Separate browser profile opts into a process and profile inside the workspace folder. Neither
/// mode copies the owner's profile, cookies or password database.
/// </summary>
public sealed partial class WorkspaceBrowser : IDisposable
{
    /// <summary>
    /// A Chromium to drive, found rather than assumed. A hardcoded
    /// `C:\Program Files\Google\Chrome\Application\chrome.exe` is only where
    /// Chrome lands when it is installed for every user - the ordinary per-user install puts it under
    /// LOCALAPPDATA, and a PC with no Chrome at all would have no browser layer whatsoever.
    ///
    /// The owner's default browser comes first when it is a Chromium, so a Brave user gets Brave.
    /// Edge is last on purpose and matters most: it ships with Windows, it is Chromium, and it speaks
    /// the same DevTools protocol. It is what makes the browser layer work on a PC where nothing has
    /// been installed. Resolved once - a browser does not move while ARS is running.
    ///
    /// Finding nothing is remembered only briefly. ARS starts with Windows and stays in the
    /// tray, so a fresh PC with no browser installs one while we are running; a permanent miss left
    /// the browser layer dead until the app was restarted, with no hint why. Looking again is a
    /// handful of registry reads and File.Exists calls.
    /// </summary>
    public static string ChromePath
    {
        get
        {
            lock (Gate)
            {
                if (_browser is { Length: > 0 }) return _browser;
                if (_browser is not null && Environment.TickCount64 < _lookAgainAt) return _browser;
                _browser = FindBrowser() ?? string.Empty;
                if (_browser.Length == 0) _lookAgainAt = Environment.TickCount64 + 30_000;
                return _browser;
            }
        }
    }

    static readonly Lock Gate = new();
    static string? _browser;
    static long _lookAgainAt;

    static string? FindBrowser()
    {
        // An escape hatch for a PC where the browser is somewhere nobody expected, and the only way
        // the probes can drive Edge on a machine that also has Chrome.
        string? chosen = Environment.GetEnvironmentVariable("DESKWEAVE_WORKSPACE_BROWSER");
        if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen)) return chosen;

        // The owner's pick, Brave say. Edge as default is what Windows ships with, not a choice, so
        // it does not jump ahead of a Chrome they installed.
        if (DefaultChromium() is { } preferred && !IsEdge(preferred)) return preferred;

        foreach (Chromium browser in Chromiums)
            if (AppPath(browser.Exe) is { } registered && File.Exists(registered)) return registered;
        foreach (Chromium browser in Chromiums)
            foreach (string guess in InstallGuesses(browser))
                if (File.Exists(guess)) return guess;
        return null;
    }

    static string? AppPath(string exe)
    {
        foreach (Microsoft.Win32.RegistryKey root in new[]
                 { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            try
            {
                using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string path && path.Length > 0) return path.Trim('"');
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Why the last cold start for a workspace desktop did not come up - which stage lost the
    /// race, not just that it did. Keyed by <see cref="AgentDesktop.Name"/> so several workspaces
    /// starting Chrome at once never stomp on each other's diagnostic, and cleared the moment a
    /// start on that desktop succeeds. Before this, "DID NOT COME UP" looked the same whether
    /// Chrome was not installed, its process would not launch, its DevTools port never opened, or
    /// a page just never attached in time - four different problems with the same silence.
    /// </summary>
    internal static string? StartFailureReason(string desktopName) =>
        _startFailures.TryGetValue(desktopName, out string? reason) ? reason : null;

    static readonly ConcurrentDictionary<string, string> _startFailures = new();

    static void Record(string desktopName, string reason) => _startFailures[desktopName] = reason;

    readonly AgentDesktop _desktop;
    readonly AnonymousPipeServerStream? _toChrome;
    readonly AnonymousPipeServerStream? _fromChrome;
    readonly ClientWebSocket? _socket;
    readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _waiting = [];
    readonly Lock _gate = new();
    readonly CancellationTokenSource _stopping = new();
    string _session = string.Empty;
    int _nextId = 1;
    bool _disposed;

    WorkspaceBrowser(AgentDesktop desktop, string transport, int pid,
        AnonymousPipeServerStream? toChrome = null, AnonymousPipeServerStream? fromChrome = null,
        ClientWebSocket? socket = null, bool ownerSession = false)
    {
        _desktop = desktop;
        Transport = transport;
        ProcessId = pid;
        _toChrome = toChrome;
        _fromChrome = fromChrome;
        _socket = socket;
        UsesOwnerSession = ownerSession;
        _ = Task.Run(fromChrome is not null ? ReadPipe : ReadSocket);
    }

    /// <summary>"pipe", "loopback port 51234", or "your browser session". No endpoint token is logged.</summary>
    public string Transport { get; }

    public int ProcessId { get; }

    /// <summary>The live owner browser stays on the owner's desktop; only ARS-created tabs are driven.</summary>
    public bool UsesOwnerSession { get; }

    /// <summary>The newest DevTools protocol error, retained for an honest diagnostic receipt.</summary>
    public string? LastProtocolError { get; private set; }

    /// <summary>
    /// Whether this browser has ever loaded a page. A browser warmed up at start sits on about:blank
    /// with nothing on it, and a photograph of the screen it is on carries no page content - so it
    /// must not put the mission into the state where commands are refused. The moment it navigates
    /// anywhere real this is true for good, and every rule that guards page content applies again.
    /// </summary>
    public bool HasContent { get; private set; }

    /// <summary>Attaching DevTools is separate from confirming the requested first page loaded.</summary>
    internal bool InitialNavigationConfirmed { get; private set; }

    /// <summary>The file in the workspace folder that says a browser was really used here.</summary>
    internal const string UsedMark = "browser-used";

    /// <summary>
    /// Leaves the mark <see cref="WorkspaceControl.Prewarm"/> reads. Best effort: a workspace that
    /// cannot write it simply pays the cold start on its next browse, which is what it did before.
    /// </summary>
    void Used()
    {
        try
        {
            if (_desktop.Folder is not { } folder) return;
            string mark = Path.Combine(folder, UsedMark);
            if (!File.Exists(mark)) File.WriteAllBytes(mark, []);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool Blank(string url)
    {
        string where = url.Trim();
        return where.Length == 0 || where.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chrome's window, so layer 2 can photograph the page at any time.</summary>
    public nint Window
    {
        get
        {
            if (UsesOwnerSession) return 0;
            // The class alone also matches Electron apps (VS Code, Slack) in the same workspace.
            foreach (AgentWindow window in _desktop.Windows())
            {
                if (!window.ClassName.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) continue;
                Native.GetWindowThreadProcessId(window.Handle, out int owner);
                if (owner == ProcessId) return window.Handle;
            }
            return 0;
        }
    }

    /// <summary>
    /// Starts the workspace's browser and attaches to its first page. Returns null when Chrome is
    /// not installed or never came up after a retry; the caller then works in pixels, which still
    /// works. <see cref="StartFailureReason"/> says exactly which stage lost the race.
    /// </summary>
    public static async Task<WorkspaceBrowser?> Start(AgentDesktop desktop, string url,
        bool allowPort = true, CancellationToken cancel = default, long lease = 0)
    {
        _startFailures.TryRemove(desktop.Name, out _);
        // A browser the owner has not connected yet, or a connection they declined, still browses:
        // in ARS's own profile, as before live sessions existed, without their sign-ins.
        if (!AppSettingsStore.Current.SeparateBrowserProfile
            && await ConnectOwner(desktop, url, cancel).ConfigureAwait(false) is { } owner) return owner;
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(ChromePath))
        {
            Record(desktop.Name, "no supported Chromium browser is installed");
            return null;
        }
        if (desktop.Folder is null)
        {
            Record(desktop.Name, "the workspace has no folder yet to hold a browser profile");
            return null;
        }
        string profile = Path.Combine(desktop.Folder, "chrome-profile");
        if (!DisableWorkspacePasswordSaving(profile))
        {
            Record(desktop.Name, "the separate browser profile could not disable password saving");
            return null;
        }
        // The browser now starts with ordinary user permissions, so its own renderer sandbox
        // stays enabled, as it does on the owner's desktop. Keep a separate browser profile.
        // The crash-restore bubble is what a browser shows after being killed, which is exactly how
        // a workspace browser ends. An agent should not have to recognise and dismiss it to see the
        // page it just asked for. Maximized, then filled to the whole screen once it is up, because
        // the workspace screen is what the corner shows: a page filling it is readable there.
        // Muted by Chrome itself: the workspace's audio sweep cannot reach the browser's own job, and
        // muting chrome.exe's session through Windows is remembered for the owner's Chrome as well.
        string common = $"--no-first-run --no-default-browser-check --hide-crash-restore-bubble --start-maximized " +
            $"--disable-session-crashed-bubble --restore-last-session=false --mute-audio " +
            $"--user-data-dir=\"{profile}\" ";

        cancel.ThrowIfCancellationRequested();
        WorkspaceBrowser? pipe = await TryPipe(desktop, common, url, cancel, lease).ConfigureAwait(false);
        if (pipe is not null)
        {
            // The normal-token launch can make Chromium's inherited DevTools pipe work on machines
            // where the former lowered-token launch could not. Chromium may still create a New Tab
            // target before the command-line URL, so explicitly navigate the target we attached to.
            // This keeps the page the agent reads aligned with the address Start was asked to open.
            return await NavigateStarted(pipe, url, cancel).ConfigureAwait(false);
        }
        if (!allowPort) return null;
        cancel.ThrowIfCancellationRequested();
        WorkspaceBrowser? port = await TryPort(desktop, common, profile, url, cancel, lease, attempt: 1)
            .ConfigureAwait(false);
        if (port is null && !cancel.IsCancellationRequested)
        {
            // One bounded retry after TryPort has stopped its unsuccessful launch. A retry is
            // recovery, not evidence that the original failure was harmless or caused by load.
            // Keep the failed stage visible if this attempt also fails.
            port = await TryPort(desktop, common, profile, url, cancel, lease, attempt: 2).ConfigureAwait(false);
        }
        return port is null ? null : await NavigateStarted(port, url, cancel).ConfigureAwait(false);
    }

    static async Task<WorkspaceBrowser> NavigateStarted(WorkspaceBrowser browser, string url,
        CancellationToken cancel)
    {
        try
        {
            if (!browser.UsesOwnerSession) await browser.CloseLeftovers(cancel).ConfigureAwait(false);
            browser.InitialNavigationConfirmed = await browser.Go(url, cancel).ConfigureAwait(false);
            // A warm-up start is for later, not for now. Leaving it in front put Chrome over the
            // window the agent had just opened, so the next look photographed an empty browser and
            // the agent had to notice and raise its own window again. Sized, then parked at the
            // back; the first real navigation in Go raises it.
            browser._desktop.Fill(browser.Window);
            if (Blank(url)) browser._desktop.Behind(browser.Window);
            return browser;
        }
        catch
        {
            // No control plane has adopted this browser yet. Releasing only DevTools would leave
            // its process holding the profile and prevent a subsequent explicit retry attaching.
            browser.Abandon();
            throw;
        }
    }

    /// <summary>
    /// Chromium on Windows adopts raw inherited HANDLE values named by remote-debugging-io-pipes;
    /// its CRT descriptors 3/4 are not populated by assigning standard input/output. Keep CDP
    /// separate from stdout/stderr, then give startup the same attachment budget as the port route.
    /// </summary>
    static async Task<WorkspaceBrowser?> TryPipe(AgentDesktop desktop, string common, string url,
        CancellationToken cancel, long lease)
    {
        var toChrome = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChrome = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        WorkspaceBrowser? browser = null;
        int pid = 0;
        bool attached = false;
        try
        {
            nint read = toChrome.ClientSafePipeHandle.DangerousGetHandle();
            nint write = fromChrome.ClientSafePipeHandle.DangerousGetHandle();
            // Chromium's AdoptPipes parses uint32 decimal values (HANDLE values are valid when
            // transferred as 32 bits between Windows processes). Read first, then write.
            string ioPipes = unchecked((uint)(nuint)read).ToString(CultureInfo.InvariantCulture) + ","
                + unchecked((uint)(nuint)write).ToString(CultureInfo.InvariantCulture);
            cancel.ThrowIfCancellationRequested();
            pid = desktop.LaunchBrowser(ChromePath,
                common + "--remote-debugging-pipe --remote-debugging-io-pipes=" + ioPipes + " " + Quote(url),
                (read, write), lease);
            // Only the child keeps its client ends. Parent copies would keep the reader from EOF.
            toChrome.DisposeLocalCopyOfClientHandle();
            fromChrome.DisposeLocalCopyOfClientHandle();
            if (pid == 0)
            {
                Record(desktop.Name, "pipe launch: the browser process itself would not launch");
                return null;
            }
            browser = new WorkspaceBrowser(desktop, "pipe", pid, toChrome, fromChrome);
            if (!Blank(url)) browser.HasContent = true;
            if (await browser.Attach(cancel, TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            {
                cancel.ThrowIfCancellationRequested();
                attached = true;
                _startFailures.TryRemove(desktop.Name, out _);
                return browser;
            }
            cancel.ThrowIfCancellationRequested();
            Record(desktop.Name, "pipe attachment: no page attached within 20s; "
                + ProtocolFailureSummary(browser.LastProtocolError));
            return null;
        }
        finally
        {
            if (!attached)
            {
                browser?.Dispose();
                toChrome.Dispose();
                fromChrome.Dispose();
                // Preserve a successfully returned browser. A failed/cancelled attempt must release
                // its profile before an explicit port fallback, and can stop only its workspace job.
                if (pid > 0 && desktop.OwnsProcess(pid)) Stop(pid);
            }
        }
    }

    static void Stop(int pid)
    {
        try { using Process chrome = Process.GetProcessById(pid); chrome.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the state we wanted.
        }
        // Chrome's own shutdown has to finish releasing the profile lock before the next one starts.
        Thread.Sleep(1500);
    }

    /// <summary>Whether a launched process is still there, the same way <see cref="Alive"/> checks
    /// the attached browser's - a process that already exited did not lose a timing race, it
    /// crashed, and the two want different fixes.</summary>
    static bool ProcessAlive(int pid)
    {
        try { using Process chrome = Process.GetProcessById(pid); return !chrome.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    static async Task<WorkspaceBrowser?> TryPort(AgentDesktop desktop, string common, string profile,
        string url, CancellationToken cancel, long lease, int attempt)
    {
        string stamp = Path.Combine(profile, "DevToolsActivePort");
        try { if (File.Exists(stamp)) File.Delete(stamp); } catch (IOException) { }

        // Port 0 means the operating system picks one that is free. Nothing here guesses a number,
        // which is exactly the collision the owner asked to avoid - and rules port allocation out
        // as a cause of the load failure this method retries against: every process gets its own
        // OS-assigned port regardless of how many other Chrome processes are starting alongside it.
        cancel.ThrowIfCancellationRequested();
        int pid = desktop.LaunchBrowser(ChromePath, common + "--remote-debugging-port=0 " + Quote(url), lease: lease);
        if (pid == 0)
        {
            Record(desktop.Name, $"attempt {attempt}: the browser process itself would not launch");
            return null;
        }

        bool attached = false;
        WorkspaceBrowser? browser = null;
        ClientWebSocket? socket = null;
        try
        {
            string first = string.Empty, path = string.Empty;
            for (int wait = 0; wait < 80 && first.Length == 0; wait++)
            {
                await Task.Delay(250, cancel).ConfigureAwait(false);
                try
                {
                    if (!File.Exists(stamp)) continue;
                    string[] lines = File.ReadAllLines(stamp);
                    if (lines.Length >= 2) { first = lines[0]; path = lines[1]; }
                }
                catch (IOException) { }
            }
            if (!int.TryParse(first, out int port))
            {
                // Distinguishes a crash from a machine that is simply still busy 20s in - a fixed
                // retry count reads identically for both unless something asks the process itself.
                Record(desktop.Name, ProcessAlive(pid)
                    ? $"attempt {attempt}: the browser was still starting after 20s but never wrote DevToolsActivePort"
                    : $"attempt {attempt}: the browser process exited before it opened a DevTools port");
                return null;
            }

            socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{path}"), cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                socket.Dispose();
                Record(desktop.Name, $"attempt {attempt}: the DevTools WebSocket handshake failed ({ex.GetType().Name})");
                return null;
            }
            browser = new WorkspaceBrowser(desktop, $"loopback port {port}", pid, socket: socket);
            if (await browser.Attach(cancel).ConfigureAwait(false))
            {
                attached = true;
                _startFailures.TryRemove(desktop.Name, out _);
                return browser;
            }
            Record(desktop.Name, $"attempt {attempt}: connected to DevTools but no page attached within 20s; "
                + ProtocolFailureSummary(browser.LastProtocolError));
            return null;
        }
        finally
        {
            if (!attached)
            {
                browser?.Dispose();
                socket?.Dispose();
                if (desktop.OwnsProcess(pid)) Stop(pid);
            }
        }
    }

    // "--" ends Chrome's switches, so a url such as "--remote-debugging-port=9222" stays a url.
    static string Quote(string url) => "-- \"" + url.Replace("\"", string.Empty) + "\"";

    // Applies only to ARS's opt-in profile. The owner's browser settings are never modified.
    // Chromium defines credentials_enable_service as the switch controlling credential saving:
    // components/password_manager/core/common/password_manager_pref_names.h.
    static bool DisableWorkspacePasswordSaving(string profile)
    {
        try
        {
            string folder = Path.Combine(profile, "Default");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Preferences");
            JsonObject preferences = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
                : new JsonObject();
            preferences["credentials_enable_service"] = false;
            string temporary = path + ".ars-tmp";
            File.WriteAllText(temporary, preferences.ToJsonString());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the page and attaches flat, so both transports look identical above this line: one
    /// browser connection, a session id on every page message.
    /// </summary>
    async Task<bool> Attach(CancellationToken cancel, TimeSpan? budget = null)
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        patience.CancelAfter(budget ?? TimeSpan.FromSeconds(20));
        try
        {
            for (int tries = 0; tries < 20; tries++)
            {
                // Through the same resolution every later call uses, so the tab attached to at start
                // is the tab on screen rather than whichever one Chrome happened to list first.
                if (await Follow(patience.Token).ConfigureAwait(false) && _session.Length > 0) return true;
                await Task.Delay(500, patience.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException
            or JsonException or InvalidOperationException)
        {
        }
        return false;
    }

    /// <summary>Waits for a committed, usable document; background requests may continue loading.</summary>
    public async Task<bool> Go(string url, CancellationToken cancel = default)
    {
        LastProtocolError = null;
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        patience.CancelAfter(NavigationBudget);
        CancellationToken bounded = patience.Token;
        try
        {
        if (!await Follow(bounded).ConfigureAwait(false))
        {
            bounded.ThrowIfCancellationRequested();
            LastProtocolError ??= "The ARS browser tab is no longer available.";
            return false;
        }
        // A failed or cancelled navigation can still put partial page content on screen.
        if (!Blank(url))
        {
            HasContent = true;
            // A page someone asked for belongs on the screen the corner shows, whether this browser
            // was parked by the warm-up or is already up. Also the record that this workspace really
            // browses, which is what decides whether waking it warms a browser at all.
            Used();
            _desktop.Fill(Window);
            _desktop.Arrange(Window, WindowArrangement.Front);
        }
        JsonNode? sent = await Call("Page.navigate", new JsonObject { ["url"] = url }, bounded, _session)
            .ConfigureAwait(false);
        bounded.ThrowIfCancellationRequested();
        if (sent is null) return false;
        if (sent["errorText"]?.GetValue<string>() is { Length: > 0 } rejected)
        {
            LastProtocolError = rejected;
            return false;
        }
        if (sent["isDownload"]?.GetValue<bool>() == true)
        {
            LastProtocolError = "The navigation became a download.";
            return false;
        }
        string? loader = sent["loaderId"]?.GetValue<string>();
        while (true)
        {
            await Task.Delay(100, bounded).ConfigureAwait(false);
            // An old document can still be complete before navigation commits. Confirm the
            // navigation's loader before reading readiness; redirects keep this loader ID.
            if (loader is { Length: > 0 })
            {
                JsonNode? tree = await Call("Page.getFrameTree", null, bounded, _session).ConfigureAwait(false);
                bounded.ThrowIfCancellationRequested();
                if (tree?["frameTree"]?["frame"]?["loaderId"]?.GetValue<string>() != loader) continue;
            }
            string ready = await On(_session, "document.readyState", bounded).ConfigureAwait(false);
            bounded.ThrowIfCancellationRequested();
            if (ready is "interactive" or "complete")
            {
                LastProtocolError = null;
                return true;
            }
        }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            LastProtocolError = "The navigation did not become usable within 12 seconds.";
            return false;
        }
    }

    internal static TimeSpan NavigationBudget => TimeSpan.FromSeconds(12);

    /// <summary>
    /// The page as an agent should see it: its text, and the things on it that can be acted on.
    /// This is the whole reason layer 3 exists - measured, Chrome's element tree
    /// published 42 elements for a page whose text is right here.
    ///
    /// Each control leads with a selector that matches it and nothing else, so it can go straight
    /// into page click or type. A bare `a "Today"` left an agent to invent one. Built
    /// from what the page already has - an id, a distinctive attribute, else its place in the
    /// tree - without marking the page, which is the app being tested.
    /// </summary>
    /// <param name="scope">Only the part of the page under this CSS selector, text and controls both.</param>
    /// <param name="offset">Where to continue the text. Controls are listed only on the first page.</param>
    public Task<string> Read(int limit = 12000, CancellationToken cancel = default, string? scope = null,
        int offset = 0, bool controls = true) => Evaluate($$"""
        (() => {
          const head = document.title + '\n' + location.href + '\n\n';
          let root = document.body;
          const scope = {{Json(scope ?? "")}};
          if (scope) {
            try { root = document.querySelector(scope); } catch { return head + 'That selector is not valid CSS.'; }
            if (!root) return head + 'Nothing on the page matched ' + scope + '.';
          }
          const one = sel => { try { return document.querySelectorAll(sel).length === 1; } catch { return false; } };
          const quote = v => '"' + v.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
          const pick = el => {
            const tag = el.tagName.toLowerCase();
            if (el.id && one('#' + CSS.escape(el.id))) return '#' + CSS.escape(el.id);
            for (const name of ['name', 'href', 'aria-label', 'placeholder', 'type', 'value', 'title']) {
              if (name === 'value' && el.matches('input[type=password]')) continue;
              const v = el.getAttribute(name);
              if (v && v.length <= 100 && one(tag + '[' + name + '=' + quote(v) + ']')) return tag + '[' + name + '=' + quote(v) + ']';
            }
            let path = '';
            for (let node = el; node && node.nodeType === 1 && node !== document.body; node = node.parentElement) {
              if (node !== el && node.id && one('#' + CSS.escape(node.id))) return '#' + CSS.escape(node.id) + ' > ' + path;
              const kin = node.parentElement ? [...node.parentElement.children].filter(c => c.tagName === node.tagName) : [node];
              const part = node.tagName.toLowerCase() + (kin.length > 1 ? ':nth-of-type(' + (kin.indexOf(node) + 1) + ')' : '');
              path = path ? part + ' > ' + path : part;
            }
            return 'body > ' + path;
          };
          // Measured: a YouTube results page came back as 63k characters and overflowed the
          // agent's tool output. The text now says when it was cut and how to go on, and the
          // controls say how many were left out, so a smaller page costs nothing silently.
          const all = root ? root.innerText : '';
          const text = all.slice({{offset}}, {{offset}} + {{limit}});
          const rest = all.length - {{offset}} - text.length;
          let out = head + text + (rest > 0 ? '\n\n[' + rest + ' more characters: read again with offset='
                    + ({{offset}} + text.length) + ', or narrow it with selector]' : '');
          if (!{{(controls && offset == 0 ? "true" : "false")}} || !root) return out;
          const seen = [];
          let skipped = 0;
          for (const el of root.querySelectorAll('a[href],button,input,textarea,select,[role=button]')) {
            const box = el.getBoundingClientRect();
            if (box.width === 0 || box.height === 0) continue;
            if (seen.length >= 120) { skipped++; continue; }
            const what = (el.innerText || (el.matches('input[type=password]') ? '' : el.value) || el.placeholder || el.name ||
                          el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 80);
            seen.push(pick(el) + '  "' + what + '"' + (el.href ? ' -> ' + el.href.slice(0, 150) : ''));
          }
          return out + '\n\n-- controls --\n' + seen.join('\n') +
                 (skipped ? '\n[' + skipped + ' more controls: narrow it with selector]' : '');
        })()
        """, cancel);

    /// <summary>
    /// Clicks the first thing matching a CSS selector. Returns false when nothing matched.
    ///
    /// This used to call the element's own `click()`, which was a real defect and not only a
    /// detection one: a click from script carries `isTrusted: false`, and a great many widgets -
    /// every consent gate, every challenge checkbox, some menus - are written to ignore exactly
    /// that. The tool reported success and nothing had happened, so the agent clicked again, and
    /// repeated interaction that never lands is itself what escalates a site from letting you pass
    /// to making you prove yourself.
    ///
    /// The selector now only finds the element and gives back where it is. The click itself goes
    /// through the browser's own input pipeline, which is where real mouse input enters, so the
    /// page sees an ordinary trusted click - the same route Puppeteer and Playwright take.
    /// </summary>
    public async Task<bool> Click(string selector, CancellationToken cancel = default)
    {
        (double x, double y) = await Locate(selector, cancel).ConfigureAwait(false);
        if (double.IsNaN(x)) return false;

        return await ClickPoint(x, y, "left", 1, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Focuses by ordinary mouse input, then inserts Unicode through Chromium's input pipeline.
    /// This fires input events without assigning value in script or delaying every character.
    /// Use computer/keypress for applications that specifically need keyboard shortcuts/events.
    /// </summary>
    public async Task<bool> Type(string selector, string text, CancellationToken cancel = default)
    {
        if (!await Click(selector, cancel).ConfigureAwait(false)) return false;
        return await InsertText(text, cancel).ConfigureAwait(false);
    }

    /// <summary>Where an element is in the page, or NaN when nothing matched or it has no box.</summary>
    async Task<(double X, double Y)> Locate(string selector, CancellationToken cancel)
    {
        string at = await Evaluate($$"""
            (() => {
              const e = document.querySelector({{Json(selector)}});
              if (!e) return '';
              e.scrollIntoView({block: 'center'});
              const box = e.getBoundingClientRect();
              if (!box.width || !box.height) return '';
              return (box.left + box.width / 2) + ',' + (box.top + box.height / 2);
            })()
            """, cancel).ConfigureAwait(false);
        string[] pair = at.Split(',');
        if (pair.Length != 2 || !double.TryParse(pair[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(pair[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double y)) return (double.NaN, double.NaN);
        return (x, y);
    }

    /// <summary>Runs an expression in the page and returns its value as text.</summary>
    public async Task<string> Evaluate(string expression, CancellationToken cancel = default)
    {
        await Follow(cancel).ConfigureAwait(false);
        JsonNode? answer = await Call("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        }, cancel, _session).ConfigureAwait(false);
        JsonNode? value = answer?["result"]?["value"];
        if (value is null && answer?["exceptionDetails"] is JsonNode exception)
            LastProtocolError = exception.ToJsonString();
        return value is null ? string.Empty : value.ToString();
    }

    static string Json(string text) => JsonSerializer.Serialize(text);

    async Task<JsonNode?> Call(string method, JsonObject? parameters, CancellationToken cancel,
        string session = "")
    {
        if (_disposed || cancel.IsCancellationRequested) return null;
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stopping.Token);
        patience.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken bounded = patience.Token;
        int id;
        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { id = _nextId++; _waiting[id] = done; }

        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        if (session.Length > 0) message["sessionId"] = session;

        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _send.WaitAsync(bounded).ConfigureAwait(false);
            try
            {
                if (_toChrome is not null)
                {
                    // Serialize the complete NUL-delimited frame against concurrent page observations.
                    byte[] framed = new byte[payload.Length + 1];
                    payload.CopyTo(framed, 0);
                    await _toChrome.WriteAsync(framed, bounded).ConfigureAwait(false);
                    await _toChrome.FlushAsync(bounded).ConfigureAwait(false);
                }
                else if (_socket is not null)
                    await _socket.SendAsync(payload, WebSocketMessageType.Text, true, bounded).ConfigureAwait(false);
                else return null;
            }
            finally { _send.Release(); }

            return await done.Task.WaitAsync(bounded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            if (ex is OperationCanceledException && !cancel.IsCancellationRequested
                && !_stopping.IsCancellationRequested)
                LastProtocolError = "The DevTools command timed out.";
            lock (_gate) _waiting.Remove(id);
            return null;
        }
        finally { lock (_gate) _waiting.Remove(id); }
    }

    async Task ReadPipe()
    {
        var buffer = new byte[16384];
        var message = new MemoryStream();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int read = await _fromChrome!.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0) break;
                for (int at = 0; at < read; at++)
                {
                    if (buffer[at] != 0) { message.WriteByte(buffer[at]); continue; }
                    Deliver(Encoding.UTF8.GetString(message.ToArray()));
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        Fail();
    }

    async Task ReadSocket()
    {
        var buffer = new byte[16384];
        var message = new MemoryStream();
        try
        {
            while (!_stopping.IsCancellationRequested && _socket!.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult got =
                    await _socket.ReceiveAsync(buffer.AsMemory(), _stopping.Token).ConfigureAwait(false);
                if (got.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, got.Count);
                if (!got.EndOfMessage) continue;
                Deliver(Encoding.UTF8.GetString(message.ToArray()));
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
        Fail();
    }

    void Deliver(string text)
    {
        if (text.Length == 0) return;
        JsonNode? message;
        try { message = JsonNode.Parse(text); }
        catch (JsonException) { return; }
        DiscoverOwnedPopup(message);
        // Other events are not retained, especially metadata about existing owner tabs.
        if (message?["id"] is not JsonNode number) return;
        TaskCompletionSource<JsonNode?>? waiting;
        lock (_gate)
        {
            if (!_waiting.Remove(number.GetValue<int>(), out waiting)) return;
        }
        // An error object is not a successful protocol result. In particular a JSON character
        // serialized as a string made every key event fail while Type still reported success.
        if (message["error"] is JsonNode error)
        {
            LastProtocolError = error.ToJsonString();
            waiting.TrySetResult(null);
        }
        else
        {
            waiting.TrySetResult(message["result"]);
        }
    }

    void Fail()
    {
        // The reader loop only ends when the connection is gone. Everything above this line has to
        // know that, or a browse against a browser the owner closed fails forever with no reason.
        _broken = true;
        if (!_disposed && LastProtocolError is null)
            LastProtocolError = "DevTools connection closed before the command completed.";
        List<TaskCompletionSource<JsonNode?>> stranded;
        lock (_gate) { stranded = [.. _waiting.Values]; _waiting.Clear(); }
        foreach (TaskCompletionSource<JsonNode?> one in stranded) one.TrySetResult(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseOwnedTargets();
        _disposed = true;
        _stopping.Cancel();
        Fail();
        _toChrome?.Dispose();
        _fromChrome?.Dispose();
        _socket?.Dispose();
        _stopping.Dispose();
        // The process itself belongs to the workspace's job object and dies when the workspace does.
    }

    /// <summary>Release an unadopted or disconnected browser before explicitly starting another.</summary>
    internal void Abandon()
    {
        Dispose();
        if (_desktop.OwnsProcess(ProcessId)) Stop(ProcessId);
    }
}
