using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>One workspace as the stack, the wide sidebar and the workspace-full header see it.</summary>
internal sealed class HubEntry(string id) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    string _name = "";
    public string Name { get => _name; set { Set(ref _name, value); Changed(nameof(OpenLabel)); Changed(nameof(SleepLabel)); } }
    bool _working;
    public bool Working { get => _working; set { Set(ref _working, value); Changed(nameof(OpenLabel)); } }
    bool _needsYou;
    public bool NeedsYou { get => _needsYou; set { Set(ref _needsYou, value); Changed(nameof(OpenLabel)); } }
    /// <summary>Working, but nobody is driving it right now (WorkspaceRuntime.IsIdle): the stack's
    /// one blue dot used to mean both "mid-task" and "sitting idle burning resources" at once.</summary>
    bool _idle;
    public bool Idle { get => _idle; set { Set(ref _idle, value); Changed(nameof(OpenLabel)); } }
    /// <summary>Screen-reader name for the card or row: the dot is the only visible status.</summary>
    public string OpenLabel => "Open workspace " + Name + (NeedsYou ? ", needs you" : Idle ? ", idle" : Working ? ", working" : ", recent");
    /// <summary>Tooltip and screen-reader name for the card/row's own Sleep control (fix list item
    /// 2.2): says exactly what happens up front, since a one-click action has no confirmation dialog
    /// to say it in.</summary>
    public string SleepLabel => "Sleep " + Name + ". Open apps close; saved files and the last picture stay. The next agent action wakes it.";
    /// <summary>"Claude Code", "Codex wants you", "Sleeps in 4m", or empty when there is nothing to say.</summary>
    string _agentText = "";
    public string AgentText { get => _agentText; set => Set(ref _agentText, value); }
    /// <summary>Asleep only, the stack's wording: "2h", "yesterday", "Mon".</summary>
    string _age = "";
    public string Age { get => _age; set => Set(ref _age, value); }
    /// <summary>Asleep only, the wide sidebar's wording: "2h ago", "Yesterday", "Monday".</summary>
    string _sidebarAge = "";
    public string SidebarAge { get => _sidebarAge; set => Set(ref _sidebarAge, value); }
    BitmapSource? _preview;
    public BitmapSource? Preview { get => _preview; set => Set(ref _preview, value); }
    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

/// <summary>The relative time an asleep workspace shows, in the two wordings the reference uses.</summary>
internal static class HubFormat
{
    internal static string StackAge(DateTimeOffset lastUsed, DateTimeOffset now)
    {
        TimeSpan since = now - lastUsed;
        if (since < TimeSpan.FromMinutes(1)) return "now";
        if (since < TimeSpan.FromHours(1)) return Math.Max(1, (int)since.TotalMinutes) + "m";
        if (lastUsed.Date == now.Date) return (int)since.TotalHours + "h";
        if (lastUsed.Date == now.Date.AddDays(-1)) return "yesterday";
        if (since < TimeSpan.FromDays(7)) return lastUsed.ToString("ddd");
        return lastUsed.ToString("MMM d");
    }

    internal static string SidebarAge(DateTimeOffset lastUsed, DateTimeOffset now)
    {
        TimeSpan since = now - lastUsed;
        if (since < TimeSpan.FromMinutes(1)) return "Just now";
        if (since < TimeSpan.FromHours(1)) return Math.Max(1, (int)since.TotalMinutes) + "m ago";
        if (lastUsed.Date == now.Date) return (int)since.TotalHours + "h ago";
        if (lastUsed.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (since < TimeSpan.FromDays(7)) return lastUsed.ToString("dddd");
        return lastUsed.ToString("MMM d");
    }

    /// <summary>What an idle running workspace says about when it sleeps, from
    /// <see cref="HiveMind.AgentWorkspaces.WorkspaceRuntime.SleepsIn"/>. Minute granularity: a
    /// countdown to the second would just be motion nobody asked for on a card meant to sit still.</summary>
    internal static string SleepsInLabel(TimeSpan? left) => left switch
    {
        null => "",
        { TotalSeconds: <= 60 } => "Sleeps soon",
        { } remaining => $"Sleeps in {(int)Math.Ceiling(remaining.TotalMinutes)}m",
    };
}

/// <summary>
/// Backs both the stack and the wide sidebar: every stored workspace, split into working and
/// asleep, kept in step with the workspace store and the running desktops. A scene fills it once
/// through <see cref="LoadFixture"/> instead of reading real stores.
/// </summary>
internal sealed class HubViewModel : IDisposable
{
    public ObservableCollection<HubEntry> Working { get; } = [];
    public ObservableCollection<HubEntry> Asleep { get; } = [];
    readonly Dictionary<string, HubEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    DispatcherTimer? _agingTimer;
    bool _fixture;
    bool _disposed;

    public HubViewModel()
    {
        // Both events can arrive on whatever thread the store or an agent continuation runs on;
        // Refresh mutates the UI-bound Working/Asleep collections, so it must run on the UI thread.
        WorkspaceStore.Changed += ScheduleRefresh;
        WorkspaceRuntime.AttentionChanged += ScheduleRefresh;
    }

    void ScheduleRefresh() => Application.Current?.Dispatcher.BeginInvoke(Refresh);

    /// <summary>Starts the relative-state refresh: "2h" creeping to "3h" on a recent card, and now
    /// also a running one crossing from working into idle, and its "Sleeps in" countdown, neither of
    /// which raises an event of its own - they are just true once enough wall-clock time has passed.
    /// Ten seconds rather than the old one minute so idle reads as idle soon after it actually is,
    /// not up to a minute later; still cheap; call only while the hub is visible, idempotent.</summary>
    public void StartAging()
    {
        if (_agingTimer is not null || _disposed) return;
        _agingTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _agingTimer.Tick += (_, _) => Refresh();
        _agingTimer.Start();
    }

    /// <summary>Stops the relative-age refresh; nothing ticks while the hub is hidden.</summary>
    public void StopAging()
    {
        _agingTimer?.Stop();
        _agingTimer = null;
    }

    /// <summary>True while the once-a-minute age refresh is running (the gate drives this through
    /// the real window's visibility rather than waiting out a real minute).</summary>
    internal bool AgingActive => _agingTimer is not null;

    public HubEntry? Find(string id) => _byId.GetValueOrDefault(id);

    public void Select(string? id)
    {
        foreach (HubEntry entry in _byId.Values) entry.Selected = entry.Id == id;
    }

    public void Refresh()
    {
        if (_fixture || _disposed) return;
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<StoredWorkspace> records = WorkspaceStore.All();
        var keep = records.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stale in _byId.Keys.Where(id => !keep.Contains(id)).ToArray()) _byId.Remove(stale);
        var working = new List<HubEntry>();
        var asleep = new List<HubEntry>();
        // Most recently used first: a workspace that just went to sleep tops Recent.
        foreach (StoredWorkspace workspace in records.OrderByDescending(w => w.LastUsed))
        {
            if (!_byId.TryGetValue(workspace.Id, out HubEntry? entry))
            {
                entry = new HubEntry(workspace.Id);
                _byId.Add(workspace.Id, entry);
            }
            entry.Name = workspace.Name;
            WorkspaceRuntime? runtime = WorkspaceRuntime.Of(workspace.Id);
            bool isWorking = runtime is not null;
            entry.Working = isWorking;
            bool needsYou = runtime?.Access?.Handoffs.All.Any(h => h.State == "pending") == true;
            entry.NeedsYou = needsYou;
            // Idle only means something while running, and only when nothing else already claims
            // the row's one line of trailing text (a pending question outranks a sleep countdown).
            bool idle = isWorking && runtime!.IsIdle;
            entry.Idle = idle && !needsYou;
            string driver = runtime?.Access?.LastController ?? string.Empty;
            string kept = WorkspaceHome.Label(workspace.Agents);
            string who = driver.Length > 0 ? WorkspaceHome.DisplayName(driver)
                : kept.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : kept;
            string sleepsIn = entry.Idle ? HubFormat.SleepsInLabel(runtime!.SleepsIn) : "";
            entry.AgentText = needsYou ? (who.Length > 0 ? who + " wants you" : "Needs you")
                : sleepsIn.Length > 0 ? (who.Length > 0 ? who + " · " + sleepsIn : sleepsIn) : who;
            entry.Age = isWorking ? "" : HubFormat.StackAge(workspace.LastUsed, now);
            entry.SidebarAge = isWorking ? "" : HubFormat.SidebarAge(workspace.LastUsed, now);
            // A workspace with no computer running shows the last look it had rather than an empty
            // rectangle, and a running one shows it until its own live frame arrives.
            HubLastLook.Fill(entry, isWorking);
            (isWorking ? working : asleep).Add(entry);
        }
        Sync(Working, working);
        Sync(Asleep, asleep);
    }

    static void Sync(ObservableCollection<HubEntry> target, List<HubEntry> wanted)
    {
        for (int i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            int old = target.IndexOf(wanted[i]);
            if (old >= 0) target.Move(old, i); else target.Insert(i, wanted[i]);
        }
    }

    /// <summary>Replaces both lists with fixed entries for a scene. Real refreshes stop after this.</summary>
    internal void LoadFixture(IEnumerable<HubEntry> working, IEnumerable<HubEntry> asleep)
    {
        _fixture = true;
        Working.Clear();
        foreach (HubEntry entry in working) { _byId[entry.Id] = entry; Working.Add(entry); }
        Asleep.Clear();
        foreach (HubEntry entry in asleep) { _byId[entry.Id] = entry; Asleep.Add(entry); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAging();
        WorkspaceStore.Changed -= ScheduleRefresh;
        WorkspaceRuntime.AttentionChanged -= ScheduleRefresh;
    }
}

/// <summary>Shared preview-loop rules (brief A.6, A.10): the app picks the rate itself, no Smoothness
/// or Pause-previews-on-battery setting to read. Balanced pace, and a slower one on battery rather
/// than none - most Windows machines are laptops, and a tile frozen on its last frame reads as the
/// feature being broken.</summary>
internal static class HubPreview
{
    /// <summary>How often the stack's tiles and the workspace-full screen redraw. Battery stretches
    /// the second out to two and a half, the same 2.5x the corner view takes when it is idle, and
    /// never stops. Callers read this every tick instead of keeping the value their timer started
    /// with, so plugging in speeds the picture back up and unplugging slows it down with no restart.
    /// The battery question goes to the engine, the one the corner asks, so the two surfaces cannot
    /// drift apart on what unplugged means.</summary>
    internal static TimeSpan Interval() =>
        TimeSpan.FromSeconds(WorkspacePeekCapture.OnBattery() ? 2.5 : 1);
}
