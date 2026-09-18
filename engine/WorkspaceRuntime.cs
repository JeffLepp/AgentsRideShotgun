using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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

    /// <summary>How long a workspace nobody uses keeps its desktop and browser (MVP_SPEC, Sleep).</summary>
    internal static TimeSpan SleepAfter { get; set; } = TimeSpan.FromMinutes(30);
    // One shared clock while anything runs, none while nothing does.
    static DispatcherTimer? _sleeper;
    long _touched = Environment.TickCount64;

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
    public static WorkspaceRuntime? Of(string id) =>
        Live.TryGetValue(id, out WorkspaceRuntime? found) ? found : null;

    /// <summary>True while any workspace has a computer running.</summary>
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
        if (Of(workspace.Id) is { } already) return already;
        var runtime = new WorkspaceRuntime(workspace);
        Live[workspace.Id] = runtime;
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
    /// owner's hands on it (Pause every agent included), or a question for the owner.
    /// </summary>
    internal TimeSpan? Quiet
    {
        get
        {
            if (_plane?.Driving == Driver.Owner) return null;
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
        _plane?.Dispose();
        _plane = null;
        _computer?.Dispose();
        _computer = null;
    }

    /// <summary>Stops every running workspace and the shared clocks. Called once, at app exit.</summary>
    public static void Rest()
    {
        _sleeper?.Stop();
        _sweeper?.Dispose();
        _sweeper = null;
        foreach (WorkspaceRuntime runtime in Live.Values.ToArray()) runtime.Dispose();
        Live.Clear();
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
