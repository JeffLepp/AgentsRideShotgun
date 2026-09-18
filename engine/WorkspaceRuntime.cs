using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// A workspace's live half: its Windows desktop, its control plane and its agent, held together and
/// held outside the panel.
///
/// The panel used to own all three as its own fields. That made a mission a property of a page: it
/// existed only while that page was on screen, it died when the owner looked at something else, and
/// two pages open on one workspace would each have built a second desktop over the first. A runtime
/// belongs to the workspace's id instead, so everything looking at that workspace - a panel, the
/// dashboard card, or nothing at all - is looking at the same one.
///
/// This is also what lets a parked mission wake with nothing on screen. The clock is static and
/// runs for the module, not for a page, and a wake with no panel simply starts the runtime the panel
/// would have started and hands the conversation back.
/// </summary>
public sealed class WorkspaceRuntime : IDisposable
{
    /// <summary>How soon a wake that could not proceed is retried.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(20);

    static readonly Dictionary<string, WorkspaceRuntime> Live = new(StringComparer.OrdinalIgnoreCase);
    static DispatcherTimer? _clock;
    static DateTimeOffset? _nextWake;
    static bool _reviewQueued;

    /// <summary>How long a workspace nobody uses keeps its desktop and browser (MVP_SPEC, Sleep).</summary>
    internal static TimeSpan SleepAfter { get; set; } = TimeSpan.FromMinutes(30);
    // One shared clock while anything runs, none while nothing does.
    static DispatcherTimer? _sleeper;
    long _touched = Environment.TickCount64;

    /// <summary>Set while a tick is running, so a wake that opens a panel cannot start a second tick.</summary>
    static bool _ticking;

    readonly object _gate = new();
    readonly List<(string Who, string What)> _spoken = [];
    readonly Dispatcher _owner;
    AgentDesktop? _computer;
    WorkspaceControl? _plane;
    WorkspaceAgent? _agent;
    internal WorkspaceExternalAccess? Access { get; private set; }

    /// <summary>
    /// Something changed that anything drawing a workspace from outside it - the dashboard cards -
    /// has no other way to hear about: a request that needs the owner, or a workspace starting or
    /// stopping. A card decides from this whether it is showing a live screen or a drawn one.
    /// </summary>
    internal static event Action? AttentionChanged;
    internal event Action? AccessChanged;
    bool _disposed;

    WorkspaceRuntime(StoredWorkspace workspace)
    {
        _owner = Dispatcher.CurrentDispatcher;
        Id = workspace.Id;
        _computer = AgentDesktop.Create(AgentDesktop.NameFor(workspace.Id), workspace.Power, workspace.Mode);
        try
        {
            // Give the first visible app a head start while the control plane and local MCP listener
            // are being built. The runtime is not published until all of them are ready, so this
            // changes only time-to-first-window, not what callers can observe or control.
            _computer.Launch(TerminalPath(), TerminalArguments(_computer.Folder!));
            // The control plane owns the lease, so who is driving is one answer rather than a
            // boolean here and a different boolean wherever an agent ends up living.
            _plane = new WorkspaceControl(_computer);
            _plane.DriverChanged += who => { _touched = Environment.TickCount64; DriverChanged?.Invoke(who); };
            _agent = new WorkspaceAgent(
                _plane, workspace.Name, _computer.Folder ?? string.Empty,
                workspace.AgentCredentials, workspace.RunUsageCeilingTokens);
            _agent.Said += said => { Note("Agent", said); Said?.Invoke(said); };
            // Deliberately not recorded: these are half-sentences, and the finished message follows
            // on Said. A panel opened after the fact replays whole messages, never a typing effect.
            _agent.Saying += typing => Saying?.Invoke(typing);
            _agent.Acting += (tool, detail) => { Doing = tool; Acting?.Invoke(tool, detail); };
            // The run's own counter moved. The record has not, so nothing re-reads it for this.
            _agent.Metered += () => Metered?.Invoke(false);
            _agent.StateChanged += state =>
            {
                Remember();
                // Told once, on his own desktop, because a workspace runs with its panel closed and
                // an outcome that only exists inside the panel is one nobody sees for hours.
                WorkspaceMissions.Announce(Id, workspace.Name, state, _agent?.Outcome ?? string.Empty);
                Moved?.Invoke(state);
            };
            Access = new WorkspaceExternalAccess(workspace.Id, _plane);
            Access.Changed += OnAccessChanged;
            // Off the constructor: the runtime is usable the moment it is published, and the browser
            // becomes ready underneath it. An agent that never browses simply never notices.
            if (WorkspaceAccessStore.Read(workspace.Id).PrewarmBrowser)
            {
                WorkspaceControl warming = _plane;
                _ = Task.Run(() => warming.Prewarm(CancellationToken.None));
            }
            // Reading every Start Menu shell link takes about seven seconds, and `open` blocks the
            // agent's turn while it happens. Doing it here means the first open by name is free.
            _ = Task.Run(WorkspacePrograms.Warm);
        }
        catch
        {
            Tear();
            throw;
        }
    }

    /// <summary>The workspace this runtime is for.</summary>
    public string Id { get; }

    public AgentDesktop? Computer => _computer;
    public WorkspaceControl? Plane => _plane;
    public WorkspaceAgent? Agent => _agent;

    /// <summary>The last tool the agent reached for, so a panel opened mid-mission is not blank.</summary>
    public string Doing { get; private set; } = string.Empty;

    /// <summary>What has been said in this workspace since its computer started. A panel that opens
    /// onto a mission already running replays this rather than showing an empty conversation.</summary>
    public IReadOnlyList<(string Who, string What)> Conversation
    {
        get { lock (_gate) return _spoken.ToArray(); }
    }

    public event Action<string>? Said;

    /// <summary>Text the agent is writing right now. <see cref="Said"/> follows with the whole of it.</summary>
    public event Action<string>? Saying;
    public event Action<string, string>? Acting;
    public event Action<MissionState>? Moved;
    public event Action<Driver>? DriverChanged;

    /// <summary>
    /// What the boss agent has spent moved. True when the workspace's stored total moved with it,
    /// which is the only time anything watching has to read the record again.
    /// </summary>
    public event Action<bool>? Metered;

    /// <summary>Raised when this runtime stops, so anything drawing it stops drawing it live.</summary>
    public event Action? Ended;

    public WorkspacePower Power
    {
        set { if (_computer is not null) _computer.Power = value; }
    }

    /// <summary>Use a freshly persisted policy for the next run without restarting the desktop.</summary>
    public void Configure(StoredWorkspace workspace)
    {
        if (!workspace.Id.Equals(Id, StringComparison.OrdinalIgnoreCase)) return;
        _agent?.Configure(workspace.AgentCredentials, workspace.RunUsageCeilingTokens);
    }

    /// <summary>The live runtime for a workspace, or null when its computer is not running.</summary>
    public static WorkspaceRuntime? Of(string id) =>
        Live.TryGetValue(id, out WorkspaceRuntime? found) ? found : null;

    /// <summary>True while any workspace has a computer running, panel or no panel.</summary>
    public static bool AnyRunning => Live.Count > 0;

    /// <summary>
    /// Every workspace running right now. A copy, because the corner view reads this whenever a
    /// workspace starts or stops and a runtime disposing itself mid-read would otherwise be an
    /// exception in a window that is only watching.
    /// </summary>
    internal static IReadOnlyList<WorkspaceRuntime> Running => Live.Values.ToArray();

    void OnAccessChanged() { AccessChanged?.Invoke(); AttentionChanged?.Invoke(); }

    /// <summary>
    /// The workspace's runtime, started if it was not already running. Starting twice is what built
    /// two desktops over one workspace, so there is one way in and it is this.
    /// </summary>
    public static WorkspaceRuntime Start(StoredWorkspace workspace)
    {
        if (Of(workspace.Id) is { } already)
        {
            already.Configure(workspace);
            return already;
        }
        var runtime = new WorkspaceRuntime(workspace);
        Live[workspace.Id] = runtime;
        // A mission this process never started. The conversation is the CLI's and it is on disk, so
        // a workspace parked before HiveMind closed is picked up rather than lost.
        runtime._agent?.Adopt(workspace.Session, workspace.WakeAt, workspace.WakeNote,
            workspace.Mission, workspace.Outcome);
        // The desktop is new; the conversation being handed back to the agent is not. If that
        // conversation already has page content in it, the fresh control plane has to be told so,
        // or sleeping over a page would be the way to get execution back.
        if (workspace.SessionReadUntrustedContent) runtime._plane?.CarryUntrustedContent();
        if (_sleeper is null)
        {
            _sleeper = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _sleeper.Tick += (_, _) => Doze();
        }
        _sleeper.Start();
        AttentionChanged?.Invoke();
        return runtime;
    }

    /// <summary>
    /// How long nothing has happened here, or null while something is: an agent holding it, the
    /// owner's hands on it (Pause every agent included), a question for the owner, a boss mission.
    /// </summary>
    internal TimeSpan? Quiet
    {
        get
        {
            if (_agent?.State is MissionState.Working or MissionState.NeedsYou || _plane?.Driving == Driver.Owner) return null;
            if (Access is { } access && (access.HasDriver || access.Handoffs.All.Any(r => r.State == "pending"))) return null;
            return TimeSpan.FromMilliseconds(Environment.TickCount64 - Math.Max(_touched, Access?.LastActive ?? 0));
        }
    }

    /// <summary>
    /// Puts the workspace nobody has used for longest to sleep, to make room for another one. False
    /// when every running workspace is in use. Its files stay, and the next agent call wakes it.
    /// </summary>
    internal static bool SleepQuietest()
    {
        foreach (WorkspaceRuntime runtime in Running.Where(r => r.Quiet is not null && r.Id != WorkspacePeekHost.PinnedOn)
            .OrderByDescending(r => r.Quiet))
            if (runtime.TrySleep()) return true;
        return false;
    }

    /// <summary>Stops this workspace if nothing got hold of it since it was found quiet.</summary>
    bool TrySleep()
    {
        if (Access?.Retire() == false) return false;
        Dispose();
        return true;
    }

    /// <summary>
    /// Sleeps what nobody has used for <see cref="SleepAfter"/>, and the quietest one each minute
    /// while memory is nearly full. A pinned corner window keeps the workspace it shows awake.
    /// </summary>
    internal static void Doze()
    {
        foreach (WorkspaceRuntime runtime in Running)
            if (runtime.Id != WorkspacePeekHost.PinnedOn && runtime.Quiet >= SleepAfter) runtime.TrySleep();
        if (WorkspaceLimits.MemoryLoad() >= 90) SleepQuietest();
        if (Live.Count == 0) _sleeper?.Stop();
    }

    static Timer? _sweeper;

    /// <summary>
    /// Screenshots older than a week go from every workspace, running or asleep (MVP_SPEC, History):
    /// now and hourly for as long as the app runs, on a pool thread. Walks the folders rather than
    /// reading workspace records, which can write a record back while one is being created.
    /// </summary>
    internal static void SweepEvidence() => _sweeper ??= new Timer(_ =>
    {
        try { foreach (string folder in Directory.EnumerateDirectories(WorkspaceStore.Root)) WorkspaceEvidence.Expire(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }, null, TimeSpan.Zero, TimeSpan.FromHours(1));

    /// <summary>Adds a line to the conversation from outside - the owner's own messages.</summary>
    public void Note(string who, string what)
    {
        lock (_gate)
        {
            _spoken.Add((who, what));
            // Bounded on purpose: a mission that runs for a week must not grow a list until the
            // process runs out of memory for the sake of a panel nobody may ever open.
            if (_spoken.Count > 400) _spoken.RemoveRange(0, _spoken.Count - 400);
        }
    }

    /// <summary>
    /// Hands the owner's words to the mission that is already running. They reach it on its next
    /// tool call, which is seconds away, and cost it nothing: no interruption, no restart, and the
    /// conversation it is in is the one it reads them in.
    /// </summary>
    public bool Interject(string said)
    {
        if (_plane is null || _agent?.State != MissionState.Working) return false;
        _plane.Interject(said);
        Note("You", said);
        return true;
    }

    /// <summary>Runs a mission under this runtime and releases every live resource after the agent
    /// deliberately parks. The session and wake record have already been persisted by then, so a
    /// sleeping workspace costs no desktop, process, listener, capture, audio sweep, or thread.
    ///
    /// A message the owner sent while it worked, that it finished before ever collecting, becomes
    /// the next mission rather than being dropped: he said it, he watched it appear in the
    /// conversation, and a run that ended a second later is not a reason for it to go nowhere.</summary>
    public async Task<string> Run(string mission,
        long? budgetTokens = null, CancellationToken cancel = default)
    {
        string outcome = await RunOnce(mission, budgetTokens, cancel).ConfigureAwait(false);
        while (!_disposed && _agent is not null && !cancel.IsCancellationRequested
            && _plane?.TakeOwnerMessages() is { Length: > 0 } missed)
            outcome = await RunOnce(missed, budgetTokens, cancel).ConfigureAwait(false);
        return outcome;
    }

    async Task<string> RunOnce(string mission, long? budgetTokens, CancellationToken cancel)
    {
        WorkspaceAgent? agent = _agent;
        if (agent is null) return "the workspace computer is not running";
        if (Access?.BeginSupervisor() == false) return "A connected agent already has this workspace. Release its control first.";
        try
        {
            string outcome = await agent.Run(mission, budgetTokens, cancel).ConfigureAwait(false);
            Bank(agent);
            ReleaseIfParked(agent);
            return outcome;
        }
        finally { Access?.EndSupervisor(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Live.Remove(Id);
        KeepLastLook();
        Tear();
        Ended?.Invoke();
        AttentionChanged?.Invoke();
    }

    /// <summary>
    /// What a stopped workspace shows under Recent: the last picture anything took of its screen,
    /// and when it was last used. No new capture is taken for it; stopping stays quick.
    /// </summary>
    void KeepLastLook()
    {
        DateTimeOffset used = DateTimeOffset.Now - (Quiet ?? TimeSpan.Zero);
        try
        {
            if (_plane?.LastFrame is { } frame && AppSettingsStore.Current.Screenshots != ScreenshotMode.Off)
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(frame));
                using var file = File.Create(WorkspaceStore.LastFrameOf(Id));
                png.Save(file);
            }
            WorkspaceStore.Update(Id, stored => stored with { LastUsed = used });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void Tear()
    {
        if (Access is not null) { Access.Changed -= OnAccessChanged; Access.Dispose(); Access = null; }
        _agent?.Dispose();
        _agent = null;
        _plane?.Dispose();
        _plane = null;
        _computer?.Dispose();
        _computer = null;
    }

    /// <summary>
    /// Adds one finished invocation to the workspace's running total. Every way a mission is run
    /// ends here - a mission the owner sent, a park waking itself, an interrupted one picked back
    /// up - because all three are the same invocation and a total that counted only the first
    /// would quietly under-report a workspace that works while nobody is watching.
    ///
    /// Banked when the run ends rather than as it goes, because the CLI's own result is the
    /// authoritative number and the count kept during the run is an estimate standing in for it.
    /// Read-modify-write against the record, like everything else that writes to it from here.
    /// </summary>
    void Bank(WorkspaceAgent agent)
    {
        if (agent.UsageTokens <= 0 && agent.UsageUsd <= 0) return;
        WorkspaceStore.Update(Id, stored => stored.WithRun(agent.UsageTokens, agent.UsageUsd));
        Metered?.Invoke(true);
    }

    void ReleaseIfParked(WorkspaceAgent agent)
    {
        if (agent.State != MissionState.Waiting) return;
        _owner.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed && ReferenceEquals(_agent, agent) && agent.State == MissionState.Waiting)
                Dispose();
        }));
    }

    /// <summary>
    /// Everything that has to outlive this process goes on the workspace's record here - the
    /// conversation's id, when a parked mission wants waking, and what it said it was waiting for.
    /// Read-modify-write against the folder rather than saving a whole record this object is holding,
    /// because the panel writes the name, the mission and the power to the same file.
    /// </summary>
    void Remember()
    {
        if (_agent is null) return;
        WorkspaceStore.Update(Id, stored => stored with
        {
            Session = _agent.SessionId,
            WakeAt = _agent.WakeAt,
            WakeNote = _agent.WakeNote,
            // Travels with the conversation, not with the desktop. A session that has seen a page
            // has seen it for good; only starting a new conversation clears it, and that happens at
            // the source in WorkspaceAgent rather than here.
            SessionReadUntrustedContent = _plane?.ReadUntrustedContent ?? false,
            Mission = _agent.State,
            Outcome = _agent.Outcome,
            MissionAt = DateTimeOffset.Now,
            // A state the owner has not been shown yet must stay unannounced, and a state he has
            // been shown must not be announced again by the next thing that saves the record.
            Announced = WorkspaceMissions.WorthTelling(_agent.State) && stored.Announced == _agent.State
                ? stored.Announced : WorkspaceMissions.WorthTelling(_agent.State) ? stored.Announced
                : MissionState.Idle,
        });
        // A park just made in this process. Arm one wake for its actual due time. If a run cleared
        // an existing park, review the records once so the old timer is cancelled or moved to the
        // next workspace rather than waking for a record that no longer exists.
        if (_agent.WakeAt is { } due && !_ticking) Hurry(due);
        else if (_nextWake is not null) Review();
    }

    // ---- the clock ------------------------------------------------------------
    //
    // One clock for the module, started at app startup and again by any panel that opens, because a
    // panel can be built in a host that never called Initialize. Starting it twice does nothing.

    /// <summary>Starts the module's clock if it is not already running. Safe to call repeatedly.</summary>
    public static void Watch()
    {
        if (_clock is not null) return;
        Dispatcher owner = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!owner.CheckAccess()) { owner.BeginInvoke(Watch); return; }
        _clock = new DispatcherTimer(DispatcherPriority.Background, owner) { Interval = Beat };
        _clock.Tick += Tick;
        // Nothing can still be running in a process that has only just started, so a record left
        // saying Working is a mission HiveMind or Windows stopped. It becomes Interrupted here,
        // once, before anything reads it - and the owner is told about anything he has not seen.
        Interrupted = WorkspaceMissions.FindInterrupted().Count;
        WorkspaceMissions.AnnounceWaiting();
        // Queued, not called. A wake that is already due starts a Windows desktop and launches a
        // shell into it, and Initialize runs during HiveMind's own startup, on its only thread.
        owner.BeginInvoke(DispatcherPriority.Background, new Action(() => Tick(null, EventArgs.Empty)));
    }

    /// <summary>How many missions this HiveMind found interrupted when it started.</summary>
    public static int Interrupted { get; private set; }

    /// <summary>
    /// Picks an interrupted mission back up. It is the same run as a wake - the CLI is handed its
    /// own conversation back and told what happened - so this is not a second way to run a mission.
    /// Returns false when the workspace has nothing to resume or the agent cannot be started.
    /// </summary>
    public static bool ResumeInterrupted(StoredWorkspace stored)
    {
        if (stored.Mission != MissionState.Interrupted || stored.Session.Length == 0) return false;
        string? cli = WorkspaceAgent.FindCli();
        if (cli is null || !WorkspaceAgentSetup.IsTrustedCli(cli)) return false;
        WorkspaceRuntime runtime;
        try { runtime = Start(stored); }
        catch (InvalidOperationException) { return false; }
        if (runtime._agent is null || runtime.Access?.HasDriver == true) return false;
        WorkspaceStore.Update(stored.Id, one => one with
        {
            Mission = MissionState.Working,
            MissionAt = DateTimeOffset.Now,
            Announced = MissionState.Idle,
        });
        runtime.Doing = "Resuming";
        runtime.Acting?.Invoke("Resuming", string.Empty);
        _ = runtime.Resume(true, interrupted: true);
        return true;
    }

    /// <summary>Stops the clock and every running workspace. Called once, at app exit.</summary>
    public static void Rest()
    {
        if (_clock is not null)
        {
            _clock.Stop();
            _clock.Tick -= Tick;
            _clock = null;
        }
        _nextWake = null;
        _reviewQueued = false;
        _sleeper?.Stop();
        _sweeper?.Dispose();
        _sweeper = null;
        foreach (WorkspaceRuntime runtime in Live.Values.ToArray()) runtime.Dispose();
        Live.Clear();
    }

    /// <summary>Something was just parked. Wake once when it is actually due.</summary>
    static void Hurry(DateTimeOffset due)
    {
        if (_clock is null) return;
        if (!_clock.Dispatcher.CheckAccess())
        {
            _clock.Dispatcher.BeginInvoke(() => Hurry(due));
            return;
        }
        Arm(due);
    }

    static void Review()
    {
        if (_clock is null || _reviewQueued) return;
        if (!_clock.Dispatcher.CheckAccess()) { _clock.Dispatcher.BeginInvoke(Review); return; }
        _reviewQueued = true;
        _clock.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _reviewQueued = false;
            Tick(null, EventArgs.Empty);
        }));
    }

    static void Arm(DateTimeOffset due)
    {
        if (_clock is null) return;
        if (_clock.IsEnabled && _nextWake is { } current && current <= due) return;
        _clock.Stop();
        _nextWake = due;
        _clock.Interval = DelayUntil(DateTimeOffset.Now, due);
        _clock.Start();
    }

    internal static TimeSpan DelayUntil(DateTimeOffset now, DateTimeOffset due)
    {
        TimeSpan delay = due - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// Reads the store, wakes anything due, and decides how soon to look again. It is a scan, not a
    /// schedule: the record on disk is the only thing a parked mission leaves behind, so the record
    /// is what gets read.
    /// </summary>
    static void Tick(object? sender, EventArgs e)
    {
        if (_ticking) return;
        _ticking = true;
        _clock?.Stop();
        _nextWake = null;
        var clock = Stopwatch.StartNew();
        DateTimeOffset? next = null;
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (StoredWorkspace stored in WorkspaceStore.All())
            {
                if (stored.WakeAt is not { } due) continue;
                if (now < due)
                {
                    if (next is null || due < next) next = due;
                    continue;
                }
                if (!Wake(stored))
                {
                    DateTimeOffset retry = now + Beat;
                    if (next is null || retry < next) next = retry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A workspace folder that Windows had open for a moment. The next tick reads it again.
            next = DateTimeOffset.Now + Beat;
        }
        finally
        {
            Ticks++;
            LastScanMs = clock.Elapsed.TotalMilliseconds;
            _ticking = false;
            if (next is { } due) Arm(due);
        }
    }

    // What the clock costs, kept because a timer that runs for the life of the app has to be able to
    // show its bill rather than be assumed cheap.
    public static int Ticks { get; private set; }
    public static double LastScanMs { get; private set; }
    public static TimeSpan Every => _clock?.IsEnabled == true ? _clock.Interval : TimeSpan.Zero;

    /// <summary>
    /// Hands one parked mission its conversation back. Starts the workspace's computer first if
    /// HiveMind took it, and tells the agent so - there is nothing else to reattach to, because a
    /// parked mission deliberately leaves nothing running.
    ///
    /// Returns false when it could not be woken, so the caller keeps checking rather than treating
    /// a mission it never resumed as finished.
    /// </summary>
    public static bool Wake(StoredWorkspace stored)
    {
        string? cli = WorkspaceAgent.FindCli();
        if (cli is null || !WorkspaceAgentSetup.IsTrustedCli(cli)) return false;
        if (stored.AgentCredentials == WorkspaceAgentCredentialMode.Subscription
            && !WorkspaceAgent.OnSubscription()) return false;
        WorkspaceRuntime? runtime = Of(stored.Id);
        bool restarted = runtime is null;
        try
        {
            runtime ??= Start(stored);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (runtime._agent is null) return false;
        if (runtime.Access?.HasDriver == true) return false;
        runtime.Doing = "Waking up";
        runtime.Acting?.Invoke("Waking up", string.Empty);
        _ = runtime.Resume(restarted);
        return true;
    }

    async Task Resume(bool restarted, bool interrupted = false)
    {
        WorkspaceAgent? agent = _agent;
        if (agent is null) return;
        if (Access?.BeginSupervisor() == false) return;
        try
        {
            await agent.Wake(restarted, interrupted).ConfigureAwait(false);
            Bank(agent);
            ReleaseIfParked(agent);
        }
        finally { Access?.EndSupervisor(); }
    }

    /// <summary>Windows PowerShell, which every Windows has. Nothing is installed to get one.</summary>
    internal static string TerminalArguments(string folder)
    {
        // Known-folder APIs ignore APPDATA redirection. Explicitly scope PSReadLine history and
        // skip the owner's profile (which may start conda, write owner files, or run other hooks).
        string history = Path.Combine(folder, "terminal-history.txt").Replace("'", "''");
        string setup = "Import-Module PSReadLine -ErrorAction SilentlyContinue; "
            + "if (Get-Command Set-PSReadLineOption -ErrorAction SilentlyContinue) { "
            + "Set-PSReadLineOption -HistorySavePath '" + history + "' }";
        return "-NoLogo -NoProfile -NoExit -EncodedCommand "
            + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(setup));
    }

    static string TerminalPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");
}
