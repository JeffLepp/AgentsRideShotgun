using System.Text.Json.Nodes;

namespace HiveMind.AgentWorkspaces;

/// <summary>One page open in the workspace browser, as the owner and the agent see it.</summary>
/// <param name="Number">What the tab tools take. Stable for as long as the tab list is.</param>
/// <param name="Active">The tab actually on screen, which is the one a screenshot shows.</param>
/// <param name="Selected">The tab the page tools are currently attached to.</param>
public sealed record BrowserTab(int Number, string TargetId, string Title, string Url,
    bool Active, bool Selected);

public sealed partial class WorkspaceBrowser
{
    /// <summary>
    /// The tab the page tools use, when the agent has picked one on purpose. Empty means follow
    /// whichever tab is on screen, which is the default and the only setting that keeps the pixels
    /// and the page text talking about the same thing.
    /// </summary>
    string _pinned = string.Empty;

    readonly Dictionary<string, string> _sessions = [];

    /// <summary>The tab the page tools are attached to right now, for status and the log.</summary>
    public string SelectedTab { get; private set; } = string.Empty;

    /// <summary>True when the agent pinned a tab rather than following the visible one.</summary>
    public bool TabPinned => _pinned.Length > 0;

    /// <summary>
    /// Whether this connection is still worth using. A browser whose process has gone, or whose
    /// DevTools connection has dropped, is not a browser to keep navigating; before this, a closed
    /// browser left a dead object in place and every later browse failed against it forever.
    /// </summary>
    public bool Alive
    {
        get
        {
            if (_disposed || _broken) return false;
            try
            {
                using var chrome = System.Diagnostics.Process.GetProcessById(ProcessId);
                return !chrome.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        }
    }

    bool _broken;

    /// <summary>
    /// Every page open in this browser. Ordered as Chrome reports them, numbered from one, with the
    /// tab that is actually on screen marked - which is what "the active tab" means to a person
    /// looking at the workspace, and therefore what it has to mean here.
    /// </summary>
    public async Task<IReadOnlyList<BrowserTab>> Tabs(CancellationToken cancel = default)
    {
        IReadOnlyList<string> targets = await Pages(cancel).ConfigureAwait(false);
        string visible = await Visible(targets, cancel).ConfigureAwait(false);
        var tabs = new List<BrowserTab>();
        for (int i = 0; i < targets.Count; i++)
        {
            string session = await SessionFor(targets[i], cancel).ConfigureAwait(false);
            string title = session.Length == 0 ? string.Empty
                : await On(session, "document.title", cancel).ConfigureAwait(false);
            string url = session.Length == 0 ? string.Empty
                : await On(session, "location.href", cancel).ConfigureAwait(false);
            tabs.Add(new BrowserTab(i + 1, targets[i], title, url,
                targets[i] == visible, targets[i] == SelectedTab));
        }
        return tabs;
    }

    /// <summary>
    /// Pins the page tools to one tab by its number, or unpins them back to following the visible
    /// one when the number is zero. Returns false when there is no such tab.
    /// </summary>
    public async Task<bool> SelectTab(int number, CancellationToken cancel = default)
    {
        if (number <= 0)
        {
            _pinned = string.Empty;
            return await Follow(cancel).ConfigureAwait(false);
        }
        IReadOnlyList<string> targets = await Pages(cancel).ConfigureAwait(false);
        if (number > targets.Count) return false;
        string chosen = targets[number - 1];
        string session = await SessionFor(chosen, cancel).ConfigureAwait(false);
        if (session.Length == 0) return false;
        // Bring it to the front as well. A pinned tab the owner cannot see would put the page tools
        // and the screenshot back on different pages, which is the whole bug being fixed here.
        await Call("Target.activateTarget", new JsonObject { ["targetId"] = chosen }, cancel)
            .ConfigureAwait(false);
        _pinned = chosen;
        SelectedTab = chosen;
        _session = session;
        return true;
    }

    /// <summary>Opens a new tab on a URL and works in it from then on.</summary>
    public async Task<bool> NewTab(string url, CancellationToken cancel = default)
    {
        JsonNode? made = await Call("Target.createTarget",
            new JsonObject { ["url"] = url.Length == 0 ? "about:blank" : url }, cancel).ConfigureAwait(false);
        if (made?["targetId"]?.GetValue<string>() is not { Length: > 0 } target) return false;
        string session = await SessionFor(target, cancel).ConfigureAwait(false);
        if (session.Length == 0) return false;
        await Call("Target.activateTarget", new JsonObject { ["targetId"] = target }, cancel).ConfigureAwait(false);
        SelectedTab = target;
        _session = session;
        if (_pinned.Length > 0) _pinned = target;
        if (!Blank(url)) HasContent = true;
        return true;
    }

    /// <summary>
    /// Points the page tools at the tab that is on screen, unless one was pinned on purpose. Called
    /// before every page operation: a link that opened a new tab, a tab the owner switched to, or a
    /// tab that was closed all change which page the pixels show, and the tools have to follow.
    /// </summary>
    internal async Task<bool> Follow(CancellationToken cancel)
    {
        if (_disposed) return false;
        // The cheap case, and by far the common one: the tab we are already on is still the one on
        // screen. One round trip, not one per tab.
        if (_session.Length > 0 && _pinned.Length == 0 && SelectedTab.Length > 0
            && await On(_session, "document.visibilityState", cancel).ConfigureAwait(false) == "visible")
            return true;

        IReadOnlyList<string> targets = await Pages(cancel).ConfigureAwait(false);
        if (targets.Count == 0) return _session.Length > 0;

        if (_pinned.Length > 0)
        {
            // A pinned tab that has been closed is not an error to sit on forever.
            if (targets.Contains(_pinned))
            {
                string held = await SessionFor(_pinned, cancel).ConfigureAwait(false);
                if (held.Length > 0) { _session = held; SelectedTab = _pinned; return true; }
            }
            _pinned = string.Empty;
        }

        string visible = await Visible(targets, cancel).ConfigureAwait(false);
        // Nothing reported itself visible - a minimized window, or a browser with no focus at all.
        // The newest page is the one something just opened, which is the better guess than the oldest.
        string chosen = visible.Length > 0 ? visible : targets[^1];
        string session = await SessionFor(chosen, cancel).ConfigureAwait(false);
        if (session.Length == 0) return false;
        SelectedTab = chosen;
        _session = session;
        return true;
    }

    readonly List<string> _order = [];

    /// <summary>
    /// Page targets only; extensions and devtools pages are not tabs. Numbered in the order they
    /// were first seen and kept that way, because DevTools does not promise a stable order and an
    /// agent told to work in "tab 2" must get the same tab it was just shown.
    /// </summary>
    async Task<IReadOnlyList<string>> Pages(CancellationToken cancel)
    {
        JsonNode? targets = await Call("Target.getTargets", null, cancel).ConfigureAwait(false);
        if (targets?["targetInfos"] is null) return _order.Count > 0 ? [.. _order] : [];
        var open = new List<string>();
        foreach (JsonNode? target in targets["targetInfos"]!.AsArray())
        {
            if (target?["type"]?.GetValue<string>() != "page") continue;
            string url = target["url"]?.GetValue<string>() ?? string.Empty;
            if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) continue;
            if (target["targetId"]?.GetValue<string>() is { Length: > 0 } id) open.Add(id);
        }
        _order.RemoveAll(id => !open.Contains(id));
        foreach (string id in open) if (!_order.Contains(id)) _order.Add(id);
        // Sessions for tabs that no longer exist are not worth keeping or reusing.
        foreach (string gone in _sessions.Keys.Where(id => !open.Contains(id)).ToList())
            _sessions.Remove(gone);
        return [.. _order];
    }

    /// <summary>Which page target is the one on screen, or empty when none says it is.</summary>
    async Task<string> Visible(IReadOnlyList<string> targets, CancellationToken cancel)
    {
        string first = string.Empty;
        foreach (string target in targets)
        {
            string session = await SessionFor(target, cancel).ConfigureAwait(false);
            if (session.Length == 0) continue;
            if (await On(session, "document.visibilityState", cancel).ConfigureAwait(false) != "visible") continue;
            // More than one window can hold a visible tab. The focused one is the one a screenshot
            // of the workspace shows, so it wins outright; otherwise take the first visible page.
            if (await On(session, "document.hasFocus()", cancel).ConfigureAwait(false) == "true") return target;
            if (first.Length == 0) first = target;
        }
        return first;
    }

    /// <summary>An attached session for one tab, made once and reused while that tab lives.</summary>
    async Task<string> SessionFor(string target, CancellationToken cancel)
    {
        if (_sessions.TryGetValue(target, out string? known)) return known;
        JsonNode? attached = await Call("Target.attachToTarget", new JsonObject
        {
            ["targetId"] = target,
            ["flatten"] = true,
        }, cancel).ConfigureAwait(false);
        string session = attached?["sessionId"]?.GetValue<string>() ?? string.Empty;
        if (session.Length > 0) _sessions[target] = session;
        return session;
    }

    /// <summary>One expression in one named session, without disturbing the selected tab.</summary>
    async Task<string> On(string session, string expression, CancellationToken cancel)
    {
        JsonNode? answer = await Call("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, cancel, session).ConfigureAwait(false);
        return answer?["result"]?["value"]?.ToString() ?? string.Empty;
    }
}
