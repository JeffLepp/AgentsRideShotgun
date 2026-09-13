namespace HiveMind.AgentWorkspaces;

/// <summary>A short sequence over control IDs returned by the current workspace's UIA reader.</summary>
public sealed record WorkspaceBatchAction(string Action, int Control, string? Text = null);

public sealed record WorkspaceBatchStep(int Index, string Action, int Control, string Result);

/// <summary>Completed steps are never replayed. Next is the zero-based first unexecuted step.</summary>
public sealed record WorkspaceBatchResult(string Status, int Next, string Reason,
    IReadOnlyList<WorkspaceBatchStep> Completed);

internal static class WorkspaceBatch
{
    internal const int MaxActions = 16;
    internal const int MaxText = 32000;

    internal static WorkspaceBatchResult Execute(IReadOnlyList<WorkspaceBatchAction> actions,
        Func<bool> stillOwnsLease, Func<int, string?> validate,
        Func<WorkspaceBatchAction, (bool Applied, string Result)> execute,
        CancellationToken cancel)
    {
        var completed = new List<WorkspaceBatchStep>();
        WorkspaceBatchResult Stop(string status, string reason) =>
            new(status, completed.Count, reason, completed.ToArray());

        // Validate the entire request shape before allowing any side effects. Targets are checked
        // separately, immediately before each step, because an earlier action can change the UI.
        if (actions.Count is < 1 or > MaxActions)
            return Stop("rejected", $"A batch must contain 1 to {MaxActions} actions.");
        foreach (WorkspaceBatchAction? action in actions)
            if (action is null || action.Control <= 0 || action.Action is not ("press" or "write" or "read")
                || action.Action == "write" && action.Text is null
                || action.Text?.Length > MaxText)
                return Stop("rejected", "Use press, write (with text), or read with a control ID from controls.");

        foreach (WorkspaceBatchAction action in actions)
        {
            if (cancel.IsCancellationRequested) return Stop("cancelled", "The request was cancelled.");
            if (!stillOwnsLease()) return Stop("paused", "Control changed. Observe the workspace before continuing.");
            string? changed = validate(action.Control);
            if (changed is not null) return Stop("replan", changed);
            if (cancel.IsCancellationRequested) return Stop("cancelled", "The request was cancelled.");
            if (!stillOwnsLease()) return Stop("paused", "Control changed during validation.");

            (bool applied, string result) = execute(action);
            if (!applied)
                return Stop(stillOwnsLease() ? "replan" : "paused",
                    result + " The attempted step may have partially applied; observe before retrying it.");
            completed.Add(new(completed.Count, action.Action, action.Control, result));
        }
        return Stop("completed", "All steps executed. Read back the result before reporting the mission done.");
    }
}
