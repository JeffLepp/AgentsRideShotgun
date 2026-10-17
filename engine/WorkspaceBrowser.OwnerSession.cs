using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace Deskweave.AgentWorkspaces;

public sealed partial class WorkspaceBrowser
{
    // IDs created by this connection, plus their popup descendants. Never adopt existing user tabs.
    readonly ConcurrentDictionary<string, byte> _ownedTargets = new(StringComparer.Ordinal);

    public static string OwnerBrowserConnectionHint =>
        $"Open {OwnerSetupUrl}, enable remote debugging, then allow the connection in your browser. "
        + "ARS uses your live sign-ins without copying passwords, cookies or your browser profile.";

    static string OwnerSetupUrl => (Kind(OwnerBrowserPath) ?? Chromiums[0]).Inspect;

    /// <summary>Called only by the owner's Settings button. The browser owns all permission UI.</summary>
    public static bool OpenOwnerBrowserSetup()
    {
        string path = OwnerBrowserPath;
        if (!File.Exists(path)) return false;
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            start.ArgumentList.Add(OwnerSetupUrl);
            using Process? launched = Process.Start(start);
            return launched is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // The owner's live sign-ins are in their default browser, even when that is Edge. The endpoint
    // metadata is the only browser file read.
    static string OwnerBrowserPath
    {
        get
        {
            // The owner can explicitly select an installed Chromium, without an alternate profile.
            string? chosen = Environment.GetEnvironmentVariable("DESKWEAVE_WORKSPACE_BROWSER");
            if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen)) return chosen;
            return DefaultChromium() ?? RunningChromium() ?? ChromePath;
        }
    }

    static async Task<WorkspaceBrowser?> ConnectOwner(AgentDesktop desktop, string url, CancellationToken cancel)
    {
        string path = OwnerBrowserPath;
        if (!File.Exists(path))
        {
            Record(desktop.Name, "no supported Chromium browser is installed");
            return null;
        }
        string profile = (Kind(path) ?? Chromiums[0]).Profile;
        Uri? endpoint = null;
        try
        {
            // DevToolsActivePort contains only a loopback port and browser WebSocket path. Do not
            // touch Login Data, Cookies, Local State, Preferences or any user's profile database.
            string stamp = Path.Combine(profile, "DevToolsActivePort");
            if (File.Exists(stamp) && new FileInfo(stamp).Length <= 1024)
                endpoint = OwnerEndpoint(await File.ReadAllTextAsync(stamp, cancel).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (endpoint is null)
        {
            Record(desktop.Name, "your browser has not enabled a live connection. " + OwnerBrowserConnectionHint);
            return null;
        }
        return await ConnectOwnerEndpoint(desktop, endpoint, url, cancel).ConfigureAwait(false);
    }

    // Also exercised against a real Chromium fixture. No alternate profile is supplied or copied;
    // the endpoint's existing default browser context is the source of every live sign-in.
    internal static async Task<WorkspaceBrowser?> ConnectOwnerEndpoint(AgentDesktop desktop, Uri endpoint,
        string url, CancellationToken cancel)
    {
        if (endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || !endpoint.IsLoopback
            || OwnerEndpoint(endpoint.Port + "\n" + endpoint.AbsolutePath) is null)
            throw new ArgumentException("A local browser DevTools endpoint is required.", nameof(endpoint));
        WorkspaceBrowser? browser = null;
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "ARS");
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        patience.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Chrome M144+ asks the owner to allow this handshake. No automation may approve it.
            await socket.ConnectAsync(endpoint, patience.Token).ConfigureAwait(false);
            browser = new WorkspaceBrowser(desktop, "your browser session", 0, socket: socket, ownerSession: true);
            if (await browser.Call("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, patience.Token)
                    .ConfigureAwait(false) is null
                || !await browser.NewTab("about:blank", patience.Token).ConfigureAwait(false))
            {
                Record(desktop.Name, "your browser connected but did not create an ARS tab. " + OwnerBrowserConnectionHint);
                browser.Dispose();
                return null;
            }
            _startFailures.TryRemove(desktop.Name, out _);
            return await NavigateStarted(browser, url, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            browser?.Dispose();
            socket.Dispose();
            cancel.ThrowIfCancellationRequested();
            Record(desktop.Name, "your browser connection was declined, closed or not allowed within 30 seconds. "
                + OwnerBrowserConnectionHint);
            return null;
        }
    }

    internal static Uri? OwnerEndpoint(string stamp)
    {
        string[] lines = stamp.Trim().Split('\n');
        if (lines.Length != 2 || !int.TryParse(lines[0].Trim(), out int port) || port is < 1 or > 65535) return null;
        string path = lines[1].Trim();
        const string prefix = "/devtools/browser/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length
            || path.Length > 256 || !path[prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        return new Uri($"ws://127.0.0.1:{port}{path}");
    }

    void DiscoverOwnedPopup(JsonNode? message)
    {
        if (!UsesOwnerSession || message?["method"]?.GetValue<string>() != "Target.targetCreated") return;
        JsonNode? target = message["params"]?["targetInfo"];
        if (target?["type"]?.GetValue<string>() == "page"
            && target["openerId"]?.GetValue<string>() is { } opener && _ownedTargets.ContainsKey(opener)
            && target["targetId"]?.GetValue<string>() is { } id) _ownedTargets.TryAdd(id, 0);
    }

    void CloseOwnedTargets()
    {
        if (!UsesOwnerSession || _broken) return;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (string target in _ownedTargets.Keys)
        {
            if (patience.IsCancellationRequested) break;
            try
            {
                Call("Target.closeTarget", new JsonObject { ["targetId"] = target }, patience.Token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
        }
        _ownedTargets.Clear();
    }
}
