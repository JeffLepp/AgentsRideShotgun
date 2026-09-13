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

    /// <summary>
    /// The module now has a background runtime, and it is one clock. Without it a mission parked for
    /// an hour only woke if the owner happened to have this app open when the hour was up, which is
    /// the opposite of the reason to park one.
    ///
    /// What runs here is a timer over the workspace records on disk. It starts nothing by itself: it
    /// only starts a workspace whose own agent asked to be woken at a time that has now passed.
    /// </summary>
    public static void Initialize()
    {
        WorkspaceRuntime.Watch();
        // The corner view watches the same background runtime. It costs nothing until the owner
        // turns it on, and turning it on in the panel must not be the only way to get it back after
        // a restart - a mission that works while HiveMind is minimised is exactly when it is wanted.
        WorkspacePeekHost.Start();
        // One named pipe for every connected agent. It starts nothing until an agent uses a tool.
        WorkspaceRouter.Start();
    }

    /// <summary>App exit. Stops the clock and every workspace still running under it.</summary>
    public static void Shutdown()
    {
        WorkspaceRouter.Stop();
        WorkspacePeekHost.Stop();
        WorkspaceRuntime.Rest();
    }

    public static UIElement CreatePanel() => new AgentWorkspacesPanel();

    public static UIElement CreateDashboardWidget() => new WorkspaceWidget();

    /// <summary>Optional uninstall/storage-inventory contract. This root contains only workspaces
    /// created by this private app, their records/evidence, and transient setup staging.</summary>
    public static string[] GetDataPaths() => [WorkspaceStore.Root, WorkspaceAccessStore.Root];

    public static void Uninstall(bool removeData)
    {
        WorkspaceRouter.Stop();
        WorkspacePeekHost.Stop();
        WorkspaceRuntime.Rest();
        Task.Run(WorkspaceConnections.RemoveOwnedConnections).GetAwaiter().GetResult();
    }

    internal static void RequestDashboardOpen() => DashboardOpenRequested?.Invoke();
}
