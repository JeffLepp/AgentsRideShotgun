using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    public event RoutedEventHandler? ShowMore;

    public WorkspaceInlineView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) { Detach(); Attach(); } };
        IsVisibleChanged += (_, _) => Refresh();
        Screen.MouseEnter += (_, _) => HandsOn();
        Screen.MouseLeave += (_, _) => HandsOn();
    }

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
        Refresh();
    }

    void Detach()
    {
        StopInput();
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
            : runtime is not null ? "Waiting for the screen" : _entry.Preview is null ? "Screen stopped" : "Last picture · screen stopped";
        ReturnButton.Visibility = acceptsInput && _input?.OwnsControl == true ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = runtime is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(Screen, _entry.Name + (live ? ", interactive screen" : ", last picture"));
        AutomationProperties.SetName(StartButton, "Start screen for " + _entry.Name);
        AutomationProperties.SetName(MoreButton, "Show more for " + _entry.Name);
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

    void More_Click(object sender, RoutedEventArgs e) => ShowMore?.Invoke(this, e);
    void Return_Click(object sender, RoutedEventArgs e) { _input?.Release(); Refresh(); }
    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is null) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            StoredWorkspace record = WorkspaceStore.Find(_entry.Id) ?? throw new InvalidOperationException("This workspace was deleted.");
            if (WorkspaceRuntime.Of(record.Id) is null && WorkspaceRuntime.Running.Count >= WorkspaceRouter.MaxRunning
                && !WorkspaceRuntime.SleepQuietest())
                throw new InvalidOperationException("All screens are busy. Sleep another workspace, then try again.");
            WorkspaceRuntime.Start(record);
            Refresh();
        }
        catch (Exception error)
        {
            ErrorText.Text = "Couldn't start this screen. " + error.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
