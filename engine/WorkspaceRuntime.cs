using System.IO;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// A workspace's live half: its Windows desktop and its control plane, held together and held
/// outside any window. A runtime belongs to the workspace's id, so everything looking at that
/// workspace - the hub, the corner window, or nothing at all - is looking at the same one, and
/// starting twice never builds a second desktop over the first.
/// </summary>
public sealed class WorkspaceRuntime : IDisposable
{
    static readonly Dictionary<string, WorkspaceRuntime> Live = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lock Lifetime = new();

    /// <summary>
    /// How long a workspace nobody uses keeps its desktop and browser (MVP_SPEC, Sleep). 30 minutes
    /// was the original number; the owner asked for sooner on the belief that waking is free. It is
    /// not, so this is not as short as first tried.
    ///
    /// WorkspaceRuntime.Start() alone is cheap - 15-19 ms on this machine (SleepGate.WakeCost) - but
    /// Start is not what a nap costs. Sleep tears down the whole desktop: the browser process and
    /// its DevTools connection, every app the agent had open, the shell. SleepGate.RoundTripCost
    /// measures the real round trip - browser warm and navigated, one app open, sleep, wake, browser
    /// relaunched and renavigated to the same page, app relaunched - at 5.9-6.2 s on this machine,
    /// repeatably, when nothing else is competing for the machine. Under the load three workspaces
    /// at once actually creates, a Chrome cold start can lose the race and need a retry (observed
    /// directly running this gate), so a few seconds is the floor, not the ceiling. None of that
    /// counts what a timed measurement cannot: tabs beyond the one navigated to, unsaved in-page
    /// state, or an interaction the agent was mid-way through - Chrome is started with
    /// --restore-last-session=false on purpose, so that is really gone, not just slow to fetch back.
    ///
    /// So sleeping is not free, and a workspace an agent pauses on for a few minutes, or the owner
    /// reads for six, must not pay that repeatedly. 30 minutes was too patient about a desktop and a
    /// Chrome nobody answered; 15 is the number this settles on - still half the original, which is
    /// the "sooner" the owner asked for, but long enough that an ordinary pause is not what triggers
    /// the teardown this comment just finished describing.
    /// </summary>
    internal static TimeSpan SleepAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the shared clock checks for something to sleep. The engine probe shortens
    /// this so a check can watch a real tick happen instead of waiting a full minute.</summary>
    internal static TimeSpan DozeInterval { get; set; } = TimeSpan.FromMinutes(1);

    // The shared pool clock works in a headless host too. In WPF it queues cleanup on the app
    // dispatcher, because stopping a runtime raises events consumed by its windows.
    static Timer? _sleeper;
    static int _dozeQueued;
    long _touched = Environment.TickCount64;
    readonly Lock _lastLook = new();
    int _savingLook;

    AgentDesktop? _computer;
    WorkspaceControl? _plane;
    internal WorkspaceExternalAccess? Access { get; private set; }

    /// <summary>
    /// Something changed that anything drawing a workspace from outside it has no other way to
    /// hear about: a request that needs the owner, or a workspace starting or stopping.
    /// </summary>
    internal static event Action? AttentionChanged;
    internal event Action? AccessChanged;
    bool _disposed;

    WorkspaceRuntime(StoredWorkspace workspace)
    {
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

    public event Action<Driver>? DriverChanged;

    /// <summary>Raised when this runtime stops, so anything drawing it stops drawing it live.</summary>
    public event Action? Ended;

    public WorkspacePower Power
    {
        set { if (_computer is not null) _computer.Power = value; }
    }

    /// <summary>The live runtime for a workspace, or null when its computer is not running.</summary>
    public static WorkspaceRuntime? Of(string id)
    {
        lock (Lifetime) return Live.GetValueOrDefault(id);
    }

    /// <summary>True while any workspace has a computer running.</summary>
    public static bool AnyRunning { get { lock (Lifetime) return Live.Count > 0; } }

    /// <summary>
    /// Every workspace running right now. A copy, because the corner view reads this whenever a
    /// workspace starts or stops and a runtime disposing itself mid-read would otherwise be an
    /// exception in a window that is only watching.
    /// </summary>
    internal static IReadOnlyList<WorkspaceRuntime> Running { get { lock (Lifetime) return Live.Values.ToArray(); } }

    void OnAccessChanged() { AccessChanged?.Invoke(); AttentionChanged?.Invoke(); }

    /// <summary>
    /// The workspace's runtime, started if it was not already running. Starting twice is what built
    /// two desktops over one workspace, so there is one way in and it is this.
    /// </summary>
    public static WorkspaceRuntime Start(StoredWorkspace workspace)
    {
        WorkspaceRuntime runtime;
        lock (Lifetime)
        {
            if (Of(workspace.Id) is { } already) return already;
            runtime = new WorkspaceRuntime(workspace);
            Live[workspace.Id] = runtime;
            _sleeper ??= new Timer(_ => QueueDoze(), null, Timeout.Infinite, Timeout.Infinite);
            // Reapply after a test changes the interval or a workspace wakes after Rest().
            _sleeper.Change(DozeInterval, DozeInterval);
        }
        AttentionChanged?.Invoke();
        return runtime;
    }

    /// <summary>
    /// How long nothing has happened here, or null while something is: an agent holding it, or the
    /// owner's hands on it (Pause every agent included).
    ///
    /// A pending desktop request used to belong in that list too, and made this unbounded: an
    /// agent that calls request_desktop and never comes back, and an owner who never clicks it,
    /// pinned the desktop and its Chrome open forever. A request no longer holds the workspace open
    /// by itself - RequestDesktop runs under a Use() lease, which already counts as activity and
    /// resets the idle clock the normal way, so a workspace with a stale, unanswered request still
    /// sleeps after the usual <see cref="SleepAfter"/> of nothing else happening. Sleeping does not
    /// discard the request silently: WorkspaceExternalAccess.Dispose resolves whatever is left
    /// pending the same way turning access off already does, so nothing is left claiming to be
    /// waiting on a workspace that no longer exists.
    /// </summary>
    internal TimeSpan? Quiet
    {
        get
        {
            if (_plane?.Driving == Driver.Owner) return null;
            if (Access is { HasDriver: true }) return null;
            var commands = _plane?.Commands.Activity;
            if (commands is { Running: true }) return null;
            return TimeSpan.FromMilliseconds(Environment.TickCount64
                - Math.Max(Math.Max(_touched, Access?.LastActive ?? 0), commands?.LastActive ?? 0));
        }
    }

    /// <summary>
    /// Whether this workspace reads as idle right now: nobody is driving it and nothing is using it.
    /// The UI needs this to stop drawing an idle workspace the same as one mid-task - today both are
    /// one blue dot. Backed by <see cref="Quiet"/> so there is exactly one idea of "idle" in the
    /// engine, not a second one invented for display.
    /// </summary>
    public bool IsIdle => Quiet is not null;

    /// <summary>
    /// How long until this workspace sleeps on its own, or null while it is not idle at all (nothing
    /// is counting down). Clamped at zero rather than going negative once a workspace is overdue -
    /// the clock only checks once every <see cref="DozeInterval"/>, so there is a real, if short,
    /// window where a workspace has passed its bound and not yet been swept.
    ///
    /// This is the ordinary idle-timeout answer only. A workspace can still sleep sooner than this
    /// says, as the quietest one picked to make room when <see cref="WorkspaceLimits.MemoryLoad"/>
    /// is nearly full - <see cref="MemoryLoad"/> is what explains that case to the owner.
    /// </summary>
    public TimeSpan? SleepsIn
    {
        get
        {
            if (Quiet is not { } quiet) return null;
            TimeSpan left = SleepAfter - quiet;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// How much of the machine's memory is in use right now, 0-100. The same figure Doze() acts on
    /// at 90 to sleep the quietest workspace and make room - surfaced here rather than computed a
    /// second way, so the owner sees the one number the engine actually decides by.
    /// </summary>
    public static uint MemoryLoad => WorkspaceLimits.MemoryLoad();

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
        if (Quiet is null) return false;
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
        // Runtime events update WPF views. A pool timer must never tear those views down itself.
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui is not null && !ui.CheckAccess()) { QueueDoze(); return; }
        lock (Lifetime)
        {
            foreach (WorkspaceRuntime runtime in Running)
            {
                runtime.KeepLookWhileRunning();
                if (runtime.Id != WorkspacePeekHost.PinnedOn && runtime.Quiet >= SleepAfter) runtime.TrySleep();
            }
            if (WorkspaceLimits.MemoryLoad() >= 90) SleepQuietest();
            if (Live.Count == 0) _sleeper?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    static void QueueDoze()
    {
        if (Interlocked.Exchange(ref _dozeQueued, 1) != 0) return;
        void Tick()
        {
            try { Doze(); }
            finally { Volatile.Write(ref _dozeQueued, 0); }
        }
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui is null || ui.CheckAccess()) { Tick(); return; }
        if (ui.HasShutdownStarted) { Volatile.Write(ref _dozeQueued, 0); return; }
        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)Tick);
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

    public void Dispose()
    {
        lock (Lifetime)
        {
            if (_disposed) return;
            // Drain any writer before the final frame. A queued writer must see disposal and
            // cannot overwrite the last frame or collide with the next runtime for this ID.
            lock (_lastLook)
            {
                _disposed = true;
                KeepLastLook();
            }
            Live.Remove(Id);
            Tear();
        }
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
            WriteLastLook(_plane?.LastFrame);
            WorkspaceStore.Update(Id, stored => stored with { LastUsed = used });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The same picture, kept while the workspace is still running. Writing it only on the way out
    /// meant a workspace whose app was killed - every publish does exactly that - had no picture at
    /// all afterwards, and its row under Recent was an empty rectangle no different from any other
    /// workspace's. Off the clock's own thread; the frame is already frozen.
    /// </summary>
    void KeepLookWhileRunning()
    {
        if (_disposed || _plane?.LastFrame is not { } frame || Interlocked.Exchange(ref _savingLook, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { lock (_lastLook) { if (!_disposed) WriteLastLook(frame); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { Volatile.Write(ref _savingLook, 0); }
        });
    }

    void WriteLastLook(System.Windows.Media.Imaging.BitmapSource? frame)
    {
        lock (_lastLook)
        {
            if (frame is null || AppSettingsStore.Current.Screenshots == ScreenshotMode.Off) return;
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(frame));
            // Rename only a complete image, serialized with periodic and final writes.
            string path = WorkspaceStore.LastFrameOf(Id);
            string part = path + ".part";
            using (FileStream file = File.Create(part)) png.Save(file);
            File.Move(part, path, overwrite: true);
        }
    }

    void Tear()
    {
        if (Access is not null) { Access.Changed -= OnAccessChanged; Access.Dispose(); Access = null; }
        _plane?.Dispose();
        _plane = null;
        _computer?.Dispose();
        _computer = null;
    }

    /// <summary>Stops every running workspace and the shared clocks. Called once, at app exit.</summary>
    public static void Rest()
    {
        lock (Lifetime)
        {
            _sleeper?.Change(Timeout.Infinite, Timeout.Infinite);
            _sweeper?.Dispose();
            _sweeper = null;
            foreach (WorkspaceRuntime runtime in Live.Values.ToArray()) runtime.Dispose();
            Live.Clear();
        }
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
