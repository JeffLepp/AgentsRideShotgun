using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

internal sealed class PillInfo(string text, bool working) { public string Text => text; public bool Working => working; }

/// <summary>
/// A workspace in full: header, the live screen, "What it did" and "Files". Its own
/// preview loop picks its own rate like the stack's working cards.
/// </summary>
public partial class WorkspaceFullView : UserControl, IDisposable
{
    readonly ObservableCollection<PillInfo> _pills = [];
    readonly ObservableCollection<DidRow> _did = [];
    readonly ObservableCollection<FileRow> _files = [];
    string? _id;
    WorkspaceScreenInput? _input;
    DispatcherTimer? _screenTimer;
    bool _fixture;
    bool _renaming;
    bool _disposed;
    bool _capturingScreen;
    bool _readingMemory;
    int _memoryGeneration;
    WorkspaceRuntime? _memoryRuntime;
    /// <summary>A live frame has been on screen for this workspace, so the stored one is past.</summary>
    bool _liveShown;
    /// <summary>The screen the picture on show came from. Input goes only to that screen, so a
    /// workspace that restarted never takes clicks aimed at its old picture.</summary>
    WorkspaceControl? _shownPlane;
    /// <summary>What "What it did" and "Files" were last read from.</summary>
    (long, DateTime) _didSeen, _filesSeen;
    /// <summary>Whichever runtime "You have control" is currently listening to, so the toast still
    /// clears if this workspace slept and woke again under a new WorkspaceRuntime while the page
    /// was open. Re-synced every RefreshLive tick rather than fixed once in SetWorkspace.</summary>
    WorkspaceRuntime? _driverRuntime;
    /// <summary>The owner has not scrolled the activity lists away from the newest row, so a reload
    /// should keep following it. Starts true: opening the page lands on the newest row.</summary>
    bool _followTail = true;

    public WorkspaceFullView()
    {
        InitializeComponent();
        HerePanel.ItemsSource = _pills;
        DidList.ItemsSource = _did;
        FilesList.ItemsSource = _files;
        // Named after WorkspaceScreenInput's own constants rather than a copy of
        // the numbers, so a future change to LeaveDelay or StayDelay reaches this wording for free.
        ControlToastText.Text = "You have control. The agent waits, and resumes "
            + (int)WorkspaceScreenInput.LeaveDelay.TotalSeconds + "s after you leave, or "
            + (int)WorkspaceScreenInput.StayDelay.TotalSeconds + "s after you stop.";
        // Hidden means the wide window isn't showing this page (stack mode, or another workspace
        // selected): the timer keeps existing but does no work until it is visible again.
        IsVisibleChanged += (_, _) =>
        {
            if (_screenTimer is null) return;
            if (IsVisible) { _screenTimer.Start(); ScreenTick(null, EventArgs.Empty); }
            else _screenTimer.Stop();
        };
        Unloaded += (_, _) => StopLive();
    }

    /// <summary>The workspace was renamed from this page: id, new name.</summary>
    public event Action<string, string>? Renamed;
    /// <summary>The workspace was deleted from this page.</summary>
    public event Action<string>? Deleted;

    public string? WorkspaceId => _id;

    public void SetWorkspace(string id)
    {
        StopLive();
        _fixture = false;
        _id = id;
        Reload();
        // Clicking the screen takes over right there, the same as the corner window.
        _input = new WorkspaceScreenInput(ScreenImage, () =>
            WorkspaceRuntime.Of(_id ?? "") is { Plane: { } plane } runtime && ReferenceEquals(plane, _shownPlane) ? runtime : null);
        _input.OwnerActed += OwnerActed;
        // Full desktop: the agent's screen gets its taskbar here too, under the Last seen pill.
        _taskbar = new WorkspaceTaskbar(() => _id is { } shown ? WorkspaceRuntime.Of(shown) : null, () => _input?.Touch(), 40)
        {
            Visibility = Visibility.Collapsed,
        };
        ScreenLayers.Children.Insert(1, _taskbar);
        StartScreenTimer();
    }

    WorkspaceTaskbar? _taskbar;

    /// <summary>The strip, for the UI probe.</summary>
    internal WorkspaceTaskbar? Taskbar => _taskbar;

    /// <summary>The live picture, for the UI probe, which clicks it the way the owner does.</summary>
    internal Image ScreenPicture => ScreenImage;

    /// <summary>The owner's hand on that picture, for the UI probe.</summary>
    internal WorkspaceScreenInput? ScreenInput => _input;

    void Reload()
    {
        if (_id is not { } id || WorkspaceStore.Find(id) is not { } workspace) return;
        HeaderError.Visibility = Visibility.Collapsed;
        NameText.Text = workspace.Name;
        RenameBox.Text = workspace.Name;
        const string folderPrefix = "folder:";
        PathText.Text = WorkspaceHome.IsFolder(workspace.Agents) ? workspace.Agents[folderPrefix.Length..] : WorkspaceStore.FolderOf(id);
        ScreenImage.Source = null;
        _liveShown = false;
        _shownPlane = null;
        ShowStoredFrame(id);
        RefreshLive();
        LoadDid(id);
        LoadFiles(id);
    }

    /// <summary>Cheap, frequent: the "who is here" pills and a fresh frame. Called by the timer and
    /// after a theme change, since the resources code caches here go stale otherwise.</summary>
    internal void RefreshLive()
    {
        if (_fixture || _id is not { } id) return;
        WorkspaceRuntime? runtime = WorkspaceRuntime.Of(id);
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        string driver = runtime?.Access?.Controller ?? "";
        _pills.Clear();
        if (driver.Length > 0) _pills.Add(new PillInfo(WorkspaceHome.DisplayName(driver), true));
        else if (workspace is not null && !WorkspaceHome.IsFolder(workspace.Agents) && WorkspaceHome.Label(workspace.Agents) is { Length: > 0 } kept)
            _pills.Add(new PillInfo(kept, false));
        // The Sleep control and the one honest memory line both only mean
        // something while this workspace actually has a computer running.
        SleepButton.Visibility = runtime is not null ? Visibility.Visible : Visibility.Collapsed;
        RefreshMemory(runtime);
        BrowserStatusText.Text = runtime?.Plane?.BrowserStatus ?? "";
        BrowserStatusText.Visibility = BrowserStatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDriverSubscription(runtime);
        UpdateControlToast(runtime);
        SizeScreen();
    }

    void RefreshMemory(WorkspaceRuntime? runtime)
    {
        if (!ReferenceEquals(runtime, _memoryRuntime))
        {
            _memoryRuntime = runtime;
            _memoryGeneration++;
            MemoryText.Text = ""; // Never show a prior workspace's sample or an invented zero.
        }
        bool show = runtime is not null && HeaderError.Visibility != Visibility.Visible;
        MemoryText.Visibility = show && MemoryText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (runtime is not null && !_readingMemory) _ = ReadMemoryAsync(runtime, _memoryGeneration);
    }

    async Task ReadMemoryAsync(WorkspaceRuntime runtime, int generation)
    {
        _readingMemory = true;
        try
        {
            ulong? bytes = await Task.Run(runtime.PrivateWorkingSetBytes);
            // A pending sample must not revive a sleeping, switched, unloaded or disposed page.
            if (_disposed || generation != _memoryGeneration || !IsVisible
                || !ReferenceEquals(WorkspaceRuntime.Of(_id ?? ""), runtime)) return;
            ApplyMemorySample(bytes);
        }
        finally { _readingMemory = false; }
    }

    internal void ApplyMemorySample(ulong? bytes)
    {
        MemoryText.Text = bytes is { } measured ? FormatWorkspaceMemory(measured) : "";
        MemoryText.Visibility = bytes.HasValue && HeaderError.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static string FormatWorkspaceMemory(ulong bytes)
    {
        const ulong megabyte = 1024 * 1024, gigabyte = 1024 * megabyte;
        return "Workspace RAM " + (bytes < gigabyte
            ? Math.Min(1023, Math.Round(bytes / (double)megabyte)).ToString("0", CultureInfo.CurrentCulture) + " MB"
            : (bytes / (double)gigabyte).ToString("0.0", CultureInfo.CurrentCulture) + " GB");
    }

    /// <summary>Keeps "You have control" listening to whichever WorkspaceRuntime instance actually
    /// backs this workspace right now. Re-checked on every RefreshLive tick
    /// instead of once in SetWorkspace, so a workspace that slept and woke under a new runtime while
    /// this page was open does not leave the toast listening to a runtime that is gone.</summary>
    void UpdateDriverSubscription(WorkspaceRuntime? runtime)
    {
        if (ReferenceEquals(_driverRuntime, runtime)) return;
        if (_driverRuntime is not null) _driverRuntime.DriverChanged -= OnDriverChanged;
        _driverRuntime = runtime;
        if (_driverRuntime is not null) _driverRuntime.DriverChanged += OnDriverChanged;
    }

    // DriverChanged can arrive from an agent's own thread (WorkspaceControl.AgentTakes), not only
    // from this page's own UI-thread input handling, so this hop is not optional.
    void OnDriverChanged(Driver who) => Dispatcher.BeginInvoke(() => UpdateControlToast(WorkspaceRuntime.Of(_id ?? "")));

    // The owner pressing the picture (WorkspaceScreenInput.OwnerActed) already runs on this page's
    // own UI thread, so this one updates the toast directly rather than hopping through the dispatcher.
    void OwnerActed()
    {
        UpdateControlToast(WorkspaceRuntime.Of(_id ?? ""));
        // Taking control speeds the picture up now, not at the end of a glance-paced wait.
        if (_screenTimer is { IsEnabled: true }) ScreenTick(null, EventArgs.Empty);
    }

    /// <summary>"You have control": visible for exactly as long as
    /// WorkspaceControl.Driving says the owner is the one driving.</summary>
    void UpdateControlToast(WorkspaceRuntime? runtime) =>
        ControlToast.Visibility = runtime?.Plane?.Driving == Driver.Owner ? Visibility.Visible : Visibility.Collapsed;

    // --- "What it did", from the workspace's own evidence log --------------------------------

    void LoadDid(string id)
    {
        _didSeen = ActivityLog.Signature(id, logs: true);
        _did.Clear();
        foreach (DidRow row in ActivityLog.Read(id)) _did.Add(row);
        DidEmptyText.Visibility = _did.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- "Files", newest first from the workspace folder --------------------------------------

    void LoadFiles(string id)
    {
        _filesSeen = ActivityLog.Signature(id, logs: false);
        _files.Clear();
        foreach (FileRow row in ActivityLog.Files(id)) _files.Add(row);
        FilesEmptyText.Visibility = _files.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- the live screen -----------------------------------------------------------------------

    void StartScreenTimer()
    {
        _screenTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _screenTimer.Interval = HubPreview.Interval();
        _screenTimer.Tick -= ScreenTick;
        _screenTimer.Tick += ScreenTick;
        if (!IsVisible) return;
        _screenTimer.Start();
        ScreenTick(null, EventArgs.Empty);
    }

    /// <summary>This page holds control, so it redraws fast enough to drive. Not the pointer being on
    /// the picture: here the picture fills most of the window, so hovering meant watching at the
    /// driving pace, and every fast frame queues on the thread the agent's own actions use.</summary>
    bool HandsOn => _input?.OwnsControl == true;
    long _livedAt;

    async void ScreenTick(object? sender, EventArgs e)
    {
        if (_disposed || _id is not { } id || !IsVisible) return;
        // Like the stack's loop, the pace is re-read every tick rather than kept from the start, so
        // a power change, or the owner's hand arriving or leaving, reaches this screen at once.
        bool handsOn = HandsOn;
        if (_screenTimer is { } beat) beat.Interval = handsOn ? HubPreview.HandsOnInterval() : HubPreview.Interval();
        // Only the picture speeds up. The pills read the store, memory samples the workspace jobs
        // off-thread, and both keep the glance pace however fast frames are coming.
        if (!handsOn || Environment.TickCount64 - _livedAt >= HubPreview.Interval().TotalMilliseconds)
        {
            _livedAt = Environment.TickCount64;
            RefreshLive();
            // A finished command or a new file shows without reselecting the workspace.
            if (ActivityLog.Signature(id, logs: true) != _didSeen) LoadDid(id);
            if (ActivityLog.Signature(id, logs: false) != _filesSeen) LoadFiles(id);
        }
        WorkspaceControl? plane = WorkspaceRuntime.Of(id)?.Plane;
        // Asleep: the picture is the last one it had, dimmed, and says so rather than looking live
        // or broken.
        LastSeenPill.Visibility = plane is null && ScreenImage.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        ScreenImage.Opacity = plane is null ? 0.55 : 1.0;
        if (_taskbar is not null)
            _taskbar.Visibility = plane is not null && !(ScreenImage.Source is BitmapSource shown && plane.IsBrowserViewport(shown))
                && AppSettingsStore.Current.AgentScreen == AgentScreenLook.Full
                ? Visibility.Visible : Visibility.Collapsed;
        if (plane is null)
        {
            if (WorkspaceStore.Find(id) is { } stored)
                LastSeenText.Text = "Last seen " + stored.LastUsed.ToLocalTime().ToString("MMM d, h:mm tt");
            return;
        }
        // One capture in flight at a time: a slow Task.Run from an earlier tick must finish (or be
        // dropped below) before another starts. Checked after the asleep look above, so Sleep dims
        // the picture at once even while a capture is still landing.
        if (_capturingScreen) return;
        _capturingScreen = true;
        try
        {
            BitmapSource? frame = await Task.Run(() =>
            {
                try { BitmapSource? shot = plane.Frame(); if (shot is not null && !shot.IsFrozen) shot.Freeze(); return shot; }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
            });
            // Drop a result that lands after the view hid or moved on to another workspace.
            if (_disposed || id != _id || !IsVisible || frame is null
                || !ReferenceEquals(WorkspaceRuntime.Of(id)?.Plane, plane)) return;
            ScreenImage.Source = frame;
            _liveShown = true;
            _shownPlane = plane;
        }
        finally { _capturingScreen = false; }
    }

    /// <summary>
    /// The last picture this workspace's screen had, while a live one is on its way or will never
    /// come. Read off the UI thread: decoding a whole screen's PNG on it is what made opening a
    /// recent workspace sit there for a moment before anything appeared.
    /// </summary>
    async void ShowStoredFrame(string id)
    {
        BitmapSource? last = await HubLastLook.FullAsync(id);
        // A live frame that landed while this was decoding is the newer truth and keeps the screen.
        if (_disposed || id != _id || last is null || _liveShown) return;
        ScreenImage.Source = last;
        if (WorkspaceRuntime.Of(id)?.Plane is null)
        {
            ScreenImage.Opacity = 0.55;
            LastSeenPill.Visibility = Visibility.Visible;
            if (WorkspaceStore.Find(id) is { } stored)
                LastSeenText.Text = "Last seen " + stored.LastUsed.ToLocalTime().ToString("MMM d, h:mm tt");
        }
        SizeScreen();
    }

    void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        => SizeScreen();

    void SizeScreen()
    {
        if (ScreenBorder.Parent is not FrameworkElement parent || parent.ActualWidth <= 0) return;
        bool live = _fixture || (_id is { } id && WorkspaceRuntime.Of(id)?.Plane is not null);
        // History is a record to scan. Keep its last picture small so activity and files stay
        // visible; a running workspace keeps the full interactive screen.
        double width = live ? parent.ActualWidth : Math.Min(320, parent.ActualWidth);
        ScreenBorder.HorizontalAlignment = HorizontalAlignment.Left;
        ScreenBorder.Width = width;
        ScreenBorder.Height = Math.Round(width * AgentDesktop.ScreenHeight / AgentDesktop.ScreenWidth);
        ScreenBorder.Visibility = live || ScreenImage.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        ScreenImage.Cursor = live ? Cursors.Hand : Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName(ScreenImage, live ? "Live workspace screen" : "Last workspace screen");
    }

    /// <summary>Tracks whether the owner is looking at the newest row so a reload can follow it
    /// there without overriding a scroll they did themselves. An extent change with no offset change is
    /// new content landing, not the owner's hand; only their own scroll updates the flag.</summary>
    void ActivityScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
        {
            if (_followTail) ActivityScroll.ScrollToEnd();
            return;
        }
        _followTail = ActivityScroll.VerticalOffset >= ActivityScroll.ScrollableHeight - 1;
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (PathText.Text.Length > 0) SettingsActions.CopyText(PathText.Text);
    }

    /// <summary>Sleeps this workspace right now, in one click. The same
    /// WorkspaceRuntime.Of(id)?.Dispose() the delete path already uses: work is not lost, the
    /// folder and last picture stay, and the next agent call wakes it. Refreshed immediately rather
    /// than left to the next timer tick, since the owner just asked for this and expects to see it.</summary>
    void Sleep_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        WorkspaceRuntime.Of(id)?.Dispose();
        RefreshLive();
        ScreenTick(null, EventArgs.Empty);
    }

    // --- folder, rename, more ------------------------------------------------------------------

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        HeaderError.Visibility = Visibility.Collapsed;
        try
        {
            string folder = WorkspaceStore.FolderOf(id);
            if (!Directory.Exists(folder)) { ShowHeaderError("Couldn't open the folder."); return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { ShowHeaderError("Couldn't open the folder."); }
    }

    void ShowHeaderError(string message)
    {
        HeaderError.Text = message;
        HeaderError.Visibility = Visibility.Visible;
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        HeaderError.Visibility = Visibility.Collapsed;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        if (Window.GetWindow(this) is not WorkspaceWindow) AddMenuItem(menu, "Pop out", PopOut);
        AddMenuItem(menu, "Rename", BeginRename);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Delete", () => DeleteWorkspace(id));
        // Test seam (ui-probe/Scenes.Hub.cs): the UI probe drives this exact menu instance rather than
        // rebuilding its own copy of the item list above.
        LastMoreMenu = menu;
        menu.IsOpen = true;
    }

    internal ContextMenu? LastMoreMenu;

    static void AddMenuItem(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        System.Windows.Automation.AutomationProperties.SetName(item, label);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    void PopOut()
    {
        if (_id is { } id && Window.GetWindow(this) is MainWindow main) main.PopOutWorkspace(id);
    }

    ContextMenu? _pendingScreenMenu;
    internal ContextMenu? LastScreenMenu;

    internal bool ScreenRightDown(Point point)
    {
        if (!_liveShown || _input?.EmptyDesktop(point) != true) return false;
        var menu = new ContextMenu { PlacementTarget = ScreenImage, Placement = PlacementMode.RelativePoint,
            HorizontalOffset = point.X, VerticalOffset = point.Y };
        if (_taskbar is { IsVisible: true } taskbar) AddMenuItem(menu, "Open an app...", taskbar.OpenMenu);
        if (Window.GetWindow(this) is not WorkspaceWindow) AddMenuItem(menu, "Pop out", PopOut);
        if (_input.OwnsControl) AddMenuItem(menu, "Give back control", _input.Release);
        LastScreenMenu = _pendingScreenMenu = menu;
        return true;
    }

    internal bool ScreenRightUp()
    {
        if (_pendingScreenMenu is not { } menu) return false;
        _pendingScreenMenu = null;
        if (ScreenImage.IsMouseCaptured) ScreenImage.ReleaseMouseCapture();
        // Open after release so the popup cannot steal the matching button-up.
        if (_id is { } id && ReferenceEquals(WorkspaceRuntime.Of(id)?.Plane, _shownPlane) && IsVisible)
            menu.IsOpen = true;
        return true;
    }

    void Screen_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (!ScreenRightDown(e.GetPosition(ScreenImage))) return;
        ScreenImage.CaptureMouse();
        e.Handled = true;
    }

    void Screen_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (ScreenRightUp()) e.Handled = true;
    }

    void BeginRename()
    {
        HeaderError.Visibility = Visibility.Collapsed;
        _renaming = true;
        NameText.Visibility = Visibility.Collapsed;
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void CommitRename()
    {
        if (!_renaming) return;
        _renaming = false;
        RenameBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
        string name = RenameBox.Text.Trim();
        if (_id is not { } id || name.Length == 0) { Reload(); return; }
        if (WorkspaceStore.Update(id, w => w with { Name = name }) is { } updated)
        {
            if (WorkspaceRuntime.Of(id) is { } runtime) runtime.DisplayName = updated.Name;
            NameText.Text = updated.Name;
            Renamed?.Invoke(id, updated.Name);
        }
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _renaming = false; RenameBox.Visibility = Visibility.Collapsed; NameText.Visibility = Visibility.Visible; e.Handled = true; }
    }
    void RenameBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    internal static Func<bool>? ConfirmDeleteForTests;

    void DeleteWorkspace(string id)
    {
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        if (workspace is null) return;
        // What goes is what lives in the workspace folder: the agent's screen, the files on its page, its
        // history and browser sign-ins. The project folder is never touched, and a person deciding
        // whether to press Delete needs that said more than anything else.
        if (!(ConfirmDeleteForTests?.Invoke() ?? Question.Ask(Window.GetWindow(this), "Delete " + workspace.Name + "?",
                "Its screen, the files on its page, its history and its browser sign-ins are deleted, and anything "
                + "running on it stops. Your project folder is not touched. This can't be undone.", "Delete", danger: true))) return;
        WorkspaceRuntime.Of(id)?.Dispose();
        WorkspaceAccessStore.Write(id, new WorkspaceAccessPolicy());
        WorkspaceAccessStore.Withdraw(id);
        // Windows can still hold a file in it for longer than Delete waits. The workspace is still
        // there then, so say so here rather than let the hub drop a card for something that stayed.
        if (!WorkspaceStore.Delete(id)) { ShowHeaderError("Couldn't delete it. Something still has its files open."); return; }
        Deleted?.Invoke(id);
    }

    // --- a scene's fixed content, in place of the real stores ----------------------------------

    internal void LoadFixture(string name, string path, IEnumerable<(string Text, bool Working)> pills,
        BitmapSource screen, IEnumerable<(string Time, string Prefix, string Code, BitmapSource? Thumb)> did,
        IEnumerable<(string Name, string When)> files)
    {
        StopLive();
        _fixture = true;
        _id = null;
        NameText.Text = name;
        PathText.Text = path;
        _pills.Clear();
        foreach (var (text, working) in pills) _pills.Add(new PillInfo(text, working));
        ScreenImage.Source = screen;
        _did.Clear();
        foreach (var (time, prefix, code, thumb) in did) _did.Add(new DidRow(time, prefix, code, thumb));
        DidEmptyText.Visibility = _did.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _files.Clear();
        foreach (var (fname, when) in files) _files.Add(new FileRow(fname, when));
        FilesEmptyText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void StopLive()
    {
        _memoryGeneration++;
        _memoryRuntime = null;
        MemoryText.Text = "";
        MemoryText.Visibility = Visibility.Collapsed;
        _pendingScreenMenu = null;
        if (LastScreenMenu is { } menu) menu.IsOpen = false;
        if (LastMoreMenu is { } more) more.IsOpen = false;
        if (ScreenImage.IsMouseCaptured) ScreenImage.ReleaseMouseCapture();
        if (_screenTimer is not null) { _screenTimer.Stop(); _screenTimer.Tick -= ScreenTick; _screenTimer = null; }
        if (_driverRuntime is not null) { _driverRuntime.DriverChanged -= OnDriverChanged; _driverRuntime = null; }
        ControlToast.Visibility = Visibility.Collapsed;
        if (_input is not null) { _input.OwnerActed -= OwnerActed; _input.Dispose(); _input = null; }
        if (_taskbar is not null) { ScreenLayers.Children.Remove(_taskbar); _taskbar = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLive();
    }
}
