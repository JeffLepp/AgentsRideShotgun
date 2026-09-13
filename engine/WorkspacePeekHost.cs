using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Runs the corner view for the whole module: which workspace it shows, when it is on screen, and
/// the one global hotkey that calls it up.
///
/// It lives here rather than in the panel for the same reason the runtime does. A workspace works
/// with its page closed and HiveMind minimised - that is the point of it - so a window that only
/// existed while the owner was already looking at the panel would appear exactly when it was not
/// needed. Nothing here runs while the corner view is off: no timer, no capture, no window.
/// </summary>
internal static class WorkspacePeekHost
{
    /// <summary>How often the picture is refreshed while it is on screen. Two and a half frames a
    /// second, and it borrows the panel's own capture whenever the panel is open.</summary>
    static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(400);

    /// <summary>A picture younger than this is used as it is rather than taking another.</summary>
    static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(350);

    static Dispatcher? _owner;
    static PeekSettings _settings = new();
    static WorkspacePeekWindow? _window;
    static WorkspacePeekHotkey? _key;
    static DispatcherTimer? _beat;
    static WorkspaceRuntime? _followed;
    static string _name = string.Empty;
    static DateTimeOffset _stirred;
    static bool _started, _pinned, _dismissed, _drawing, _hooked;

    /// <summary>The corner view moved, was pinned, or took a new hotkey. The panel redraws its own
    /// controls from this, so the settings section is never out of step with the window itself.</summary>
    internal static event Action? Changed;

    /// <summary>The settings as they are now. The panel edits a copy and calls <see cref="Apply"/>.</summary>
    internal static PeekSettings Settings => _settings;

    /// <summary>Whether Windows actually gave HiveMind the hotkey. False when another program holds it.</summary>
    internal static bool HotkeyHeld { get; private set; }

    /// <summary>Whether the owner has pinned it up. Told to the panel so the checkbox is honest.</summary>
    internal static bool Pinned => _pinned;

    /// <summary>The window itself, so a probe can read what is actually on screen. Nothing in the
    /// app uses this: the panel asks about the settings, never about the window.</summary>
    internal static WorkspacePeekWindow? WindowForProbes => _window;

    /// <summary>
    /// Starts watching. Called at app startup and again by any panel that is built, because a panel
    /// can be created in a host that never called Initialize. Starting twice does nothing.
    /// </summary>
    internal static void Start()
    {
        Dispatcher owner = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!owner.CheckAccess()) { owner.BeginInvoke(Start); return; }
        if (_started) return;
        _started = true;
        _owner = owner;
        _settings = WorkspacePeekStore.Read();
        WorkspaceRuntime.AttentionChanged += Attention;
        // The shell closing takes its own windows with it; this one is nobody's child, so without
        // this HiveMind can exit leaving a picture of a workspace floating over the desktop.
        if (Application.Current is { } app && !_hooked)
        {
            _hooked = true;
            app.Exit += (_, _) => Stop();
        }
        HoldHotkey();
        Rethink();
    }

    /// <summary>App exit, or the module being uninstalled. Gives the hotkey back to Windows.</summary>
    internal static void Stop()
    {
        if (!_started) return;
        _started = false;
        WorkspaceRuntime.AttentionChanged -= Attention;
        Follow(null);
        StopBeat();
        _key?.Dispose();
        _key = null;
        HotkeyHeld = false;
        WorkspacePeekWindow? window = _window;
        _window = null;
        window?.Close();
        _pinned = false;
        _dismissed = false;
    }

    /// <summary>
    /// Takes new settings, saves them, and obeys them now rather than at the next start. Returns
    /// false when they could not be written; what is on screen has changed either way, so the panel
    /// says so instead of pretending the choice did not take.
    /// </summary>
    internal static bool Apply(PeekSettings settings)
    {
        _settings = settings.Sane();
        bool saved = WorkspacePeekStore.Write(_settings);
        if (_started)
        {
            HoldHotkey();
            // A mode the owner has just turned on should not wait for the agent's next tool call
            // to prove itself. Treat the change as activity.
            _stirred = DateTimeOffset.Now;
            _dismissed = false;
            Rethink();
        }
        Changed?.Invoke();
        return saved;
    }

    static void HoldHotkey()
    {
        _key ??= Listener();
        HotkeyHeld = _key?.Hold(_settings.Hotkey) == true;
    }

    static WorkspacePeekHotkey? Listener()
    {
        // A host with no message loop of its own - a probe, a test - simply has no hotkey. The
        // corner view still works from the panel.
        try
        {
            var key = new WorkspacePeekHotkey();
            key.Pressed += Pressed;
            return key;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The hotkey. It pins the view up and takes it down again, and it works whatever the mode is -
    /// including Off, where the key is the only way in. With nothing running there is no picture to
    /// show, so it opens the app instead of putting an empty window on the owner's screen.
    /// </summary>
    static void Pressed()
    {
        if (!WorkspaceRuntime.AnyRunning) { Open(); return; }
        _pinned = !_pinned;
        _dismissed = false;
        _stirred = DateTimeOffset.Now;
        Rethink();
        Changed?.Invoke();
    }

    /// <summary>A workspace started or stopped, or one is asking for something.</summary>
    static void Attention()
    {
        if (_owner is null) return;
        if (!_owner.CheckAccess()) { _owner.BeginInvoke(Attention); return; }
        Stir();
    }

    /// <summary>The workspace did something. This is what "activity" means to the fade.</summary>
    static void Stir()
    {
        _stirred = DateTimeOffset.Now;
        // Dismissing it means "not now", not "not again". The next thing the agent does brings it
        // back, which is the whole reason the owner turned an activity view on.
        _dismissed = false;
        Rethink();
    }

    static void StirFrom()
    {
        if (_owner is not { } owner) return;
        if (owner.CheckAccess()) Stir(); else owner.BeginInvoke(Stir);
    }

    /// <summary>
    /// Decides, from the settings and what the workspaces are doing, whether the window is up - and
    /// puts it where the owner left it. Everything else here ends in a call to this.
    /// </summary>
    static void Rethink()
    {
        if (!_started || _owner is null) return;
        // Off, and not called up by the hotkey: nothing to follow, no window, and no timer. The
        // module's idle cost with the corner view off is exactly what it was before it existed.
        Follow(_settings.Mode == PeekMode.Off && !_pinned ? null : Pick());

        bool wanted = !_dismissed && WorkspacePeekPolicy.Wanted(_settings.Mode, _followed is not null,
            Busy(_followed), DateTimeOffset.Now - _stirred,
            TimeSpan.FromSeconds(_settings.QuietSeconds), _pinned);

        if (!wanted)
        {
            _window?.Leave();
            StopBeat();
            return;
        }

        WorkspacePeekWindow window = _window ??= Build();
        Position(window);
        window.ShowPinned(_pinned);
        Say(window);
        window.Arrive();
        StartBeat();
        Draw();
    }

    static WorkspacePeekWindow Build()
    {
        var window = new WorkspacePeekWindow();
        window.OpenRequested += Open;
        window.PinClicked += () =>
        {
            _pinned = !_pinned;
            Rethink();
            Changed?.Invoke();
        };
        window.HideRequested += () =>
        {
            _dismissed = true;
            _pinned = false;
            Rethink();
            Changed?.Invoke();
        };
        window.Dropped += Dropped;
        return window;
    }

    /// <summary>The owner wants the whole workspace, not a glance at it.</summary>
    static void Open()
    {
        if (_followed is { } runtime) ModuleEntry.Selected = runtime.Id;
        ModuleEntry.RequestDashboardOpen();
        // He pressed a key over a game, or clicked a window floating above one: the shell itself
        // may be minimised or on the tray. Opening a page inside a window he cannot see is not
        // opening it.
        if (Application.Current?.MainWindow is not { } shell) return;
        if (shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal;
        shell.Show();
        shell.Activate();
    }

    /// <summary>
    /// Where it was dragged to. A drop still on the main screen becomes the nearest corner, so it
    /// stays put when the taskbar or the resolution changes; a drop on another monitor is kept as
    /// the exact place it was put, because no corner in these settings describes that monitor.
    /// </summary>
    static void Dropped(Rect where)
    {
        Rect work = SystemParameters.WorkArea;
        Apply(WorkspacePeekPlacement.OnWorkArea(work, where)
            ? _settings with { Corner = WorkspacePeekPlacement.Nearest(work, where), Left = null, Top = null }
            : _settings with { Left = where.Left, Top = where.Top });
    }

    static void Position(WorkspacePeekWindow window)
    {
        Size size = _settings.Size;
        Rect screens = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        window.Place(_settings.Left is { } left && _settings.Top is { } top
            ? WorkspacePeekPlacement.Fit(screens, new Rect(left, top, size.Width, size.Height))
            : WorkspacePeekPlacement.Place(SystemParameters.WorkArea, _settings.Corner, size));
    }

    /// <summary>
    /// Which workspace the corner view is of. The one that is working, then the one that wants the
    /// owner, then whichever it was already showing - so a workspace does not swap out from under
    /// him the moment two are running.
    /// </summary>
    static WorkspaceRuntime? Pick()
    {
        IReadOnlyList<WorkspaceRuntime> running = WorkspaceRuntime.Running;
        return running.FirstOrDefault(one => one.Agent?.State == MissionState.Working)
            ?? running.FirstOrDefault(one => one.Agent?.State == MissionState.NeedsYou
                || one.Access?.HasDriver == true)
            ?? running.FirstOrDefault(one => ReferenceEquals(one, _followed))
            ?? running.LastOrDefault();
    }

    /// <summary>Listens to one workspace at a time, and only while there is a corner view to feed.</summary>
    static void Follow(WorkspaceRuntime? runtime)
    {
        if (ReferenceEquals(runtime, _followed)) return;
        if (_followed is { } left)
        {
            left.Acting -= Acted;
            left.Said -= Spoke;
            left.Moved -= MovedOn;
            left.DriverChanged -= Drove;
            left.Ended -= Ended;
        }
        _followed = runtime;
        _name = runtime is null ? string.Empty
            : WorkspaceStore.Find(runtime.Id)?.Name ?? "Workspace";
        if (runtime is null) return;
        runtime.Acting += Acted;
        runtime.Said += Spoke;
        runtime.Moved += MovedOn;
        runtime.DriverChanged += Drove;
        runtime.Ended += Ended;
    }

    // The runtime raises these on whatever thread the agent is on. Every one of them lands back on
    // the shell's thread before it touches a window.
    static void Acted(string tool, string detail) => StirFrom();
    static void Spoke(string said) => StirFrom();
    static void MovedOn(MissionState state) => StirFrom();
    static void Drove(Driver who) => StirFrom();
    static void Ended() => StirFrom();

    /// <summary>Whether this workspace is doing something the owner would want to see.</summary>
    static bool Busy(WorkspaceRuntime? runtime) => runtime is not null
        && (runtime.Agent?.State is MissionState.Working or MissionState.NeedsYou
            || runtime.Access?.HasDriver == true
            || runtime.Plane?.Driving == Driver.Owner
            || runtime.Access?.Handoffs.All.Any(request => request.State == "pending") == true);

    /// <summary>The line under the workspace's name, in the words every other surface uses.</summary>
    static void Say(WorkspacePeekWindow window)
    {
        if (_followed is not { } runtime) return;
        MissionState state = runtime.Agent?.State ?? MissionState.Idle;
        bool requests = runtime.Access?.Handoffs.All.Any(request => request.State == "pending") == true;
        (string doing, string dot) =
            requests ? ("Desktop request · Needs you", "AWWaitingBrush")
            : runtime.Plane?.Driving == Driver.Owner ? ("You have control", "AWAccentBrush")
            : runtime.Access?.Controller is { Length: > 0 } controller ? (controller + " is working", "AWWorkingBrush")
            : state switch
            {
                MissionState.Working => (runtime.Doing.Length > 0 ? runtime.Doing : "Working", "AWWorkingBrush"),
                MissionState.NeedsYou => ("Needs you", "AWWaitingBrush"),
                MissionState.Failed => ("Stopped", "AWFailedBrush"),
                MissionState.Interrupted => ("Interrupted", "AWFailedBrush"),
                MissionState.Done => ("Done", "AWWorkingBrush"),
                MissionState.Waiting => ("Waiting", "AWWaitingBrush"),
                _ => ("Running", "AWIdleBrush"),
            };
        window.Describe(_name, doing, dot);
    }

    static void StartBeat()
    {
        if (_beat is not null || _owner is null) return;
        _beat = new DispatcherTimer(DispatcherPriority.Background, _owner) { Interval = Beat };
        _beat.Tick += Tick;
        _beat.Start();
    }

    static void StopBeat()
    {
        if (_beat is null) return;
        _beat.Stop();
        _beat.Tick -= Tick;
        _beat = null;
    }

    // The only timer this feature has, and it runs only while the window is actually on screen.
    // It is also what makes the fade happen: the quiet time is checked here, not by a second clock.
    static void Tick(object? sender, EventArgs e) => Rethink();

    /// <summary>
    /// One picture, taken off the UI thread the way the panel takes its own. A capture is bounded
    /// but a bounded one is still up to two seconds, and the shell's thread must never spend two
    /// seconds anywhere.
    /// </summary>
    static void Draw()
    {
        WorkspaceRuntime? runtime = _followed;
        if (_drawing || _owner is not { } owner || _window is not { Watching: true }) return;
        if (runtime?.Plane is not { } plane) return;
        _drawing = true;
        Task.Run<BitmapSource?>(() =>
        {
            try { return plane.Glance(Fresh); }
            catch (ObjectDisposedException) { return null; }
        }).ContinueWith(taken => owner.BeginInvoke(() =>
        {
            _drawing = false;
            // The workspace it was of may have stopped, or the view may have moved to another one,
            // while the blit was in flight. A picture of the wrong desktop is worse than none.
            if (taken.Result is not { } frame || !ReferenceEquals(runtime, _followed)) return;
            if (_window is { Watching: true } window) window.ShowFrame(frame);
        }), TaskScheduler.Default);
    }
}
