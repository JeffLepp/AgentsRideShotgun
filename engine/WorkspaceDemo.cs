namespace HiveMind.AgentWorkspaces;

public enum WorkspaceVisualState
{
    Starting,
    Ready,
    Working,
    Waiting,
    NeedsYou,
    HumanControl,
    Sleeping,
    Paused,
    Failed
}

public sealed record DemoWorkspace(
    string Id,
    string Name,
    string MainAgent,
    string Task,
    WorkspaceVisualState State,
    string StateDetail,
    string FrameHeading,
    string FrameDetail)
{
    public string StateLabel => WorkspaceDemoCatalog.StateLabel(State);
    public string AccessibleName => $"{Name}, {StateLabel}. {Task}";
}

public sealed record WorkspaceStateOption(WorkspaceVisualState State, string Label);

/// <summary>One deterministic catalog shared by the widget, expanded panel, full window, and tests.</summary>
public static class WorkspaceDemoCatalog
{
    public static IReadOnlyList<DemoWorkspace> Compact { get; } =
    [
        new(
            "deskweave-test-lab",
            "Deskweave Test Lab",
            "Codex · main agent",
            "Reviewing the installed shell",
            WorkspaceVisualState.Working,
            "Checking the rendered workspace flow",
            "Installed UI review",
            "Dashboard · Library · restart path"),
        new(
            "campaign-desk",
            "Campaign Desk",
            "Claude · main agent",
            "Preparing the next campaign check",
            WorkspaceVisualState.Waiting,
            "Next check tomorrow at 9:00 AM",
            "Campaign brief",
            "Waiting on the scheduled measurement"),
        new(
            "research",
            "Research",
            "Codex · main agent",
            "Collecting product evidence",
            WorkspaceVisualState.Sleeping,
            "Last frame saved · no guest compute",
            "Evidence index",
            "12 sources · 4 open questions")
    ];

    public static IReadOnlyList<WorkspaceStateOption> ReviewStates { get; } =
    [
        new(WorkspaceVisualState.Starting, "Starting"),
        new(WorkspaceVisualState.Working, "Working"),
        new(WorkspaceVisualState.Waiting, "Waiting"),
        new(WorkspaceVisualState.Sleeping, "Sleeping"),
        new(WorkspaceVisualState.Failed, "Failed")
    ];

    public static DemoWorkspace Primary(WorkspaceVisualState state) => new(
        "deskweave-test-lab",
        "Deskweave Test Lab",
        "Codex · main agent",
        TaskFor(state),
        state,
        DetailFor(state),
        "Installed UI review",
        FrameDetailFor(state));

    public static string StateLabel(WorkspaceVisualState state) => state switch
    {
        WorkspaceVisualState.NeedsYou => "Needs you",
        WorkspaceVisualState.HumanControl => "Human control",
        _ => state.ToString()
    };

    public static string BrushKey(WorkspaceVisualState state) => state switch
    {
        WorkspaceVisualState.Ready or WorkspaceVisualState.Working => "AWWorkingBrush",
        WorkspaceVisualState.Waiting or WorkspaceVisualState.NeedsYou => "AWWaitingBrush",
        WorkspaceVisualState.Failed => "AWFailedBrush",
        WorkspaceVisualState.Starting or WorkspaceVisualState.HumanControl => "AWAccentBrush",
        _ => "AWIdleBrush"
    };

    static string TaskFor(WorkspaceVisualState state) => state switch
    {
        WorkspaceVisualState.Starting => "Preparing the verified Ready recipe",
        WorkspaceVisualState.Ready => "Ready for a mission",
        WorkspaceVisualState.Working => "Reviewing the installed Deskweave surface",
        WorkspaceVisualState.Waiting => "Waiting for the next scheduled check",
        WorkspaceVisualState.NeedsYou => "Waiting for approval to install a tool",
        WorkspaceVisualState.HumanControl => "Owner is inspecting the workspace",
        WorkspaceVisualState.Sleeping => "Sleeping between approved events",
        WorkspaceVisualState.Paused => "Paused by the owner",
        WorkspaceVisualState.Failed => "Setup stopped before any system change",
        _ => "Workspace preview"
    };

    static string DetailFor(WorkspaceVisualState state) => state switch
    {
        WorkspaceVisualState.Starting => "Preparing files and the reconstruction recipe",
        WorkspaceVisualState.Ready => "No mission assigned",
        WorkspaceVisualState.Working => "Checking the rendered workspace flow",
        WorkspaceVisualState.Waiting => "Next check tomorrow at 9:00 AM",
        WorkspaceVisualState.NeedsYou => "Exact approval request is preserved",
        WorkspaceVisualState.HumanControl => "Agent input is paused",
        WorkspaceVisualState.Sleeping => "Last frame saved · no guest compute",
        WorkspaceVisualState.Paused => "Mission and evidence are preserved",
        WorkspaceVisualState.Failed => "Evidence kept · retry and rebuild remain available",
        _ => string.Empty
    };

    static string FrameDetailFor(WorkspaceVisualState state) => state switch
    {
        WorkspaceVisualState.Starting => "Loading visual shell · 64%",
        WorkspaceVisualState.Waiting => "Scheduled check · no reasoning while idle",
        WorkspaceVisualState.Sleeping => "Stored last frame",
        WorkspaceVisualState.Failed => "Setup evidence retained",
        WorkspaceVisualState.HumanControl => "Owner has mouse and keyboard",
        _ => "Dashboard · Library · restart path"
    };
}
