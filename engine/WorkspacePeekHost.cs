using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Runs the corner view for the whole module: which workspace (or two) it shows, when it is on
/// screen, the picture it draws, and the two global hotkeys. It lives here rather than in a panel
/// for the same reason the runtime does: a workspace works with every window of HiveMind closed,
/// so a corner view that only existed while a panel was open would appear exactly when it was not
/// needed. Nothing here runs while it has nothing to show: no timer, no capture, no window.
/// </summary>
internal static class WorkspacePeekHost
{
    static Dispatcher? _owner;
    static AppSettings _settings = new();
    static WorkspacePeekWindow? _window;
    static WorkspacePeekHotkey? _pauseKey;
    static DispatcherTimer? _beat;
    static WorkspacePeekDropHook? _dropHook;

    // Every running workspace this module is watching, so it knows which one most recently did
    // something even when neither is the one on screen right now.
    static readonly Dictionary<string, Follow> _followed = [];
    static readonly List<string> _recent = []; // ids, most recently active first

    // When each followed workspace's current run started (its Agent last became Working), so the
    // result chip only ever offers a file that run actually made.
    static readonly Dictionary<string, DateTimeOffset> _runStarted = [];

    // The ids Pause every agent itself took control of, so a second press hands back only those and
    // not a workspace the owner was already driving before he paused.
    static readonly HashSet<string> _pausedByUs = [];

    static string? _frontId, _backId, _resultForId, _resultName, _resultPath, _pendingId;

    /// <summary>The workspace a pinned corner window is showing, which must not be put to sleep
    /// under it. Null when the corner is not pinned or shows nothing.</summary>
    internal static string? PinnedOn => _started && _settings.CornerPinned ? _frontId : null;
    static DateTimeOffset _stirred = DateTimeOffset.MinValue;
    static BitmapSource? _background;
    static DateTimeOffset _backgroundAt;
    static bool _started, _dismissed, _summoned, _drawing, _hooked, _pausedAll;

    // Remembered only for this run: the width the owner last grew it to, so the shrink button can
    // offer to grow back once he has shrunk it again. Size and position that must survive a real
    // restart live in AppSettings (CornerWidth, CornerLeft/Top) instead.
    static double? _lastGrownWidth;

    internal static void Start()
    {
        Dispatcher owner = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!owner.CheckAccess()) { owner.BeginInvoke(Start); return; }
        if (_started) return;
        _started = true;
        _owner = owner;
        _settings = AppSettingsStore.Current;
        AppSettingsStore.Changed += SettingsChanged;
        WorkspaceRuntime.AttentionChanged += Attention;
        ModuleEntry.HubShowingChanged += HubChanged;
        ModuleEntry.ShowCornerRequested += ShowCornerNow;
        ModuleEntry.PauseAllRequested += TogglePauseAll;
        if (Application.Current is { } app && !_hooked) { _hooked = true; app.Exit += (_, _) => Stop(); }
        HoldHotkeys();
        Sync();
    }

    internal static void Stop()
    {
        if (!_started) return;
        _started = false;
        AppSettingsStore.Changed -= SettingsChanged;
        WorkspaceRuntime.AttentionChanged -= Attention;
        ModuleEntry.HubShowingChanged -= HubChanged;
        ModuleEntry.ShowCornerRequested -= ShowCornerNow;
        ModuleEntry.PauseAllRequested -= TogglePauseAll;
        _pausedAll = false;
        foreach (WorkspaceRuntime runtime in WorkspaceRuntime.Running)
            if (_pausedByUs.Contains(runtime.Id) && runtime.Plane is { Driving: Driver.Owner } plane)
                plane.Release();
        foreach (string id in _followed.Keys.ToArray()) Unfollow(id);
        _recent.Clear();
        _pausedByUs.Clear();
        StopBeat();
        StopDropHook();
        _pauseKey?.Dispose(); _pauseKey = null;
        _window?.HandBackInput();
        WorkspacePeekWindow? window = _window;
        _window = null;
        window?.Close();
        _dismissed = _summoned = _pausedAll = false;
        _announced.Clear();
        ModuleEntry.AllPaused = false;
        _frontId = _backId = null;
        _background = null;
    }

    static void SettingsChanged(AppSettings settings)
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(() => SettingsChanged(settings)); return; }
        AppSettings previous = _settings;
        _settings = settings;
        if (!_started) return;
        if (previous.PauseHotkey != settings.PauseHotkey) HoldHotkeys();
        Rethink();
    }

    static void HubChanged()
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(HubChanged); return; }
        Rethink();
    }

    static void Attention()
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(Attention); return; }
        Sync();
    }

    /// <summary>Keeps the followed set equal to whatever is running, so recency and busy-ness are
    /// known for every candidate even when only one (or none) is on screen.</summary>
    static void Sync()
    {
        if (!_started) return;
        IReadOnlyList<WorkspaceRuntime> running = WorkspaceRuntime.Running;
        var ids = new HashSet<string>(running.Select(r => r.Id), StringComparer.Ordinal);
        foreach (string id in _followed.Keys.Where(id => !ids.Contains(id)).ToArray()) Unfollow(id);
        foreach (WorkspaceRuntime r in running) if (!_followed.ContainsKey(r.Id)) FollowOne(r);
        _recent.RemoveAll(id => !ids.Contains(id));
        foreach (string id in ids) if (!_recent.Contains(id)) _recent.Add(id);
        Rethink();
    }

    static void FollowOne(WorkspaceRuntime runtime)
    {
        var follow = new Follow(runtime, () => StirFrom(runtime.Id), who => Driven(runtime.Id, who));
        follow.Attach();
        _followed[runtime.Id] = follow;
        if (_pausedAll && runtime.Plane is { Driving: not Driver.Owner } plane)
        {
            plane.OwnerTakes();
            _pausedByUs.Add(runtime.Id);
        }
        // Already held by an agent by the time this module noticed it: the exact start passed
        // unseen, so the honest floor is now - a file from before this moment is not claimed as
        // this run's, which only ever costs a chip that could have shown, never a wrong one.
        if (runtime.Access?.HasDriver == true) _runStarted[runtime.Id] = DateTimeOffset.Now;
    }

    static void Unfollow(string id)
    {
        if (_followed.Remove(id, out Follow? follow)) follow.Detach();
        _recent.Remove(id);
        _runStarted.Remove(id);
        _pausedByUs.Remove(id);
    }

    /// <summary>
    /// Who drives a followed workspace changed. An agent taking it starts a run, the stretch the
    /// result chip reports on; letting go ends it, and then the chip offers what that run made.
    /// </summary>
    static void Driven(string id, Driver who)
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(() => Driven(id, who)); return; }
        if (who != Driver.Agent || !_followed.ContainsKey(id)) return;
        _runStarted[id] = DateTimeOffset.Now;
        if (_resultForId == id) _resultForId = null;
    }

    static void StirFrom(string id)
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(() => StirFrom(id)); return; }
        Touch(id);
        _stirred = DateTimeOffset.Now;
        _dismissed = false;
        Rethink();
    }

    static void Touch(string id) { _recent.Remove(id); _recent.Insert(0, id); }

    /// <summary>Changes which workspace is on screen, dropping the cached whole frame whenever it
    /// really changes - otherwise the new workspace's first in-use tick would draw its window over
    /// the previous one's stale desktop until the once-a-second background refresh caught up.</summary>
    static void SetFront(string? id)
    {
        if (_frontId != id) { _background = null; _backgroundAt = DateTimeOffset.MinValue; }
        _frontId = id;
    }

    static bool Busy(WorkspaceRuntime r) => r.Access?.HasDriver == true
        || r.Plane?.Driving == Driver.Owner
        || r.Access?.Handoffs.All.Any(request => request.State == "pending") == true;

    static (WorkspaceRuntime? Front, WorkspaceRuntime? Back) PickTwo()
    {
        List<WorkspaceRuntime> ordered = _recent
            .Select(id => _followed.TryGetValue(id, out Follow? f) ? f.Runtime : null)
            .Where(r => r is not null).Select(r => r!).ToList();
        List<WorkspaceRuntime> busy = ordered.Where(Busy).ToList();
        WorkspaceRuntime? front = busy.Count > 0 ? busy[0] : ordered.FirstOrDefault();
        // Never replace the screen under the owner's pointer or mid-drag when another agent acts.
        if (_window is { Hovered: true } or { Manipulating: true }
            && ordered.FirstOrDefault(r => r.Id == _frontId) is { } held) front = held;
        WorkspaceRuntime? back = busy.FirstOrDefault(r => r.Id != front?.Id);
        return (front, back);
    }

    /// <summary>Decides, from the settings and what the workspaces are doing, whether the window is
    /// up, lays it out, and puts it where the owner left it. Everything ends in a call to this.</summary>
    static void Rethink()
    {
        if (!_started || _owner is null) return;
        Announce();
        (WorkspaceRuntime? front, WorkspaceRuntime? back) = PickTwo();
        bool held = _window?.Hovered == true || _window?.Manipulating == true || _pausedAll && front is not null;
        bool busy = front is not null && Busy(front);
        TimeSpan quiet = DateTimeOffset.Now - _stirred;
        TimeSpan fade = TimeSpan.FromSeconds(5);
        // The hub can't answer a desktop request, so a question shows here even while it is open.
        bool asking = front?.Access?.Handoffs.All.Any(request => request.State == "pending") == true;
        bool wanted = WorkspacePeekPolicy.Wanted(_settings.CornerShow, front is not null,
            ModuleEntry.HubShowing && !asking, _dismissed, _summoned, _settings.CornerPinned, held, busy, quiet, fade);
        // A tray summon's own grace period (held or within the fixed fade after the last activity)
        // has ended: stop treating it as summoned, or it would keep forcing ComesAndGoes-like timing
        // on a mode (Off, say) that means something else once a later Settings change picks it up.
        if (_summoned && !held && quiet >= fade) _summoned = false;

        if (front is null || !wanted)
        {
            _window?.HandBackInput();
            _window?.Leave();
            StopBeat();
            SetFront(front?.Id);
            _backId = null;
            StartDropHookIfIdle();
            return;
        }

        StopDropHook();
        SetFront(front.Id);
        _backId = back?.Id;

        WorkspacePeekWindow window = _window ??= Build();
        Size card = WorkspacePeekPlacement.Card(_settings);
        bool grown = card.Width > WorkspacePeekPlacement.SmallWidth + 0.5;
        if (grown) _lastGrownWidth = card.Width;
        if (!window.Manipulating)
        {
            window.Configure(card, back is not null, grown, !grown && _lastGrownWidth is not null);
            // Saved positions describe the front card, not the union with a temporary back card.
            Rect frontRect = PlaceRect(card);
            double extra = window.VisibleSize.Height - card.Height;
            window.Place(new Rect(frontRect.Left, frontRect.Top - extra, card.Width, window.VisibleSize.Height));
        }
        window.SetPinned(_settings.CornerPinned);

        (string message, PeekTone tone) = Status(front);
        window.Describe(WorkspaceName(front), message, tone);
        window.SetActive(front.Access?.HasDriver == true);
        if (back is not null)
        {
            (string backMessage, PeekTone backTone) = Status(back);
            window.DescribeBack(WorkspaceName(back), backMessage, backTone);
            window.ShowBackFrame(back.Plane?.Glance(TimeSpan.FromSeconds(1)));
        }

        UpdateResultChip(window, front);
        UpdateNeedsYou(window, front);
        UpdateToast(window, front);

        window.Arrive();
        StartBeat();
        Draw();
    }

    static WorkspacePeekWindow Build()
    {
        var window = new WorkspacePeekWindow();
        window.BindInput(() => _frontId is { } id && _followed.TryGetValue(id, out Follow? f) ? f.Runtime : null);
        window.OwnerActed += () => { _stirred = DateTimeOffset.Now; _dismissed = false; };
        window.OpenRequested += () => OpenWorkspace(_frontId);
        window.PinClicked += () => AppSettingsStore.Update(s => s with { CornerPinned = !s.CornerPinned });
        window.ShrinkClicked += () =>
        {
            if (WorkspacePeekPlacement.Grown(_settings))
                AppSettingsStore.Update(s => s with { CornerWidth = null });
            else if (_lastGrownWidth is { } width)
                AppSettingsStore.Update(s => s with { CornerWidth = width });
        };
        window.HideRequested += () => { _dismissed = true; AppSettingsStore.Update(s => s with { CornerPinned = false }); };
        window.HoverChanged += Rethink;
        window.PromoteRequested += () => { if (_backId is { } id) Touch(id); Rethink(); };
        window.Moved += rect => AppSettingsStore.Update(s => s with { CornerLeft = rect.Left, CornerTop = rect.Top });
        window.Resized += rect => AppSettingsStore.Update(s => s with
            { CornerWidth = rect.Width, CornerLeft = rect.Left, CornerTop = rect.Top });
        window.FilesDropped += DropFiles;
        window.SheetOpenClicked += () => Decide(true);
        window.SheetKeepClicked += () => Decide(false);
        window.ResumeClicked += ModuleEntry.RequestPauseAll;
        window.ChipOpenRequested += OpenResult;
        return window;
    }

    /// <summary>The owner wants the whole workspace, not a glance at it.</summary>
    static void OpenWorkspace(string? id)
    {
        if (id is { } known && _followed.TryGetValue(known, out Follow? f)) ModuleEntry.Selected = f.Runtime.Id;
        ModuleEntry.RequestDashboardOpen();
        if (Application.Current?.MainWindow is not { } shell) return;
        if (shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal;
        shell.Show();
        shell.Activate();
    }

    /// <summary>Copies dropped files into the front workspace's folder, never moving the source.
    /// Internal rather than private so the gate can exercise it without a real OS drag.</summary>
    internal static void DropFiles(string[] paths)
    {
        if (_frontId is not { } id || !_followed.TryGetValue(id, out Follow? f) || f.Runtime.Plane is not { } plane) return;
        string folder = plane.Folder;
        if (folder.Length == 0) return;
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Copy(path, UniqueDestination(folder, Path.GetFileName(path)), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static string UniqueDestination(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    internal static Action<string>? OpenResultForTests;

    static void OpenResult(string path)
    {
        if (!File.Exists(path)) return;
        if (OpenResultForTests is { } observe) { observe(path); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // The file may have gone away or its default app may be unavailable.
        }
    }

    static void Decide(bool approve)
    {
        if (_frontId is { } id && _followed.TryGetValue(id, out Follow? f) && f.Runtime.Access is { } access
            && _pendingId is { } requestId)
            access.Handoffs.Decide(requestId, approve);
        Rethink();
    }

    static void UpdateResultChip(WorkspacePeekWindow window, WorkspaceRuntime front)
    {
        // A run is over when the agent that took the workspace let go of it.
        if (front.Access is not { HasDriver: false } || !_runStarted.TryGetValue(front.Id, out DateTimeOffset since))
        {
            _resultForId = null; _resultName = null; _resultPath = null;
        }
        else if (_resultForId != front.Id)
        {
            _resultForId = front.Id;
            (_resultName, _resultPath) = NewestFile(front.Plane?.Folder, since);
        }
        window.ShowResultChip(_resultName, _resultPath);
    }

    /// <summary>The newest file in the folder that this run itself could have made - created or
    /// written at or after <paramref name="since"/>. A file already sitting there before the run
    /// started is not this run's result, whatever its name; with nothing that qualifies there is no
    /// chip rather than a guess.</summary>
    static (string? Name, string? Path) NewestFile(string? folder, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return (null, null);
        try
        {
            FileInfo? newest = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.CreationTimeUtc >= since.UtcDateTime || f.LastWriteTimeUtc >= since.UtcDateTime)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? (null, null) : (newest.Name, newest.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    static void UpdateNeedsYou(WorkspacePeekWindow window, WorkspaceRuntime front)
    {
        IReadOnlyList<WorkspaceHandoff> all = front.Access?.Handoffs.All ?? [];
        WorkspaceHandoff? pending = all.FirstOrDefault(request => request.State == "pending" && request.Id == _askFirst)
            ?? all.FirstOrDefault(request => request.State == "pending");
        _pendingId = pending?.Id;
        window.ShowNeedsYou(pending is null ? null : Question(pending));
    }

    static readonly HashSet<string> _announced = [];

    /// <summary>
    /// MVP_SPEC, Alerts: a Windows notification only when an agent needs the owner and this window
    /// can't show it - turned off in Settings, or a full-screen app in front. Once per request; the
    /// hub shows its own. Windows' Do not disturb and sound settings apply to the notification.
    /// </summary>
    static void Announce()
    {
        if (_settings.CornerShow != CornerShow.Off && !FullScreenInFront()) return;
        foreach (WorkspaceRuntime r in WorkspaceRuntime.Running)
            foreach (WorkspaceHandoff request in r.Access?.Handoffs.All ?? [])
                if (request.State == "pending" && _announced.Add(request.Id))
                    ModuleEntry.RequestAttention(AgentName(r) + " wants you", Question(request), r.Id, request.Id);
    }

    static bool FullScreenInFront() =>
        SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;   // busy, D3D full screen, presentation

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    static string Question(WorkspaceHandoff request) =>
        "Open " + (request.Kind == "file" ? Path.GetFileName(request.Target) : request.Target) + " on your desktop?";

    static void UpdateToast(WorkspacePeekWindow window, WorkspaceRuntime front)
    {
        if (_pausedAll) window.ShowPausedToast();
        else if (front.Plane?.Driving == Driver.Owner) window.ShowUsingToast(ShortAgentName(front));
        else window.HideToast();
    }

    static (string Message, PeekTone Tone) Status(WorkspaceRuntime r)
    {
        if (r.Access?.Handoffs.All.Any(request => request.State == "pending") == true)
            return (AgentName(r) + " wants you", PeekTone.Attention);
        return (AgentName(r), r.Access?.HasDriver == true ? PeekTone.Working : PeekTone.Quiet);
    }

    static string WorkspaceName(WorkspaceRuntime r) => WorkspaceStore.Find(r.Id)?.Name ?? "Workspace";

    /// <summary>What an external agent calls itself, as the reference always shows it.</summary>
    static string AgentName(WorkspaceRuntime r) =>
        r.Access is { LastController.Length: > 0 } access ? WorkspaceHome.DisplayName(access.LastController) : "Agent";

    static string ShortAgentName(WorkspaceRuntime r)
    {
        string name = AgentName(r);
        return name == "Claude Code" ? "Claude" : name;
    }

    /// <summary>Whether the owner's own use, or the size, makes the front-window pipeline worth its
    /// extra cost over the plain whole-screen one.</summary>
    static bool InUse(WorkspaceRuntime front) => _window?.Hovered == true
        || WorkspacePeekPlacement.Grown(_settings) || front.Plane?.Driving == Driver.Owner;

    static PreviewSmoothness Rate => WorkspacePeekCapture.OnBattery()
        ? PreviewSmoothness.BatterySaver : PreviewSmoothness.Balanced;
    static TimeSpan IdleInterval => IdleIntervalFor(Rate);
    static TimeSpan InUseInterval => InUseIntervalFor(Rate);

    /// <summary>How often idle draws while on screen and not in use, by Preview smoothness. Public
    /// to the engine probe too, so it measures the same rate this host actually paces itself to.</summary>
    internal static TimeSpan IdleIntervalFor(PreviewSmoothness smoothness) => smoothness switch
    {
        PreviewSmoothness.Smooth => TimeSpan.FromSeconds(1 / 5.0),
        PreviewSmoothness.BatterySaver => TimeSpan.FromSeconds(1.0),
        _ => TimeSpan.FromSeconds(1 / 2.5),
    };

    internal static TimeSpan InUseIntervalFor(PreviewSmoothness smoothness) => smoothness switch
    {
        PreviewSmoothness.Smooth => TimeSpan.FromSeconds(1 / 15.0),
        PreviewSmoothness.BatterySaver => TimeSpan.FromSeconds(1 / 8.0),
        _ => TimeSpan.FromSeconds(1 / 12.0),
    };

    static void StartBeat()
    {
        if (_beat is not null || _owner is null) return;
        _beat = new DispatcherTimer(DispatcherPriority.Background, _owner) { Interval = IdleInterval };
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

    // The only timer this feature has, and it runs only while the window is actually on screen. It
    // both re-checks the fade and drives the picture, at whatever rate is current for the moment.
    static void Tick(object? sender, EventArgs e)
    {
        Rethink();
        if (_beat is { } timer && _frontId is { } id && _followed.TryGetValue(id, out Follow? f))
            timer.Interval = InUse(f.Runtime) ? InUseInterval : IdleInterval;
        Draw();
    }

    static void Draw()
    {
        if (_drawing || _owner is not { } owner || _window is not { Watching: true } window) return;
        if (_frontId is not { } id || !_followed.TryGetValue(id, out Follow? f) || f.Runtime.Computer is not { } desktop) return;
        bool inUse = InUse(f.Runtime);
        if (!inUse && WorkspacePeekCapture.OnBattery()) return;
        _drawing = true;
        string capturedFor = id;
        Task.Run(() =>
        {
            // Any capture failure - the desktop tearing down mid-shot, a window closing between the
            // list and the print, anything Windows hands back - costs this one tick, never the UI
            // dispatcher: there is always a next tick.
            try { return WorkspacePeekCapture.Take(desktop, ref _background, ref _backgroundAt, inUse); }
            catch (Exception) { return default; }
        }).ContinueWith(taken => owner.BeginInvoke(() =>
        {
            _drawing = false;
            WorkspacePeekCapture.Frame frame = taken.Result;
            if (frame.Background is null || capturedFor != _frontId) return;
            if (_window is not { Watching: true } current) return;
            current.ShowFrame(frame.Background);
            if (frame.Patch is { } patch) current.ShowFramePatch(patch, frame.PatchX, frame.PatchY);
            else current.HideFramePatch();
        }), TaskScheduler.Default);
    }

    static void StartDropHookIfIdle()
    {
        if (_dropHook is not null || !_started) return;
        if (_settings.CornerShow == CornerShow.Off) return;
        if (_window is { Watching: true }) return;
        if (PickTwo().Front is null) return;
        _dropHook = new WorkspacePeekDropHook(TargetScreenRect, Summon);
    }

    static void StopDropHook()
    {
        _dropHook?.Dispose();
        _dropHook = null;
    }

    static Rect TargetScreenRect()
    {
        Size card = WorkspacePeekPlacement.Card(_settings);
        Rect dip = PlaceRect(card);
        // Whichever monitor the corner window actually lands on - not always the primary one, so a
        // drop target on a mixed-DPI secondary monitor lines up with where the card really is.
        double scale = WorkspacePeekPlacement.MonitorFor(dip).Scale;
        return new Rect(dip.Left * scale, dip.Top * scale, dip.Width * scale, dip.Height * scale);
    }

    static void Summon()
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(Summon); return; }
        _summoned = true;
        _dismissed = false;
        _stirred = DateTimeOffset.Now;
        Rethink();
        _summoned = false;
    }

    static Rect PlaceRect(Size total)
    {
        if (_settings.CornerLeft is { } left && _settings.CornerTop is { } top)
        {
            Rect saved = new(left, top, total.Width, total.Height);
            if (WorkspacePeekPlacement.OnConnectedMonitor(saved)) return saved;
        }
        return WorkspacePeekPlacement.Corner(WorkspacePeekPlacement.HomeWorkArea(), total);
    }

    static void HoldHotkeys()
    {
        _pauseKey ??= MakeHotkey(ModuleEntry.RequestPauseAll);
        // A probe without a message loop cannot register; that is not a shortcut collision.
        bool held = _pauseKey is null;
        string? chosen = null;
        foreach (string candidate in new[] { _settings.PauseHotkey, "Ctrl+Alt+Shift+P",
            "Ctrl+Alt+F12", "Ctrl+Alt+Shift+F12" }.Distinct())
        {
            if (string.IsNullOrWhiteSpace(candidate) || _pauseKey is null || !_pauseKey.Hold(candidate)) continue;
            held = true;
            chosen = candidate;
            break;
        }
        ModuleEntry.ReportPauseShortcut(!held);
        if (chosen is not null && chosen != _settings.PauseHotkey)
            AppSettingsStore.Update(s => s with { PauseHotkey = chosen });
    }

    static WorkspacePeekHotkey? MakeHotkey(Action pressed)
    {
        // A host with no message loop of its own - a probe, a test - simply has no hotkey.
        try
        {
            var key = new WorkspacePeekHotkey();
            key.Pressed += pressed;
            return key;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Shows the corner view, whatever the mode - Off included, where the key is the only way in.
    /// Never touches CornerPinned: pressing this is a glance, not a change to what Settings says to
    /// do on its own. It stays up for the normal fade period after this (or while hovered) through
    /// the same <see cref="_summoned"/> decay Rethink already does for a drop-hook summon - not just
    /// until the next timer tick, which is what toggling a setting and un-summoning right away used
    /// to leave it doing. With nothing running there is no picture to show, so it opens the app.
    /// </summary>
    /// <summary>A notification was clicked: the corner comes up on that workspace, with that question.</summary>
    internal static void ShowFor(string id, string request)
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(() => ShowFor(id, request)); return; }
        if (_followed.ContainsKey(id)) Touch(id);
        _askFirst = request;
        ShowCornerNow();
    }

    /// <summary>The question a clicked notification asked, shown before any other pending one.</summary>
    static string? _askFirst;

    static void ShowCornerNow()
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(ShowCornerNow); return; }
        if (!WorkspaceRuntime.AnyRunning) { OpenWorkspace(null); return; }
        _summoned = true;
        _dismissed = false;
        _stirred = DateTimeOffset.Now;
        Rethink();
    }

    /// <summary>Every running workspace waits as if the owner had taken it; pressing it again hands
    /// back only the ones Pause itself took, never a workspace the owner already held before he
    /// pressed it.</summary>
    static void TogglePauseAll()
    {
        if (_owner is not { } owner) return;
        if (!owner.CheckAccess()) { owner.BeginInvoke(TogglePauseAll); return; }
        _pausedAll = !_pausedAll;
        if (_pausedAll)
        {
            _pausedByUs.Clear();
            foreach (WorkspaceRuntime r in WorkspaceRuntime.Running)
            {
                if (r.Plane is not { } plane || plane.Driving == Driver.Owner) continue;
                plane.OwnerTakes();
                _pausedByUs.Add(r.Id);
            }
        }
        else
        {
            foreach (WorkspaceRuntime r in WorkspaceRuntime.Running)
                if (_pausedByUs.Contains(r.Id) && r.Plane is { Driving: Driver.Owner } plane) plane.Release();
            _pausedByUs.Clear();
        }
        ModuleEntry.AllPaused = _pausedAll;
        Rethink();
    }

    /// <summary>One followed workspace's event subscriptions, kept together so unfollowing removes
    /// exactly the delegates that were added.</summary>
    sealed class Follow(WorkspaceRuntime runtime, Action stir, Action<Driver> driven)
    {
        internal readonly WorkspaceRuntime Runtime = runtime;
        void Drove(Driver who)
        {
            driven(who);
            if (_pausedAll && who != Driver.Owner && Runtime.Plane is { } plane)
            {
                _pausedByUs.Add(Runtime.Id);
                plane.OwnerTakes();
            }
            stir();
        }
        void Ended() => stir();
        void HandoffsChanged() => stir();

        internal void Attach()
        {
            Runtime.DriverChanged += Drove;
            Runtime.Ended += Ended;
            if (Runtime.Access is { } access) access.Handoffs.Changed += HandoffsChanged;
        }

        internal void Detach()
        {
            Runtime.DriverChanged -= Drove;
            Runtime.Ended -= Ended;
            if (Runtime.Access is { } access) access.Handoffs.Changed -= HandoffsChanged;
        }
    }
}
