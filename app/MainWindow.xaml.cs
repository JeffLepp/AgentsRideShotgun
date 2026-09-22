using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

public partial class MainWindow : Window, IDisposable
{
    internal static bool AllowActivationForTests = true;
    readonly HubViewModel _hub = new();
    readonly ObservableCollection<HubEntry> _stackWorking = [];
    readonly ObservableCollection<HubEntry> _stackAsleep = [];
    readonly ObservableCollection<HubEntry> _sidebarAsleep = [];
    readonly DispatcherTimer _cardPreviews;
    readonly DispatcherTimer _savePreferences;
    WorkspaceFullView? _workspaceView;
    SettingsView? _cardSettings;
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
        if (!AllowActivationForTests) ShowActivated = false;
        AppearanceManager.Apply(AppSettingsStore.Current.Theme);
        InitializeComponent();
        StackWorkingList.ItemsSource = _stackWorking;
        StackAsleepList.ItemsSource = _stackAsleep;
        SidebarWorkingList.ItemsSource = _hub.Working;
        SidebarAsleepList.ItemsSource = _sidebarAsleep;
        _hub.Working.CollectionChanged += HubChanged;
        _hub.Asleep.CollectionChanged += HubChanged;
        _hub.ActivityChanged += ShowToday;
        _cardPreviews = new DispatcherTimer(DispatcherPriority.Background) { Interval = HubPreview.Interval() };
        _cardPreviews.Tick += CardPreviews_Tick;
        _savePreferences = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _savePreferences.Tick += (_, _) => { _savePreferences.Stop(); SavePreferences(); };
        ModuleEntry.DashboardOpenRequested += EngineRequestedWorkspace;
        LocationChanged += (_, _) => QueuePreferenceSave();
        Loaded += Window_Loaded;
        ThemeIcon.Dark = AppearanceManager.Dark;
        ThemeButton.ToolTip = ThemeTip();
        AppearanceManager.Changed += ThemeRepainted;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Never empty (brief A.4): a fresh install always has something under Recent to show.
        WorkspaceHome.EnsureScratch();
        RestorePreferences();
        _hub.Refresh();
        UpdateVisibleWork();
    }

    void HubChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildStackLists();

    /// <summary>The Today strip's three numbers and their words, singular when there is one.</summary>
    void ShowToday()
    {
        var (actions, workspaces, commands) = _hub.Today;
        static string Count(int n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        TodayActions.Text = Count(actions);
        TodayActionsLabel.Text = actions == 1 ? "action today" : "actions today";
        TodayWorkspaces.Text = Count(workspaces);
        TodayWorkspacesLabel.Text = workspaces == 1 ? "workspace" : "workspaces";
        TodayCommands.Text = Count(commands);
        TodayCommandsLabel.Text = commands == 1 ? "command" : "commands";
    }

    void RebuildStackLists()
    {
        IEnumerable<HubEntry> Filtered(IEnumerable<HubEntry> source) =>
            _filter.Length == 0 ? source : source.Where(entry => entry.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        Sync(_stackWorking, Filtered(_hub.Working).ToList());
        Sync(_stackAsleep, (_filter.Length > 0 ? Filtered(_hub.Asleep) : Recent()).ToList());
        Sync(_sidebarAsleep, Recent().ToList());
        FilterEmptyText.Visibility = _filter.Length > 0 && _stackWorking.Count + _stackAsleep.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Recent keeps the last few. Every project folder an agent works in gets a workspace, so the
    /// list grew without end - the owner's had over a dozen on 2026-09-22, most untouched for days.
    /// An older one is still one search away, and stays listed while it is the one open.
    /// </summary>
    IEnumerable<HubEntry> Recent() =>
        _hub.Asleep.Where((entry, index) => index < RecentShown || entry.Id == _selectedId);

    const int RecentShown = 8;

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
        CloseCardSettings(animate: false);
        RememberPlacement();
        _mode = "stack";
        SettingsSlot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Collapsed;
        StackRoot.Visibility = Visibility.Visible;
        FilterButton.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        if (!(_stackPlacement ?? (ShellPlacement.DefaultStack() with { Height = 560 })).Restore(this, 320, 480)) { MinWidth = 320; MinHeight = 480; }
        QueuePreferenceSave();
        UpdateVisibleWork();
    }

    internal void ShowWide(string? select)
    {
        CloseCardSettings(animate: false);
        RememberPlacement();
        _mode = "wide";
        StackRoot.Visibility = Visibility.Collapsed;
        SettingsSlot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Visible;
        FilterButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        if (!(_widePlacement ?? ShellPlacement.DefaultWide()).Restore(this, 960, 600)) { MinWidth = 960; MinHeight = 600; }
        string? id = select ?? _selectedId ?? _hub.Working.Concat(_hub.Asleep).Select(entry => entry.Id).FirstOrDefault();
        if (id is not null) SelectWorkspace(id);
        QueuePreferenceSave();
        UpdateVisibleWork();
    }

    internal void SelectWorkspace(string id)
    {
        _selectedId = id;
        _hub.Select(id);
        RebuildStackLists();
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
        CloseCardSettings(animate: false);
        RememberPlacement();
        if (_mode != "settings") _priorMode = _mode;
        bool first = SettingsSlot.Content is not SettingsView;
        if (first)
        {
            var created = new SettingsView();
            created.BackRequested += BackFromSettings;
            SettingsSlot.Content = created;
        }
        _mode = "settings";
        StackRoot.Visibility = Visibility.Collapsed;
        WideRoot.Visibility = Visibility.Collapsed;
        SettingsSlot.Visibility = Visibility.Visible;
        FilterButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        if (!(_widePlacement ?? ShellPlacement.DefaultWide()).Restore(this, 960, 600)) { MinWidth = 960; MinHeight = 600; }
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

    /// <summary>Settings from the tray or the gear: inside the card when the stack is showing
    /// (owner's pick, 2026-09-22), the wide page when a workspace is open.</summary>
    internal void OpenSettings()
    {
        if (_mode is "stack" or "cardsettings") ShowCardSettings(); else ShowSettings();
    }

    // --- settings inside the card: slides in over the stack, the window keeps its size ----------

    internal SettingsView? CardSettings => _cardSettings;

    internal void ShowCardSettings()
    {
        if (_mode is not ("stack" or "cardsettings")) ShowStack();
        if (_mode == "cardsettings") { _cardSettings!.Focus(); return; }
        if (FilterHost.Visibility == Visibility.Visible) ClearFilter();
        if (_cardSettings is null)
        {
            _cardSettings = new SettingsView(card: true);
            _cardSettings.BackRequested += () => CloseCardSettings(animate: true);
            _cardSettings.CardTitleChanged += CardTitleChanged;
            CardSettingsSlot.Content = _cardSettings;
        }
        else if (!_cardSettings.AtCardHome) _cardSettings.ShowCardHome(animate: false);
        _mode = "cardsettings";
        CardSettingsSlot.Visibility = Visibility.Visible;
        StackRoot.IsHitTestVisible = false;
        FilterButton.Visibility = Visibility.Collapsed;
        // The Night tile does its job here, and a page name needs the room.
        ThemeButton.Visibility = Visibility.Collapsed;
        TitleMark.Visibility = Visibility.Hidden;
        CardBackButton.Visibility = Visibility.Visible;
        CardTitleChanged();
        double width = StackRoot.ActualWidth > 0 ? StackRoot.ActualWidth : ActualWidth;
        SlideX(CardSettingsSlot, width, 0, null);
        SlideX(StackRoot, 0, -0.3 * width, null);
        _cardSettings.Focus();
        UpdateVisibleWork();
    }

    void CloseCardSettings(bool animate)
    {
        if (_mode != "cardsettings") return;
        _mode = "stack";
        StackRoot.IsHitTestVisible = true;
        FilterButton.Visibility = Visibility.Visible;
        ThemeButton.Visibility = Visibility.Visible;
        TitleMark.Visibility = Visibility.Visible;
        CardBackButton.Visibility = Visibility.Collapsed;
        TitleText.Text = "Deskweave";
        double width = StackRoot.ActualWidth > 0 ? StackRoot.ActualWidth : ActualWidth;
        if (animate)
        {
            SlideX(StackRoot, -0.3 * width, 0, null);
            SlideX(CardSettingsSlot, 0, width, () => { if (_mode != "cardsettings") CardSettingsSlot.Visibility = Visibility.Collapsed; });
        }
        else
        {
            SlideX(StackRoot, 0, 0, null, instant: true);
            CardSettingsSlot.Visibility = Visibility.Collapsed;
        }
        if (IsKeyboardFocusWithin) SettingsButton.Focus();
        UpdateVisibleWork();
    }

    void CardTitleChanged()
    {
        if (_mode == "cardsettings" && _cardSettings is not null) TitleText.Text = _cardSettings.CardTitle;
    }

    void CardBack_Click(object sender, RoutedEventArgs e) => _cardSettings?.GoBack();

    void SlideX(FrameworkElement element, double from, double to, Action? done, bool instant = false)
    {
        var shift = (TranslateTransform)element.RenderTransform;
        if (instant || !IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = to;
            done?.Invoke();
            return;
        }
        var slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done is not null) slide.Completed += (_, _) => done();
        shift.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    // --- day and night: one tap picks the other; Settings, General, Theme follows Windows again --

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeChoice next = AppearanceManager.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
        AppSettingsStore.Update(s => s with { Theme = next });
    }

    void ThemeRepainted()
    {
        ThemeIcon.Dark = AppearanceManager.Dark;
        ThemeButton.ToolTip = ThemeTip();
    }

    static string ThemeTip() => AppearanceManager.Dark ? "Switch to day" : "Switch to night";

    // --- filter, clicks --------------------------------------------------------------------------

    void WorkingCard_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) TogglePreview(id); }
    void AsleepRow_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) TogglePreview(id); }
    internal void TogglePreview(string id)
    {
        bool expand = _hub.Find(id)?.Expanded == false;
        foreach (HubEntry entry in _hub.Working.Concat(_hub.Asleep)) entry.Expanded = expand && entry.Id == id;
    }
    void Inline_ShowMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HubEntry entry }) ShowWide(entry.Id);
    }
    void SidebarItem_Checked(object sender, RoutedEventArgs e) { if (sender is RadioButton { DataContext: HubEntry entry }) SelectWorkspace(entry.Id); }

    /// <summary>Sleeps one workspace right from its card or sidebar row, in one click (fix list item
    /// 2.2), the same as the workspace page's own Sleep control. Nested inside the card/row's own
    /// Button or RadioButton, which already marks a completed click handled before it can bubble
    /// into WorkingCard_Click or SidebarItem_Checked; e.Handled here is belt and braces.</summary>
    void SleepCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) WorkspaceRuntime.Of(id)?.Dispose();
        e.Handled = true;
    }

    void BeginFilter()
    {
        FilterHost.Visibility = Visibility.Visible;
        FilterBox.Focus();
    }

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (FilterHost.Visibility == Visibility.Visible) ClearFilter();
        else BeginFilter();
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
        bool windowShown = IsLoaded && IsVisible && WindowState != WindowState.Minimized;
        bool shown = windowShown && _mode is not ("settings" or "cardsettings");
        ModuleEntry.HubShowing = windowShown;
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
        if (_disposed || !_cardPreviews.IsEnabled) return;
        // The pace is re-read here rather than kept from where the loop started, so unplugging
        // slows the tiles down and plugging back in speeds them up without a restart.
        _cardPreviews.Interval = HubPreview.Interval();
        if (_capturing) return;
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
                if (frame is not null && ReferenceEquals(WorkspaceRuntime.Of(entry.Id)?.Plane, plane))
                {
                    entry.Preview = frame;
                    entry.PreviewPlane = plane;
                }
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
            if (AllowActivationForTests) Activate();
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
        if (AllowActivationForTests) Activate();
        UpdateVisibleWork();
    }

    // --- preferences -----------------------------------------------------------------------------

    void QueuePreferenceSave()
    {
        if (_restoringPreferences || _disposed || !IsLoaded) return;
        _savePreferences.Stop();
        _savePreferences.Start();
    }

    void RememberPlacement()
    {
        if (_restoringPreferences || !IsLoaded || WindowState != WindowState.Normal) return;
        if (_mode is "stack" or "cardsettings") _stackPlacement = ShellPlacement.Capture(this);
        else _widePlacement = ShellPlacement.Capture(this);
    }

    void SavePreferences()
    {
        if (_restoringPreferences || !IsLoaded) return;
        RememberPlacement();
        if (WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized;
        _ = new ShellPreferences
        {
            Stack = _stackPlacement, Wide = _widePlacement, Maximized = _wasMaximized,
            Mode = _mode switch { "settings" => _priorMode, "cardsettings" => "stack", _ => _mode }, SelectedWorkspace = _selectedId
        }.Save();
    }

    // --- title bar -------------------------------------------------------------------------------

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == "settings") BackFromSettings();
        else if (_mode == "cardsettings") CloseCardSettings(animate: true);
        else OpenSettings();
    }
    void Back_Click(object sender, RoutedEventArgs e) => ShowStack();
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
        if (e.Key != Key.Escape) return;
        if (_mode == "settings") { e.Handled = true; BackFromSettings(); }
        else if (_mode == "cardsettings") { e.Handled = true; _cardSettings?.GoBack(); }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Settings handles its own Escape and raises BackRequested; this window
        // only reacts to Escape outside it, so the two never race for the same key press.
        if (e.Key == Key.Escape && _mode is not ("settings" or "cardsettings"))
        {
            if (FilterHost.Visibility == Visibility.Visible) { ClearFilter(); e.Handled = true; }
            else if (_mode == "wide") { ShowStack(); e.Handled = true; }
            else if (_hub.Working.Concat(_hub.Asleep).FirstOrDefault(entry => entry.Expanded) is { } expanded)
            { TogglePreview(expanded.Id); e.Handled = true; }
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
        AppearanceManager.Changed -= ThemeRepainted;
        _hub.Working.CollectionChanged -= HubChanged;
        _hub.Asleep.CollectionChanged -= HubChanged;
        _hub.ActivityChanged -= ShowToday;
        _hub.Dispose();
        _workspaceView?.Dispose();
        MainSlot.Content = null;
        if (SettingsSlot.Content is SettingsView view) view.BackRequested -= BackFromSettings;
        if (_cardSettings is not null) _cardSettings.CardTitleChanged -= CardTitleChanged;
        CardSettingsSlot.Content = null;
        SettingsSlot.Content = null;
    }
}
