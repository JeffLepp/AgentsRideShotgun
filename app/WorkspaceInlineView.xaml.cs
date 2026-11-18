using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

/// <summary>The strip uses the hub's existing frame loop and the engine's owner input lease. While the
/// owner's hand is on it, it runs a faster loop of its own (<see cref="HubPreview.HandsOnInterval"/>).</summary>
public partial class WorkspaceInlineView : UserControl
{
    HubEntry? _entry;
    Window? _window;
    ScrollViewer? _viewport;
    WorkspaceRuntime? _runtime;
    WorkspaceScreenInput? _input;
    DispatcherTimer? _handsOn;
    bool _capturing;
    /// <summary>Which list is open under the screen: none, "logs" or "files". One at a time.</summary>
    string _open = "";
    /// <summary>Bumped by every load, so a slow read that lands after a newer one is dropped.</summary>
    int _loadTicket;
    bool _loading;
    /// <summary>What the open list was read from (log size, folder write time), checked every few
    /// seconds so a finished command or a new file shows without reopening the list.</summary>
    DispatcherTimer? _watch;
    (long, DateTime) _seen;

    public WorkspaceInlineView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) { Detach(); Attach(); } };
        IsVisibleChanged += (_, _) =>
        {
            // Closing the card folds its list again, so the next open shows only the screen.
            if (!IsVisible) _open = "";
            LoadActivity();
            Refresh();
        };
        Screen.MouseEnter += (_, _) => HandsOn();
        Screen.MouseLeave += (_, _) => HandsOn();
    }

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(object), typeof(WorkspaceInlineView));
    /// <summary>Shown between the screen and its controls.</summary>
    public object? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    internal WorkspaceScreenInput? ScreenInput => _input;
    internal bool Interactive => _input is not null;
    void Attach()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        _entry = DataContext as HubEntry;
        if (_entry is not null) _entry.PropertyChanged += EntryChanged;
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += WindowChanged;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { _viewport = scroll; break; }
        if (_viewport is not null) _viewport.ScrollChanged += Scrolled;
        LoadActivity();
        Refresh();
    }

    void ShowLogs_Click(object sender, RoutedEventArgs e) { _open = _open == "logs" ? "" : "logs"; LoadActivity(); }
    void ShowFiles_Click(object sender, RoutedEventArgs e) { _open = _open == "files" ? "" : "files"; LoadActivity(); }

    /// <summary>Reads only the list that is open, and only while the card is: every card carries
    /// this view, collapsed, and a closed one must not read its log or folder. The read runs off the
    /// UI thread so a long log never stalls the screen above it.</summary>
    async void LoadActivity()
    {
        bool logs = _open == "logs", files = _open == "files";
        ShowLogsButton.Content = logs ? "Hide logs" : "Show logs";
        ShowFilesButton.Content = files ? "Hide files" : "Show files";
        // The spoken name follows the visible label, so a screen reader hears "Hide" when it says Hide.
        string of = _entry is { } named ? " for " + named.Name : "";
        AutomationProperties.SetName(ShowLogsButton, ShowLogsButton.Content + of);
        AutomationProperties.SetName(ShowFilesButton, ShowFilesButton.Content + of);
        int ticket = ++_loadTicket;
        if ((!logs && !files) || _entry is not { } entry || !IsVisible)
        {
            StopWatching();
            StepList.ItemsSource = null;
            FileList.ItemsSource = null;
            StepList.Visibility = FileList.Visibility = NothingText.Visibility = Visibility.Collapsed;
            return;
        }
        string id = entry.Id;
        (long, DateTime) seen = Signature(id);
        _loading = true;
        System.Collections.IList rows;
        try
        {
            rows = await Task.Run<System.Collections.IList>(() =>
                logs ? ActivityLog.Read(id, thumbs: false) : ActivityLog.Files(id));
        }
        finally { if (ticket == _loadTicket) _loading = false; }
        if (ticket != _loadTicket) return;
        _seen = seen;
        StepList.ItemsSource = logs ? rows : null;
        FileList.ItemsSource = files ? rows : null;
        StepList.Visibility = logs && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = files && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NothingText.Text = files ? "No files yet" : "No logs yet";
        NothingText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartWatching();
    }

    (long, DateTime) Signature(string id) => ActivityLog.Signature(id, _open == "logs");

    void StartWatching()
    {
        if (_watch is not null) return;
        _watch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        // A read still in flight is left to finish rather than overtaken by another.
        _watch.Tick += (_, _) => { if (!_loading && _entry is { } e && Signature(e.Id) != _seen) LoadActivity(); };
        _watch.Start();
    }

    void StopWatching() { _watch?.Stop(); _watch = null; }

    /// <summary>A stopped screen starts from the button laid over its last picture, the job of the
    /// old Start screen button. A running one has no button there and takes the click as input.</summary>
    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is null || WorkspaceRuntime.Of(_entry.Id) is not null) return;
        ErrorText.Visibility = Visibility.Collapsed;
        if (StartScreen(_entry.Id) is { } error) ShowError(error);
        Refresh();
    }

    /// <summary>A double-click on this screen opens the workspace page, the same as on the closed card,
    /// whose second click lands here once the first has opened it. Its second click never reaches the
    /// workspace; double-clicking inside the workspace is done on the page.</summary>
    void Frame_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        e.Handled = true;
        if (_entry is { } entry && Window.GetWindow(this) is MainWindow shell) shell.ShowWide(entry.Id);
    }

    void File_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !ActivityLog.Open(path)) ShowError("Couldn't open it.");
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Detach()
    {
        StopInput();
        StopWatching();
        if (_entry is not null) _entry.PropertyChanged -= EntryChanged;
        if (_window is not null) _window.StateChanged -= WindowChanged;
        if (_viewport is not null) _viewport.ScrollChanged -= Scrolled;
        _entry = null;
        _window = null;
        _viewport = null;
    }

    // A fresh frame from the fast loop changes nothing else this view shows.
    void EntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_handsOn is null || e.PropertyName != nameof(HubEntry.Preview)) Refresh();
    }
    void WindowChanged(object? sender, EventArgs e) => Refresh();
    void Scrolled(object sender, ScrollChangedEventArgs e) => Refresh();
    void DriverChanged(Driver _) => Dispatcher.BeginInvoke(Refresh);
    void RuntimeEnded() => Dispatcher.BeginInvoke(Refresh);

    bool Exposed()
    {
        if (!IsLoaded || !IsVisible || _window?.WindowState == WindowState.Minimized
            || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return false;
        if (_viewport is null) return true;
        try
        {
            Rect bounds = Screen.TransformToAncestor(_viewport).TransformBounds(new Rect(Screen.RenderSize));
            return bounds.Left < _viewport.ViewportWidth && bounds.Right > 0
                && bounds.Top < _viewport.ViewportHeight && bounds.Bottom > 0;
        }
        catch (InvalidOperationException) { return false; }
    }

    void Refresh()
    {
        if (_entry is null) return;
        WorkspaceRuntime? runtime = WorkspaceRuntime.Of(_entry.Id);
        bool live = runtime?.Plane is { } plane && ReferenceEquals(plane, _entry.PreviewPlane) && _entry.Preview is not null;
        bool acceptsInput = live && Exposed();
        if (!acceptsInput || !ReferenceEquals(runtime, _runtime)) StopInput();
        if (acceptsInput && _input is null)
        {
            _runtime = runtime;
            _runtime!.DriverChanged += DriverChanged;
            _runtime.Ended += RuntimeEnded;
            _input = new WorkspaceScreenInput(Screen, () =>
                ReferenceEquals(WorkspaceRuntime.Of(_entry?.Id ?? ""), _runtime) ? _runtime : null);
        }
        Screen.IsHitTestVisible = acceptsInput;
        Screen.Focusable = acceptsInput;
        Screen.Opacity = live ? 1 : 0.7;
        EmptyText.Visibility = _entry.Preview is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = runtime is null ? "No picture yet" : "Starting screen…";
        bool owner = runtime?.Plane?.Driving == Driver.Owner;
        StateText.Text = live ? owner ? "You have control" : "Click screen to interact"
            : runtime is not null ? "Waiting for the screen" : "Screen stopped · click to start";
        ReturnButton.Visibility = acceptsInput && _input?.OwnsControl == true ? Visibility.Visible : Visibility.Collapsed;
        StartOverlay.Visibility = runtime is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(StartOverlay, "Start screen for " + _entry.Name);
        AutomationProperties.SetName(Screen, _entry.Name + (live ? ", interactive screen" : ", last picture"));
        HandsOn();
    }

    /// <summary>Starts or stops the fast loop: on while this screen takes input and the pointer is on
    /// it or it holds control.</summary>
    void HandsOn()
    {
        bool on = _input is { } input && (Screen.IsMouseOver || input.OwnsControl);
        if (on && _handsOn is null)
        {
            _handsOn = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = HubPreview.HandsOnInterval() };
            _handsOn.Tick += HandsOnTick;
            _handsOn.Start();
            HandsOnTick(null, EventArgs.Empty);
        }
        else if (!on && _handsOn is not null)
        {
            _handsOn.Stop();
            _handsOn.Tick -= HandsOnTick;
            _handsOn = null;
        }
    }

    async void HandsOnTick(object? sender, EventArgs e)
    {
        if (_handsOn is { } beat) beat.Interval = HubPreview.HandsOnInterval();
        // One capture in flight: a screen slower to photograph than the interval sets its own pace.
        if (_capturing || _entry is not { } entry || _runtime?.Plane is not { } plane) return;
        _capturing = true;
        try
        {
            BitmapSource? frame = await Task.Run(() =>
            {
                try { BitmapSource? shot = plane.Frame(); if (shot is not null && !shot.IsFrozen) shot.Freeze(); return shot; }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
            });
            // The same entry the hub's own loop feeds, so the card, the sidebar and this strip agree.
            if (_handsOn is not null && frame is not null && ReferenceEquals(_entry, entry)
                && ReferenceEquals(WorkspaceRuntime.Of(entry.Id)?.Plane, plane))
            {
                entry.Preview = frame;
                entry.PreviewPlane = plane;
            }
        }
        finally { _capturing = false; }
    }

    void StopInput()
    {
        if (_runtime is not null)
        {
            _runtime.DriverChanged -= DriverChanged;
            _runtime.Ended -= RuntimeEnded;
        }
        _input?.Dispose();
        _input = null;
        _runtime = null;
        HandsOn();
        Screen.IsHitTestVisible = false;
        Screen.Focusable = false;
        ReturnButton.Visibility = Visibility.Collapsed;
    }

    void Frame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Frame.Height = Math.Round(Frame.ActualWidth * AgentDesktop.ScreenHeight / AgentDesktop.ScreenWidth);
        Frame.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth, Frame.ActualHeight), 5, 5);
        Refresh();
    }

    void Return_Click(object sender, RoutedEventArgs e) { _input?.Release(); Refresh(); }
    /// <summary>Starts a workspace's screen, sleeping the quietest one when every slot is taken.
    /// Null when it started, otherwise the sentence to show.</summary>
    internal static string? StartScreen(string id)
    {
        try
        {
            StoredWorkspace record = WorkspaceStore.Find(id) ?? throw new InvalidOperationException("This workspace was deleted.");
            if (WorkspaceRuntime.Of(record.Id) is null && WorkspaceRuntime.Running.Count >= WorkspaceRouter.MaxRunning
                && !WorkspaceRuntime.SleepQuietest())
                throw new InvalidOperationException("All screens are busy. Sleep another workspace, then try again.");
            WorkspaceRuntime.Start(record);
            return null;
        }
        catch (Exception error) { return "Couldn't start this screen. " + error.Message; }
    }

    /// <summary>Shown once when this card opens, for a start that failed before it existed (the + button).</summary>
    internal void ShowStartError(string message) => ShowError(message);
}
