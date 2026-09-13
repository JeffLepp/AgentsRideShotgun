using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

public partial class MainWindow : Window, IDisposable
{
    readonly HubViewModel _hub = new();
    readonly ObservableCollection<HubEntry> _stackWorking = [];
    readonly ObservableCollection<HubEntry> _stackAsleep = [];
    readonly DispatcherTimer _cardPreviews;
    readonly DispatcherTimer _savePreferences;
    WorkspaceFullView? _workspaceView;
    ShellPlacement? _stackPlacement;
    ShellPlacement? _widePlacement;
    bool _wasMaximized;
    bool _wasMinimized;
    bool _restoringPreferences = true;
    bool _capturing;
    bool _disposed;
    long _previewGeneration;
    string _mode = "stack";
    string _priorMode = "stack";
    string? _selectedId;
    string _filter = "";

    public MainWindow()
    {
        AppearanceManager.Apply(AppSettingsStore.Current.Theme);
        InitializeComponent();
        StackWorkingList.ItemsSource = _stackWorking;
        StackAsleepList.ItemsSource = _stackAsleep;
        SidebarWorkingList.ItemsSource = _hub.Working;
        SidebarAsleepList.ItemsSource = _hub.Asleep;
        _hub.Working.CollectionChanged += HubChanged;
        _hub.Asleep.CollectionChanged += HubChanged;
        _cardPreviews = new DispatcherTimer(DispatcherPriority.Background) { Interval = HubPreview.Interval() };
        _cardPreviews.Tick += CardPreviews_Tick;
        _savePreferences = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _savePreferences.Tick += (_, _) => { _savePreferences.Stop(); SavePreferences(); };
        ModuleEntry.DashboardOpenRequested += EngineRequestedWorkspace;
        LocationChanged += (_, _) => QueuePreferenceSave();
        Loaded += Window_Loaded;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Never empty (brief A.4): a fresh install always has something under Recent to show.
        if (WorkspaceStore.All().Count == 0) WorkspaceStore.Create("Scratch");
        RestorePreferences();
        _hub.Refresh();
        UpdateVisibleWork();
    }

    void HubChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildStackLists();

    void RebuildStackLists()
    {
        IEnumerable<HubEntry> Filtered(IEnumerable<HubEntry> source) =>
            _filter.Length == 0 ? source : source.Where(entry => entry.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        Sync(_stackWorking, Filtered(_hub.Working).ToList());
        Sync(_stackAsleep, Filtered(_hub.Asleep).ToList());
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

    // --- window mode: stack, wide, or settings inside the wide frame ---------------------------

    internal string DisplayMode => _mode;
    internal string? SelectedWorkspaceId => _selectedId;
    internal WorkspaceFullView? OpenWorkspaceView => _workspaceView;
    internal HubViewModel Hub => _hub;

    void RestorePreferences()
    {
        ShellPreferences preferences = ShellPreferences.Read();
        _stackPlacement = preferences.Stack;
        _widePlacement = preferences.Wide;
        _wasMaximized = preferences.Maximized;
        _selectedId = preferences.SelectedWorkspace;
        if (preferences.Mode == "wide" && _selectedId is not null) ShowWide(_selectedId);
        else ShowStack();
        if (_wasMaximized) WindowState = WindowState.Maximized;
        _restoringPreferences = false;
    }

    internal void ShowStack()
    {
        _mode = "stack";
        MinWidth = 320; MinHeight = 480;
        SettingsSlot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Collapsed;
        StackRoot.Visibility = Visibility.Visible;
        (_stackPlacement ?? ShellPlacement.DefaultStack()).Restore(this, 320, 480);
        QueuePreferenceSave();
        UpdateVisibleWork();
    }

    internal void ShowWide(string? select)
    {
        _mode = "wide";
        MinWidth = 960; MinHeight = 600;
        StackRoot.Visibility = Visibility.Collapsed;
        SettingsSlot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Visible;
        (_widePlacement ?? ShellPlacement.DefaultWide()).Restore(this, 960, 600);
        string? id = select ?? _selectedId ?? _hub.Working.Concat(_hub.Asleep).Select(entry => entry.Id).FirstOrDefault();
        if (id is not null) SelectWorkspace(id);
        QueuePreferenceSave();
        UpdateVisibleWork();
    }

    internal void SelectWorkspace(string id)
    {
        _selectedId = id;
        _hub.Select(id);
        EnsureWorkspaceView();
        _workspaceView!.SetWorkspace(id);
        QueuePreferenceSave();
    }

    void EnsureWorkspaceView()
    {
        if (_workspaceView is not null) return;
        _workspaceView = new WorkspaceFullView();
        _workspaceView.Renamed += (id, name) => { if (_hub.Find(id) is { } entry) entry.Name = name; };
        _workspaceView.Deleted += id => { if (_selectedId == id) ShowStack(); };
        MainSlot.Content = _workspaceView;
    }

    internal void ShowSettings()
    {
        if (_mode != "settings") _priorMode = _mode;
        bool first = SettingsSlot.Content is not SettingsView;
        if (first)
        {
            var created = new SettingsView();
            created.BackRequested += BackFromSettings;
            SettingsSlot.Content = created;
        }
        _mode = "settings";
        MinWidth = 960; MinHeight = 600;
        StackRoot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Collapsed;
        SettingsSlot.Visibility = Visibility.Visible;
        (_widePlacement ?? ShellPlacement.DefaultWide()).Restore(this, 960, 600);
        var view = (SettingsView)SettingsSlot.Content;
        if (first) view.Show("general");
        // So Escape reaches its own handler first (it raises BackRequested) rather than this window's.
        view.Focus();
        QueuePreferenceSave();
        UpdateVisibleWork();
    }

    void BackFromSettings()
    {
        if (_priorMode == "wide") ShowWide(_selectedId); else ShowStack();
    }

    // --- filter, clicks --------------------------------------------------------------------------

    void WorkingCard_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) ShowWide(id); }
    void AsleepRow_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) ShowWide(id); }
    void SidebarItem_Checked(object sender, RoutedEventArgs e) { if (sender is RadioButton { DataContext: HubEntry entry }) SelectWorkspace(entry.Id); }

    void BeginFilter()
    {
        FilterHost.Visibility = Visibility.Visible;
        FilterBox.Focus();
    }

    void ClearFilter()
    {
        FilterBox.Text = "";
        FilterHost.Visibility = Visibility.Collapsed;
        FocusManager.SetFocusedElement(this, null);
        Keyboard.ClearFocus();
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        RebuildStackLists();
    }

    // --- previews: the working cards and the wide sidebar's thumbnails share one loop ----------

    void UpdateVisibleWork()
    {
        // Settings covers the whole window: none of the working cards or the sidebar's thumbnails
        // are on screen while it shows, so there is nothing to capture.
        bool shown = IsLoaded && IsVisible && WindowState != WindowState.Minimized && _mode != "settings";
        ModuleEntry.HubShowing = shown;
        if (shown)
        {
            if (!_cardPreviews.IsEnabled)
            {
                _cardPreviews.Interval = HubPreview.Interval();
                _cardPreviews.Start();
                CardPreviews_Tick(null, EventArgs.Empty);
            }
            _hub.StartAging();
        }
        else { _cardPreviews.Stop(); _previewGeneration++; _hub.StopAging(); }
    }

    async void CardPreviews_Tick(object? sender, EventArgs e)
    {
        if (_capturing || _disposed || !_cardPreviews.IsEnabled || !HubPreview.Allowed) return;
        // Only the list actually on screen for the current mode has cards worth capturing; Settings
        // (or the window not showing) already stopped the timer in UpdateVisibleWork.
        if (CaptureSurface().List is null) return;
        _capturing = true;
        long generation = _previewGeneration;
        try
        {
            foreach (HubEntry entry in _hub.Working.ToArray())
            {
                if (_disposed || generation != _previewGeneration) break;
                if (!ShouldCapture(entry)) continue;
                WorkspaceControl? plane = WorkspaceRuntime.Of(entry.Id)?.Plane;
                if (plane is null) continue;
                BitmapSource? frame = await Task.Run(() =>
                {
                    try { BitmapSource? shot = plane.Frame(); if (shot is not null && !shot.IsFrozen) shot.Freeze(); return shot; }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
                });
                if (_disposed || generation != _previewGeneration) break;
                if (frame is not null) entry.Preview = frame;
            }
        }
        finally { _capturing = false; }
    }

    /// <summary>The list and its scroll viewport that are actually on screen for the current mode
    /// (null for "settings", where neither the stack nor the sidebar is visible).</summary>
    (ItemsControl? List, ScrollViewer? Viewport) CaptureSurface() => _mode switch
    {
        "wide" => (SidebarWorkingList, SidebarScroll),
        "stack" => (StackWorkingList, StackScroll),
        _ => (null, null),
    };

    /// <summary>True when <paramref name="entry"/>'s card is realized and any part of it falls
    /// inside the visible scrolled area of the current mode's list; used by the capture loop and,
    /// through <see cref="ShouldCaptureForTest"/>, by the gate driving this same method.</summary>
    bool ShouldCapture(HubEntry entry)
    {
        (ItemsControl? list, ScrollViewer? viewport) = CaptureSurface();
        return list is not null && list.ItemContainerGenerator.ContainerFromItem(entry) is FrameworkElement container
            && IsInViewport(viewport!, container);
    }

    /// <summary>True when any part of <paramref name="item"/> falls inside the visible scrolled
    /// area of <paramref name="viewport"/>; a card scrolled off above or below is skipped.</summary>
    static bool IsInViewport(ScrollViewer viewport, FrameworkElement item)
    {
        if (!item.IsVisible || item.ActualWidth <= 0 || item.ActualHeight <= 0) return false;
        Rect bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        return bounds.IntersectsWith(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
    }

    // --- test seams for the gate (Scenes.Hub.cs): drive this window's real state, never a copy ---

    /// <summary>True while the card-preview loop's timer is actually running.</summary>
    internal bool PreviewLoopRunning => _cardPreviews.IsEnabled;
    /// <summary>The card-preview loop's current tick interval, live after a Settings change.</summary>
    internal TimeSpan PreviewLoopInterval => _cardPreviews.Interval;
    /// <summary>True when the working entry with this id would be captured on the next tick, in
    /// whichever list (stack or sidebar) is on screen right now: the same test the loop itself runs.</summary>
    internal bool ShouldCaptureForTest(string id) =>
        _hub.Working.FirstOrDefault(entry => entry.Id == id) is { } entry && ShouldCapture(entry);

    void AspectBorder_Loaded(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        double ratio = double.Parse((string)border.Tag, System.Globalization.CultureInfo.InvariantCulture);
        double radius = border.CornerRadius.TopLeft;
        void Reclip()
        {
            // Border.ClipToBounds clips to the rectangular bounds only, not the rounded corners
            // themselves, so without this the preview image's square corners cover the radius.
            if (border.ActualWidth > 0 && border.ActualHeight > 0)
                border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
        }
        border.SizeChanged += (_, args) => { if (args.WidthChanged) border.Height = Math.Round(border.ActualWidth * ratio); Reclip(); };
        Reclip();
    }

    // --- the engine asking for a specific workspace, and the corner window's visibility rule ---

    void EngineRequestedWorkspace()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
            Activate();
            if (ModuleEntry.Selected is { } id) ShowWide(id);
            UpdateVisibleWork();
        });
    }

    /// <summary>Reopening from the tray or the taskbar always lands on the stack (brief A.5).</summary>
    public void RestoreWorkspaceWindow()
    {
        if (_disposed) return;
        if (_mode != "stack") ShowStack();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        UpdateVisibleWork();
    }

    // --- preferences -----------------------------------------------------------------------------

    void QueuePreferenceSave()
    {
        if (_restoringPreferences || _disposed || !IsLoaded) return;
        _savePreferences.Stop();
        _savePreferences.Start();
    }

    void SavePreferences()
    {
        if (_restoringPreferences || !IsLoaded) return;
        if (WindowState == WindowState.Normal)
        {
            if (_mode == "wide" || (_mode == "settings" && _priorMode == "wide")) _widePlacement = ShellPlacement.Capture(this);
            else if (_mode == "stack" || (_mode == "settings" && _priorMode == "stack")) _stackPlacement = ShellPlacement.Capture(this);
        }
        if (WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized;
        _ = new ShellPreferences
        {
            Stack = _stackPlacement, Wide = _widePlacement, Maximized = _wasMaximized,
            Mode = _mode == "settings" ? _priorMode : _mode, SelectedWorkspace = _selectedId
        }.Save();
    }

    // --- title bar -------------------------------------------------------------------------------

    void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    // The close button always hides to the tray (brief A.5); Quit lives only on the tray menu.
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // --- windows: keyboard, DWM rounding, lifecycle -----------------------------------------------

    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Windows 10 stays square: a deliberate exception recorded in the spec (WAVE1, A.2).
        if (Environment.OSVersion.Version.Build < 22000) return;
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // Escape while focus sits outside Settings (on the title bar, say) still goes back. Inside it,
    // Settings has already handled the key.
    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _mode != "settings") return;
        e.Handled = true;
        BackFromSettings();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Settings handles its own Escape and raises BackRequested; this window
        // only reacts to Escape outside it, so the two never race for the same key press.
        if (e.Key == Key.Escape && _mode != "settings")
        {
            if (FilterHost.Visibility == Visibility.Visible) { ClearFilter(); e.Handled = true; }
            else if (_mode == "wide") { ShowStack(); e.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.F && _mode == "stack") { BeginFilter(); e.Handled = true; }
            else if (e.Key == Key.OemComma) { Settings_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        }
    }

    void Window_SizeChanged(object sender, SizeChangedEventArgs e) => QueuePreferenceSave();

    void Window_StateChanged(object? sender, EventArgs e)
    {
        bool minimizedNow = WindowState == WindowState.Minimized;
        // Restoring from the taskbar is another kind of reopening: back to the stack (brief A.5).
        if (_wasMinimized && !minimizedNow && _mode != "stack") ShowStack();
        _wasMinimized = minimizedNow;
        UpdateVisibleWork();
        QueuePreferenceSave();
    }

    void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateVisibleWork();
        if (!IsVisible) SavePreferences();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SavePreferences();
        _disposed = true;
        ModuleEntry.HubShowing = false;
        _cardPreviews.Stop();
        _cardPreviews.Tick -= CardPreviews_Tick;
        _savePreferences.Stop();
        ModuleEntry.DashboardOpenRequested -= EngineRequestedWorkspace;
        _hub.Working.CollectionChanged -= HubChanged;
        _hub.Asleep.CollectionChanged -= HubChanged;
        _hub.Dispose();
        _workspaceView?.Dispose();
        MainSlot.Content = null;
        if (SettingsSlot.Content is SettingsView view) view.BackRequested -= BackFromSettings;
        SettingsSlot.Content = null;
    }
}
