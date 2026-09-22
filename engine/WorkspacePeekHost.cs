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
    static DispatcherTimer? _presentationWatch;
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

    // The workspace the owner picked from the tabs. It wins over whatever is busiest for as long as
    // this look lasts; once the corner goes away, the next one leads with what is working again.
    static string? _chosenId;

    static string? _frontId, _resultForId, _resultName, _resultPath, _pendingId;
    static int _seq;

    /// <summary>The workspace a pinned corner window is showing, which must not be put to sleep
    /// under it. Null when the corner is not pinned or shows nothing.</summary>
    internal static string? PinnedOn => _started && _settings.CornerPinned ? _frontId : null;
    static DateTimeOffset _stirred = DateTimeOffset.MinValue;
    // The last whole-screen picture, tagged with the workspace it was taken on. Only ever written
    // here on the UI thread, from what the capture hands back; the tag, not the timing of this
    // assignment, is what keeps it from being paired with another workspace's window.
    static WorkspacePeekCapture.Cached? _background;
    static bool _started, _dismissed, _summoned, _drawing, _hooked, _pausedAll;
    static bool _explicitSummon;
    static bool _lastPresentation;

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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        _pausedAll = false;
        foreach (WorkspaceRuntime runtime in WorkspaceRuntime.Running)
            if (_pausedByUs.Contains(runtime.Id) && runtime.Plane is { Driving: Driver.Owner } plane)
                plane.Release();
        foreach (string id in _followed.Keys.ToArray()) Unfollow(id);
        _recent.Clear();
        _pausedByUs.Clear();
        StopBeat();
        _presentationWatch?.Stop();
        _presentationWatch = null;
        StopDropHook();
        _pauseKey?.Dispose(); _pauseKey = null;
        _window?.HandBackInput();
        WorkspacePeekWindow? window = _window;
        _window = null;
        window?.Close();
        _dismissed = _summoned = _pausedAll = false;
        _explicitSummon = false;
        _lastPresentation = false;
        _announced.Clear();
        ModuleEntry.AllPaused = false;
        _frontId = _chosenId = null;
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

    static void DisplayChanged(object? sender, EventArgs e)
    {
        _owner?.BeginInvoke(() =>
        {
            // A detached monitor must not strand the only way back to a quiet workspace.
            _window?.HideImmediately();
            Rethink();
        });
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
        WatchPresentation();
        Rethink();
    }

    // No desktop captures here. Keep watching while hidden so an existing corner disappears when
    // fullscreen starts and its normal policy is reconsidered when fullscreen ends.
    static void WatchPresentation()
    {
        if (_followed.Count == 0)
        {
            _presentationWatch?.Stop();
            _presentationWatch = null;
            return;
        }
        if (_presentationWatch is not null || _owner is null) return;
        _presentationWatch = new DispatcherTimer(DispatcherPriority.Background, _owner)
            { Interval = TimeSpan.FromMilliseconds(500) };
        _presentationWatch.Tick += (_, _) =>
        {
            bool presentation = WorkspacePresentation.Suppressed;
            if (presentation == _lastPresentation) return;
            _lastPresentation = presentation;
            Rethink();
        };
        _presentationWatch.Start();
    }

    static void FollowOne(WorkspaceRuntime runtime)
    {
        var follow = new Follow(runtime, () => StirFrom(runtime.Id), who => Driven(runtime.Id, who)) { Seq = _seq++ };
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

    /// <summary>Changes which workspace is on screen, letting go of the cached whole frame whenever
    /// it really changes so the old screen is not held in memory. A tick that lands late cannot use
    /// it either way: a background carries the workspace it was taken on and is only reused there.</summary>
    static void SetFront(string? id)
    {
        if (_frontId != id) _background = null;
        _frontId = id;
    }

    static bool Busy(WorkspaceRuntime r) => r.Access?.HasDriver == true
        || r.Plane?.Driving == Driver.Owner
        || r.Access?.Handoffs.All.Any(request => request.State == "pending") == true;

    /// <summary>Every running workspace, in the order they started - one tab each - and which of them
    /// the card shows: what the owner picked, else the busiest, else the most recent.</summary>
    static (WorkspaceRuntime? Front, IReadOnlyList<WorkspaceRuntime> All) Pick()
    {
        List<WorkspaceRuntime> all = _followed.Values.OrderBy(f => f.Seq).Select(f => f.Runtime).ToList();
        List<WorkspaceRuntime> ordered = _recent
            .Select(id => _followed.TryGetValue(id, out Follow? f) ? f.Runtime : null)
            .Where(r => r is not null).Select(r => r!).ToList();
        WorkspaceRuntime? front = all.FirstOrDefault(r => r.Id == _chosenId)
            ?? ordered.FirstOrDefault(Busy) ?? ordered.FirstOrDefault();
        // Never replace the screen under the owner's pointer or mid-drag when another agent acts.
        if (_window is { Hovered: true } or { Manipulating: true }
            && ordered.FirstOrDefault(r => r.Id == _frontId) is { } held) front = held;
        return (front, all);
    }

    /// <summary>Decides, from the settings and what the workspaces are doing, whether the window is
    /// up, lays it out, and puts it where the owner left it. Everything ends in a call to this.</summary>
    static void Rethink()
    {
        if (!_started || _owner is null) return;
        bool presentation = WorkspacePresentation.Suppressed;
        _lastPresentation = presentation;
        Announce(presentation);
        (WorkspaceRuntime? front, IReadOnlyList<WorkspaceRuntime> all) = Pick();
        bool held = _window?.Hovered == true || _window?.Manipulating == true || _pausedAll && front is not null;
        bool busy = front is not null && Busy(front);
        TimeSpan quiet = DateTimeOffset.Now - _stirred;
        TimeSpan fade = TimeSpan.FromSeconds(5);
        // The hub can't answer a desktop request, so a question shows here even while it is open.
        bool asking = front?.Access?.Handoffs.All.Any(request => request.State == "pending") == true;
        bool wanted = WorkspacePeekPolicy.Wanted(_settings.CornerShow, front is not null,
            ModuleEntry.HubShowing && !asking, _dismissed, _summoned, _settings.CornerPinned, held, busy, quiet, fade,
            presentation, _summoned && _explicitSummon);
        // A tray summon's own grace period (held or within the fixed fade after the last activity)
        // has ended: stop treating it as summoned, or it would keep forcing ComesAndGoes-like timing
        // on a mode (Off, say) that means something else once a later Settings change picks it up.
        if (_summoned && !held && quiet >= fade) _summoned = _explicitSummon = false;

        if (presentation && !(_summoned && _explicitSummon))
        {
            _window?.HandBackInput();
            _window?.HideImmediately();
            StopBeat();
            StopDropHook();
            SetFront(front?.Id);
            return;
        }

        if (front is null || !wanted)
        {
            _window?.HandBackInput();
            bool dock = front is not null && _settings.CornerShow != CornerShow.Off
                && (!ModuleEntry.HubShowing || asking);
            if (dock && _window is { } tucked)
            {
                Layout(tucked, front!, all);
                tucked.Dock(WorkspaceName(front!));
            }
            else _window?.Leave();
            StopBeat();
            SetFront(front?.Id);
            // Tucking keeps the owner's chosen workspace available from the tab. A full hide ends
            // that choice, so the next new glance can lead with whichever workspace is working.
            if (!dock) _chosenId = null;
            StartDropHookIfIdle();
            return;
        }

        StopDropHook();
        SetFront(front.Id);

        WorkspacePeekWindow window = _window ??= Build();
        Layout(window, front, all);
        window.SetPinned(_settings.CornerPinned);

        (string message, PeekTone tone) = Status(front);
        window.Describe(WorkspaceName(front), message, tone);
        window.SetActive(front.Access?.HasDriver == true);

        UpdateResultChip(window, front);
        UpdateNeedsYou(window, front);
        UpdateToast(window, front);

        window.Arrive();
        StartBeat();
        Draw();
    }

    static void Layout(WorkspacePeekWindow window, WorkspaceRuntime front, IReadOnlyList<WorkspaceRuntime> all)
    {
        Size card = WorkspacePeekPlacement.Card(_settings);
        bool grown = card.Width > WorkspacePeekPlacement.SmallWidth + 0.5;
        if (grown) _lastGrownWidth = card.Width;
        if (!window.Manipulating && !window.Sliding)
        {
            window.SetTabs([.. all.Select(r => new PeekTab(r.Id, WorkspaceName(r), Status(r).Tone, r.Id == front.Id))]);
            window.Configure(card, grown, !grown && _lastGrownWidth is not null);
            // Saved positions describe the front card, not the union with the tab strip above it -
            // so the box that has to fit on the monitor is the one the strip is part of, or a card
            // saved at the top of the screen would draw its tabs off the top edge.
            Rect frontRect = PlaceRect(card);
            double extra = window.VisibleSize.Height - card.Height;
            Rect visible = new(frontRect.Left, frontRect.Top - extra, card.Width, window.VisibleSize.Height);
            window.Place(WorkspacePeekPlacement.Fit(WorkspacePeekPlacement.MonitorFor(visible).WorkArea, visible));
        }
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
            bool grown = WorkspacePeekPlacement.Grown(_settings);
            if (!grown && _lastGrownWidth is null) return;
            double? width = grown ? null : _lastGrownWidth;
            Rect from = window.FrontRect;
            Rect to = WorkspacePeekPlacement.Regrow(from, WorkspacePeekPlacement.Card(width ?? WorkspacePeekPlacement.SmallWidth),
                WorkspacePeekPlacement.MonitorFor(from).WorkArea);
            // A card never placed by hand keeps following the home corner; one placed by hand keeps
            // the corner of the screen it sits nearest.
            AppSettingsStore.Update(s => s with
            {
                CornerWidth = width,
                CornerLeft = s.CornerLeft is null ? null : to.Left,
                CornerTop = s.CornerTop is null ? null : to.Top,
            });
        };
        window.HideRequested += () => { _dismissed = true; AppSettingsStore.Update(s => s with { CornerPinned = false }); };
        window.HoverChanged += () =>
        {
            if (window.Watching) _stirred = DateTimeOffset.Now;
            Rethink();
        };
        window.EdgeRevealRequested += () =>
        {
            _chosenId = _frontId;
            _dismissed = false;
            _stirred = DateTimeOffset.Now;
            Rethink();
        };
        window.ShowRequested += id => { _chosenId = id; Touch(id); Rethink(); };
        // Putting the card somewhere, or making it a size, is choosing to keep it: either pins it, so
        // it stays that size in that place rather than fading or tucking itself away at the edge.
        window.Moved += rect => AppSettingsStore.Update(s => s with
            { CornerLeft = rect.Left, CornerTop = rect.Top, CornerPinned = true });
        window.Resized += rect => AppSettingsStore.Update(s => s with
            { CornerWidth = rect.Width, CornerLeft = rect.Left, CornerTop = rect.Top, CornerPinned = true });
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
        if (shell.ShowActivated) shell.Activate();
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
        // Keep looking while this run has nothing to offer. The agent lets go and its last write
        // lands a moment later, and picking once - the first tick after it let go - meant that file
        // was never offered at all. Once there is something there is nothing left to look for.
        else if (_resultForId != front.Id || _resultPath is null)
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
    static void Announce(bool presentation)
    {
        if (presentation || _settings.CornerShow != CornerShow.Off) return;
        foreach (WorkspaceRuntime r in WorkspaceRuntime.Running)
            foreach (WorkspaceHandoff request in r.Access?.Handoffs.All ?? [])
                if (request.State == "pending" && _announced.Add(request.Id))
                    ModuleEntry.RequestAttention(AgentName(r) + " wants you", Question(request), r.Id, request.Id);
    }

    /// <summary>The one line the owner answers. A takeover runs the other way round from every other
    /// request - it closes his own copy of a program and starts it in the workspace - so it gets its
    /// own sentence rather than a target worded to survive somebody else's.</summary>
    static string Question(WorkspaceHandoff request) => request.Kind switch
    {
        "takeover" => "Close your copy of " + request.Target + " and start it in the workspace?",
        "file" => "Open " + Path.GetFileName(request.Target) + " on your desktop?",
        _ => "Open " + request.Target + " on your desktop?",
    };

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

    /// <summary>The pace to draw at right now. Battery slows it, on both counts, but never stops it:
    /// most Windows machines are laptops, and a corner view frozen on its last frame is the feature
    /// not working. Read fresh every tick, so plugging in speeds it back up and unplugging slows it
    /// down without a restart.</summary>
    static PreviewSmoothness Rate => WorkspacePeekCapture.OnBattery()
        ? PreviewSmoothness.BatterySaver : PreviewSmoothness.Balanced;
    static TimeSpan IdleInterval => IdleIntervalFor(Rate);
    static TimeSpan InUseInterval => InUseIntervalFor(Rate);

    /// <summary>How often idle draws while on screen and not in use, by Preview smoothness. Public
    /// to the engine probe too, so it measures the same rate this host actually paces itself to.</summary>
    internal static TimeSpan IdleIntervalFor(PreviewSmoothness smoothness) => smoothness switch
    {
        PreviewSmoothness.BatterySaver => TimeSpan.FromSeconds(1.0),
        _ => TimeSpan.FromSeconds(1 / 2.5),
    };

    internal static TimeSpan InUseIntervalFor(PreviewSmoothness smoothness) => smoothness switch
    {
        PreviewSmoothness.BatterySaver => TimeSpan.FromSeconds(1 / 8.0),
        _ => TimeSpan.FromSeconds(1 / 12.0),
    };

    /// <summary>The hub's pace for a screen the owner has a hand on (the app's HubPreview.HandsOnInterval),
    /// kept here beside the corner's so the engine probe measures the rate the hub really asks for.</summary>
    internal static TimeSpan HandsOnIntervalFor(PreviewSmoothness smoothness) => smoothness switch
    {
        PreviewSmoothness.BatterySaver => TimeSpan.FromSeconds(1 / 10.0),
        _ => TimeSpan.FromSeconds(1 / 20.0),
    };

    /// <summary>Plugged in or not, read fresh: the hub's hands-on pace right now.</summary>
    public static TimeSpan HandsOnInterval => HandsOnIntervalFor(Rate);

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
        _drawing = true;
        string capturedFor = id;
        WorkspacePeekCapture.Cached? cached = _background;
        Task.Run(() =>
        {
            // Any capture failure - the desktop tearing down mid-shot, a window closing between the
            // list and the print, anything Windows hands back - costs this one tick, never the UI
            // dispatcher: there is always a next tick.
            try { return WorkspacePeekCapture.Take(desktop, capturedFor, cached, inUse); }
            catch (Exception) { return default; }
        }).ContinueWith(taken => owner.BeginInvoke(() =>
        {
            _drawing = false;
            WorkspacePeekCapture.Frame frame = taken.Result;
            if (frame.Background is null || capturedFor != _frontId) return;
            _background = frame.Cache;
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
        if (Pick().Front is null) return;
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
        if (WorkspacePresentation.Suppressed) return;
        _summoned = true;
        _explicitSummon = false;
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
        _explicitSummon = true;
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

        /// <summary>When this workspace was first followed, so the tabs keep the order they started
        /// in - a tab that moved every time another agent did something would be no use at all.</summary>
        internal int Seq { get; init; }

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
