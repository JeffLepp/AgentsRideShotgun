namespace HiveMind.AgentWorkspaces;

/// <summary>
/// What a mission's state means to the owner, and the one place that decides whether he still has
/// to be told about it. Everything that shows a mission - the panel, the dashboard card, the
/// notification - reads its words from here, so they cannot drift apart.
/// </summary>
internal static class WorkspaceMissions
{
    /// <summary>The word for a state, as the owner sees it.</summary>
    internal static string Word(MissionState state) => state switch
    {
        MissionState.Working => "Working",
        MissionState.Waiting => "Waiting",
        MissionState.NeedsYou => "Needs you",
        MissionState.Done => "Done",
        MissionState.Failed => "Stopped",
        MissionState.Interrupted => "Interrupted",
        _ => "Idle",
    };

    /// <summary>States the owner is told about on his desktop rather than only inside the panel.</summary>
    internal static bool WorthTelling(MissionState state) =>
        state is MissionState.Done or MissionState.Failed
            or MissionState.NeedsYou or MissionState.Interrupted;

    /// <summary>Whether a workspace in this state is offering the owner a Resume.</summary>
    internal static bool CanResume(MissionState state) => state == MissionState.Interrupted;

    /// <summary>
    /// Marks any mission the record still calls Working as Interrupted. Called once when the module
    /// starts: nothing can still be running in a process that has only just begun, so a record left
    /// saying Working is a mission that HiveMind or Windows stopped without it ever finishing.
    /// Returns the workspaces that were found that way.
    /// </summary>
    internal static IReadOnlyList<StoredWorkspace> FindInterrupted()
    {
        var found = new List<StoredWorkspace>();
        foreach (StoredWorkspace stored in WorkspaceStore.All())
        {
            if (stored.Mission != MissionState.Working) continue;
            StoredWorkspace? marked = WorkspaceStore.Update(stored.Id, one => one with
            {
                Mission = MissionState.Interrupted,
                MissionAt = one.MissionAt ?? DateTimeOffset.Now,
                Outcome = one.Outcome.Length > 0 ? one.Outcome
                    : "Deskweave stopped while this mission was working. Nothing it had open is open"
                        + " any more; the files in the workspace folder are as it left them.",
            });
            found.Add(marked ?? stored with { Mission = MissionState.Interrupted });
        }
        return found;
    }

    /// <summary>
    /// Tells the owner about an outcome, once. The record carries which state he has already been
    /// told about, so closing and reopening HiveMind never repeats an alert, and an outcome that
    /// arrived while he was away is still waiting for him when it starts again.
    /// </summary>
    internal static void Announce(string workspaceId, string name, MissionState state, string outcome)
    {
        if (!WorthTelling(state)) return;
        StoredWorkspace? stored = WorkspaceStore.Find(workspaceId);
        if (stored is null || stored.Announced == state) return;
        WorkspaceStore.Update(workspaceId, one => one with { Announced = state });
        WorkspaceNotice.Show($"{name} · {Word(state)}", Line(state, outcome));
    }

    /// <summary>Everything not yet announced, told now. Used once when the module starts.</summary>
    internal static void AnnounceWaiting()
    {
        foreach (StoredWorkspace stored in WorkspaceStore.All())
            if (WorthTelling(stored.Mission) && stored.Announced != stored.Mission)
                Announce(stored.Id, stored.Name, stored.Mission, stored.Outcome);
    }

    /// <summary>The sentence the owner reads, whichever surface he reads it on.</summary>
    internal static string Line(MissionState state, string outcome)
    {
        string said = Cut(outcome.Trim());
        // Interrupted already carries its own explanation on the record, so the head would only
        // repeat it. Everything else is a short lead-in followed by whatever the mission said.
        if (state == MissionState.Interrupted)
            return said.Length > 0 ? said + " Resume to carry on."
                : "Deskweave stopped while this was working. Resume to carry on.";
        string head = state switch
        {
            MissionState.Done => "The mission finished.",
            MissionState.Failed => "The mission stopped without finishing.",
            MissionState.NeedsYou => "The agent is waiting on you.",
            _ => string.Empty,
        };
        if (said.Length == 0) return head;
        return head.Length == 0 ? said : head + " " + char.ToUpperInvariant(said[0]) + said[1..];
    }

    static string Cut(string said) => said.Length > 180 ? said[..180] + "…" : said;
}
