using System.Windows;

namespace HiveMind.AgentWorkspaces;

/// <summary>Reflection entry point for the full app panel and its optional compact dashboard view.</summary>
public static class ModuleEntry
{
    public static event Action? DashboardOpenRequested;

    /// <summary>Which stored workspace the panel opens on. Set by clicking a card on the dashboard.</summary>
    internal static string? Selected { get; set; }

    /// <summary>True while the hub window is on screen and not minimized. The app sets it on the UI
    /// thread; the corner window stays away while it is true.</summary>
    public static bool HubShowing
    {
        get => _hubShowing;
        set
        {
            if (_hubShowing == value) return;
            _hubShowing = value;
            HubShowingChanged?.Invoke();
        }
    }
    static bool _hubShowing;

    public static event Action? HubShowingChanged;

    /// <summary>Whether Windows refused the Pause shortcut because another app holds it. The corner
    /// host reports it after registration; Settings shows it under the shortcut.</summary>
    internal static bool PauseShortcutTaken { get; private set; }

    internal static event Action? PauseShortcutTakenChanged;

    internal static void ReportPauseShortcut(bool taken)
    {
        if (PauseShortcutTaken == taken) return;
        PauseShortcutTaken = taken;
        PauseShortcutTakenChanged?.Invoke();
    }

    /// <summary>The tray's "Show the corner window". The corner window shows at once and then follows
    /// its normal fade, even when Settings has it off. Raised on the UI thread.</summary>
    public static event Action? ShowCornerRequested;

    internal static void RequestShowCorner() => ShowCornerRequested?.Invoke();

    /// <summary>An agent needs the owner and the corner window can't show it (MVP_SPEC, Alerts):
    /// title, one line, the workspace and the request, for the app's Windows notification. Raised
    /// on the UI thread.</summary>
    public static event Action<string, string, string, string>? AttentionNeeded;

    internal static void RequestAttention(string title, string text, string workspace, string request) =>
        AttentionNeeded?.Invoke(title, text, workspace, request);

    /// <summary>The tray's "Pause every agent" / "Resume every agent", and the toast's Resume link.
    /// The corner window owns pausing; it sets <see cref="AllPaused"/>. Raised on the UI thread.</summary>
    public static event Action? PauseAllRequested;

    internal static void RequestPauseAll() => PauseAllRequested?.Invoke();

    /// <summary>True while Pause every agent holds every workspace. The tray reads it for its label.</summary>
    public static bool AllPaused
    {
        get => _allPaused;
        internal set
        {
            if (_allPaused == value) return;
            _allPaused = value;
            AllPausedChanged?.Invoke();
        }
    }
    static bool _allPaused;

    public static event Action? AllPausedChanged;

    /// <summary>
    /// App start: the screenshot sweep, the corner window, the one router every connected agent
    /// uses, and the keep-up loop for agents installed later. None of them starts a workspace.
    /// </summary>
    public static void Initialize()
    {
        // Before anything can start a program: a previous run that was killed mid-launch may have
        // left the owner's applications drawing in software.
        WorkspaceRenderMode.Recover();
        WorkspaceRuntime.SweepEvidence();
        // The corner window: it shows nothing until an agent works.
        WorkspacePeekHost.Start();
        // One named pipe for every connected agent. It starts nothing until an agent uses a tool.
        WorkspaceRouter.Start();
        // Consent was given once; an agent installed since then connects on its own.
        WorkspaceConnections.KeepUp();
    }

    /// <summary>App exit. Stops every workspace still running.</summary>
    public static void Shutdown()
    {
        WorkspaceConnections.StopKeepingUp();
        WorkspaceRouter.Stop();
        WorkspacePeekHost.Stop();
        WorkspaceRuntime.Rest();
        WorkspaceRenderMode.Rest();
    }

    /// <summary>Optional uninstall/storage-inventory contract. This root contains only workspaces
    /// created by this private app, their records/evidence, and transient setup staging.</summary>
    public static string[] GetDataPaths() => [WorkspaceStore.Root, WorkspaceAccessStore.Root];

    /// <summary>Uninstall: stops everything this install is running and takes its bridge out of the
    /// agents' configs. The owner's workspaces, records and settings are left where they are; nothing
    /// here deletes data.</summary>
    public static void Uninstall()
    {
        WorkspaceConnections.StopKeepingUp();
        WorkspaceRouter.Stop();
        WorkspacePeekHost.Stop();
        WorkspaceRuntime.Rest();
        WorkspaceRenderMode.Rest();
        WorkspaceRenderMode.Recover();
        Task.Run(WorkspaceConnections.RemoveOwnedConnections).GetAwaiter().GetResult();
    }

    internal static void RequestDashboardOpen() => DashboardOpenRequested?.Invoke();
}
