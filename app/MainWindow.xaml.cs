using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

public partial class MainWindow : Window, IDisposable
{
    readonly ObservableCollection<WorkspaceTile> _visible = [];
    readonly Dictionary<string, WorkspaceTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, RuntimeSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _previews;
    readonly DispatcherTimer _savePreferences;
    AgentWorkspacesPanel? _panel;
    string? _selected;
    string? _editingId;
    string _filter = "all";
    bool _focus;
    bool _compact;
    bool _collapsed;
    bool _capturing;
    bool _disposed;
    bool _refreshQueued;
    ShellPlacement? _fullPlacement;
    ShellPlacement? _compactPlacement;
    bool _wasMaximized;
    bool _restoringPreferences = true;
    double _compactHeight = 650;
    long _previewGeneration;

    public MainWindow()
    {
        AppearanceManager.Apply(AppSettingsStore.Current.Theme);
        AppearanceManager.Changed += Repaint;
        InitializeComponent();
        WorkspaceTiles.ItemsSource = _visible;
        WorkspaceTiles.Loaded += (_, _) => UpdateGalleryGeometry();
        _previews = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _previews.Tick += Preview_Tick;
        _savePreferences = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _savePreferences.Tick += SavePreferences_Tick;
        WorkspaceStore.Changed += QueueRefresh;
        WorkspaceRuntime.AttentionChanged += QueueRefresh;
        ModuleEntry.DashboardOpenRequested += EngineRequestedWorkspace;
        LocationChanged += (_, _) => QueuePreferenceSave();
        Loaded += (_, _) => { RestorePreferences(); RefreshWorkspaces(); FitLayout(); };
    }

    public static readonly DependencyProperty PreviewHeightProperty = DependencyProperty.Register(
        nameof(PreviewHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(260d));
    public double PreviewHeight { get => (double)GetValue(PreviewHeightProperty); set => SetValue(PreviewHeightProperty, value); }
    internal bool PreviewLoopRunning => _previews.IsEnabled;
    internal string DisplayMode => _collapsed ? "collapsed" : _compact ? "compact" : "full";

    /// <summary>What Settings > General > Theme does: saves the choice and repaints now.</summary>
    internal void SetTheme(ThemeChoice theme)
    {
        if (_disposed) return;
        AppSettingsStore.Update(settings => settings with { Theme = theme });
        AppearanceManager.Apply(theme);
    }

    // Brushes this window looked up in code rather than binding dynamically.
    void Repaint()
    {
        if (_disposed || FocusNav is null) return;
        RefreshWorkspaces();
        UpdateNav();
        UpdatePin();
        UpdateFocusAction();
    }

    void RestorePreferences()
    {
        ShellPreferences preferences = ShellPreferences.Read();
        _fullPlacement = preferences.Full;
        _compactPlacement = preferences.Compact;
        _wasMaximized = preferences.Maximized;
        _selected = preferences.SelectedWorkspace;
        Topmost = preferences.Topmost;
        UpdatePin();
        _fullPlacement?.Restore(this, 900, 620);
        bool compact = preferences.Mode is "compact" or "collapsed";
        if (compact)
        {
            _compact = true;
            MinWidth = 410; MinHeight = 430;
            Width = 440; Height = 650;
            _compactPlacement?.Restore(this, 410, 430);
            _compactHeight = Height;
            if (preferences.Mode == "collapsed") SetCollapsed(true);
        }
        else if (preferences.Maximized) WindowState = WindowState.Maximized;
        _restoringPreferences = false;
    }

    void QueuePreferenceSave()
    {
        if (_restoringPreferences || _disposed || _savePreferences is null || !IsLoaded) return;
        _savePreferences.Stop();
        _savePreferences.Start();
    }

    void SavePreferences_Tick(object? sender, EventArgs e) { _savePreferences.Stop(); SavePreferences(); }

    void SavePreferences()
    {
        if (_restoringPreferences || !IsLoaded) return;
        if (WindowState == WindowState.Normal && !_collapsed)
        {
            if (_compact) _compactPlacement = ShellPlacement.Capture(this);
            else _fullPlacement = ShellPlacement.Capture(this);
        }
        else if (WindowState == WindowState.Normal && _collapsed)
            _compactPlacement = ShellPlacement.Capture(this) with { Height = _compactHeight };
        if (!_compact && WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized;
        _ = new ShellPreferences
        {
            Full = _fullPlacement, Compact = _compactPlacement, Maximized = _wasMaximized,
            Topmost = Topmost, Mode = DisplayMode, SelectedWorkspace = _selected, Focus = _focus
        }.Save();
    }

    public void RestoreWorkspaceWindow()
    {
        if (_disposed) return;
        if (_collapsed) SetCollapsed(false);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = !_compact && _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        UpdateVisibleWork();
    }

    void QueueRefresh()
    {
        if (_disposed || _refreshQueued || Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!_disposed) RefreshWorkspaces();
        }, DispatcherPriority.Background);
    }

    void RefreshWorkspaces()
    {
        if (_disposed) return;
        try
        {
            IReadOnlyList<StoredWorkspace> records = WorkspaceStore.All();
            var keep = records.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string old in _tiles.Keys.Where(id => !keep.Contains(id)).ToArray()) _tiles.Remove(old);
            foreach (StoredWorkspace workspace in records)
            {
                if (!_tiles.TryGetValue(workspace.Id, out WorkspaceTile? tile))
                {
                    tile = new WorkspaceTile(workspace);
                    _tiles.Add(workspace.Id, tile);
                    tile.Preview = ReadLastFrame(workspace.Id);
                }
                tile.Workspace = workspace;
                UpdateTileState(tile);
                tile.NotifyRecord();
            }
            if (_selected is null || !_tiles.ContainsKey(_selected)) _selected = records.FirstOrDefault()?.Id;
            FocusNav.IsEnabled = _selected is not null;
            if (_focus && _selected is null) ShowOverview();
            ObserveRuntimes();
            ApplyFilter();
            UpdateFocusAction();
            UpdateVisibleWork();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError("Workspaces could not be read. " + ex.Message);
        }
    }

    void ObserveRuntimes()
    {
        foreach (string id in _subscriptions.Keys.ToArray())
            if (!ReferenceEquals(WorkspaceRuntime.Of(id), _subscriptions[id].Runtime))
            {
                _subscriptions[id].Dispose();
                _subscriptions.Remove(id);
            }
        foreach (WorkspaceRuntime runtime in WorkspaceRuntime.Running)
            if (!_subscriptions.ContainsKey(runtime.Id)) _subscriptions.Add(runtime.Id, new(runtime, QueueRefresh));
    }

    void UpdateTileState(WorkspaceTile tile)
    {
        WorkspaceRuntime? runtime = WorkspaceRuntime.Of(tile.Id);
        MissionState state = runtime?.Agent?.State ?? tile.Workspace.Mission;
        tile.Running = runtime is not null;
        tile.NeedsAttention = state is MissionState.NeedsYou or MissionState.Failed or MissionState.Interrupted
            || state == MissionState.Working && runtime is null
            || runtime?.Access?.Handoffs.All.Any(h => h.State == "pending") == true;
        tile.StatusBrush = (Brush)FindResource(tile.NeedsAttention ? "AWWaitingBrush" : tile.Running ? "AWWorkingBrush" : "ShellMutedBrush");
        // Who works here: the agent at the wheel right now, else who the workspace is kept for.
        string driver = runtime?.Access?.Controller ?? string.Empty;
        string kept = WorkspaceHome.Label(tile.Workspace.Agents);
        tile.AgentsText = driver.Length > 0 ? WorkspaceHome.DisplayName(driver)
            : kept.Equals(tile.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : kept;
        tile.AgentsBrush = (Brush)FindResource(driver.Length > 0 ? "ShellAccentBrush" : "ShellMutedBrush");
    }

    void ApplyFilter()
    {
        if (SearchBox is null) return;
        string query = SearchBox.Text.Trim();
        var matching = _tiles.Values.Where(t =>
            (_filter != "running" || t.Running) && (_filter != "attention" || t.NeedsAttention)
            && (query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Workspace.Task.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Retain item instances and the current scroll position on state updates.
        for (int i = _visible.Count - 1; i >= 0; i--) if (!matching.Contains(_visible[i])) _visible.RemoveAt(i);
        for (int i = 0; i < matching.Length; i++)
        {
            if (i < _visible.Count && ReferenceEquals(_visible[i], matching[i])) continue;
            int old = _visible.IndexOf(matching[i]);
            if (old >= 0) _visible.Move(old, i); else _visible.Insert(i, matching[i]);
        }
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchArea.Visibility = !_focus && _tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Visibility = matching.Length == 0 && _tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceScroll.Visibility = matching.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNav();
        UpdateGalleryGeometry();
    }

    void UpdateNav()
    {
        foreach (Button button in new[] { OverviewNav, RunningNav, AttentionNav, FocusNav })
        {
            bool active = button == FocusNav ? _focus : !_focus && button == (_filter == "running" ? RunningNav : _filter == "attention" ? AttentionNav : OverviewNav);
            button.Background = active ? (Brush)FindResource("ShellSelectedBrush") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(active ? "ShellTextBrush" : "ShellMutedBrush");
        }
    }

    void UpdateVisibleWork()
    {
        if (_previews is null || _disposed || FocusScroll is null) return;
        bool shown = IsVisible && WindowState != WindowState.Minimized && !_collapsed;
        FocusScroll.Visibility = shown && _focus && !_compact ? Visibility.Visible : Visibility.Collapsed;
        bool capture = shown && (!_focus || _compact) && WorkspaceRuntime.AnyRunning;
        if (capture)
        {
            if (!_previews.IsEnabled) { _previews.Start(); Preview_Tick(null, EventArgs.Empty); }
        }
        else { _previews.Stop(); _previewGeneration++; }
    }

    async void Preview_Tick(object? sender, EventArgs e)
    {
        if (_capturing || _disposed || !_previews.IsEnabled) return;
        _capturing = true;
        long generation = _previewGeneration;
        try
        {
            foreach (WorkspaceTile tile in _visible.ToArray())
            {
                if (_disposed || !_previews.IsEnabled || generation != _previewGeneration) break;
                // Do not capture tiles below or above the scrolling viewport.
                if (WorkspaceTiles.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement element
                    && element.IsVisible && element.ActualHeight > 0)
                {
                    Rect rect = element.TransformToAncestor(WorkspaceScroll).TransformBounds(new Rect(element.RenderSize));
                    if (rect.Bottom < 0 || rect.Top > WorkspaceScroll.ActualHeight) continue;
                }
                WorkspaceControl? plane = WorkspaceRuntime.Of(tile.Id)?.Plane;
                if (plane is null) continue;
                BitmapSource? frame = await Task.Run(() =>
                {
                    try
                    {
                        BitmapSource? captured = plane.Frame();
                        if (captured is not null && !captured.IsFrozen) captured.Freeze();
                        return captured;
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { return null; }
                });
                if (_disposed || generation != _previewGeneration) break;
                if (!ReferenceEquals(WorkspaceRuntime.Of(tile.Id)?.Plane, plane)
                    || !_tiles.TryGetValue(tile.Id, out WorkspaceTile? currentTile) || !ReferenceEquals(tile, currentTile)) continue;
                if (frame is not null) tile.Preview = frame;
                UpdateTileState(tile);
            }
        }
        // A tile left the tree mid-pass; the next tick tries again.
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        finally { _capturing = false; }
    }

    static BitmapSource? ReadLastFrame(string id)
    {
        try
        {
            string path = WorkspaceStore.LastFrameOf(id);
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 700;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException) { return null; }
    }

    void ShowOverview()
    {
        _focus = false;
        OverviewContent.Visibility = Visibility.Visible;
        FocusScroll.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        PageTitle.Visibility = Visibility.Visible;
        NewButton.Style = (Style)FindResource("PrimaryButton");
        PageTitle.Text = _filter == "running" ? "Running" : _filter == "attention" ? "Needs you" : "Workspaces";
        RefreshWorkspaces();
        QueuePreferenceSave();
    }

    void OpenWorkspace(string id)
    {
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        if (workspace is null) { RefreshWorkspaces(); return; }
        if (_collapsed) SetCollapsed(false);
        if (_compact) SetCompact(false);
        _selected = id;
        ModuleEntry.Selected = id;
        _focus = true;
        RenameStrip.Visibility = Visibility.Collapsed;
        OverviewContent.Visibility = Visibility.Collapsed;
        SearchArea.Visibility = Visibility.Collapsed;
        FocusScroll.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        PageTitle.Visibility = Visibility.Collapsed;
        NewButton.Style = (Style)FindResource("QuietButton");
        PageTitle.Text = workspace.Name;
        FocusNav.IsEnabled = true;
        if (_panel is null)
        {
            _panel = new AgentWorkspacesPanel();
            _panel.WorkspaceChanged += PanelWorkspaceChanged;
            // The standalone shell already owns full, compact and collapsed window modes.
            if (_panel.FindName("FullWindowButton") is Button fullWindow) fullWindow.Visibility = Visibility.Collapsed;
            FocusSlot.Content = _panel;
        }
        else _panel.SetWorkspace(workspace);
        UpdateFocusAction();
        UpdateNav();
        UpdateVisibleWork();
        QueuePreferenceSave();
    }

    void PanelWorkspaceChanged()
    {
        if (_panel is null || _disposed) return;
        _selected = _panel.SelectedWorkspaceId;
        if (_focus && WorkspaceStore.Find(_selected) is { } workspace) PageTitle.Text = workspace.Name;
        QueueRefresh();
        QueuePreferenceSave();
    }

    void UpdateFocusAction()
    {
        if (_panel?.FindName("StartButton") is Button start)
            start.Style = (Style)FindResource(WorkspaceRuntime.Of(_panel.SelectedWorkspaceId) is null ? "PrimaryButton" : "DeskButton");
    }

    void EngineRequestedWorkspace()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            RestoreWorkspaceWindow();
            if (ModuleEntry.Selected is { } id) OpenWorkspace(id);
        });
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (_collapsed) SetCollapsed(false);
        // Four folders and a small record: cheap enough to do inline, so every click is one workspace.
        StoredWorkspace created;
        try { created = WorkspaceStore.Create(NextName()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { SetError("The workspace could not be created. " + ex.Message); return; }
        _filter = "all";
        SearchBox.Clear();
        // A workspace nobody started shows nothing and looks broken, so creating one turns it on.
        StartWorkspace(created.Id);
        ShowOverview();
        WorkspaceScroll.ScrollToBottom();
    }

    string NextName()
    {
        int n = 1;
        while (_tiles.Values.Any(t => t.Name.Equals($"Workspace {n}", StringComparison.OrdinalIgnoreCase))) n++;
        return $"Workspace {n}";
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        string name = RenameBox.Text.Trim();
        string? id = _editingId;
        CancelRename_Click(sender, e);
        if (id is null || name.Length == 0) return;
        try
        {
            if (WorkspaceStore.Update(id, current => current with { Name = name }) is { } workspace && _panel?.SelectedWorkspaceId == id)
                _panel.SetWorkspace(workspace);
            RefreshWorkspaces();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError("The workspace could not be renamed. " + ex.Message); }
    }

    void SetError(string message) => MessageBox.Show(this, message, "Deskweave", MessageBoxButton.OK, MessageBoxImage.Warning);

    void WorkspaceTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { Tag: string id } button) button.ContextMenu = CreateWorkspaceMenu(id);
    }

    internal ContextMenu CreateWorkspaceMenu(string id)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Open workspace", () => OpenWorkspace(id));
        if (WorkspaceRuntime.Of(id) is null) Add("Start computer", () => StartWorkspace(id));
        else Add("Stop computer", () => StopWorkspace(id));
        Add("Rename…", () => RenameWorkspace(id));
        menu.Items.Add(AgentsMenu(id));
        Add("Open folder", () => OpenWorkspaceFolder(id));
        menu.Items.Add(new Separator());
        Add("Delete workspace…", () => DeleteWorkspace(id));
        return menu;
    }

    /// <summary>Which outside agents this workspace takes. One choice, checked where it stands.</summary>
    MenuItem AgentsMenu(string id)
    {
        string rule = WorkspaceStore.Find(id)?.Agents ?? string.Empty;
        var parent = new MenuItem { Header = "Agents" };
        void Choice(string label, bool chosen, Func<string?> pick)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = chosen };
            item.Click += (_, _) => { if (pick() is { } picked) SetAgents(id, picked); };
            parent.Items.Add(item);
        }
        bool folder = WorkspaceHome.IsFolder(rule);
        string claude = WorkspaceHome.Agent("claude-code"), codex = WorkspaceHome.Agent("codex");
        Choice("Just me", rule.Length == 0, () => string.Empty);
        Choice("Any agent", rule == WorkspaceHome.Anyone, () => WorkspaceHome.Anyone);
        Choice(folder ? "Agents in " + WorkspaceHome.Label(rule) : "Agents in a folder…", folder, PickAgentsFolder);
        Choice("Only Claude Code", rule == claude, () => claude);
        Choice("Only Codex", rule == codex, () => codex);
        // An agent Deskweave set a workspace up for keeps its own line, so the menu matches the tile.
        if (!folder && rule.Length > 0 && rule != WorkspaceHome.Anyone && rule != claude && rule != codex)
            Choice("Only " + WorkspaceHome.Label(rule), true, () => rule);
        return parent;
    }

    string? PickAgentsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Agents working in this folder use this workspace" };
        return dialog.ShowDialog(this) == true ? WorkspaceHome.Folder(dialog.FolderName) : null;
    }

    void SetAgents(string id, string rule)
    {
        try { WorkspaceHome.Set(id, rule); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError("That could not be saved. " + ex.Message); }
        RefreshWorkspaces();
    }

    /// <summary>
    /// Connecting is asked for here and nowhere else: no prompt at first run, nothing to sign in to.
    /// Each app gets one entry, and Deskweave picks the workspace when its agent first uses one.
    /// </summary>
    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        (WorkspaceConnections.AgentApp App, string Label)[] apps =
            [(WorkspaceConnections.AgentApp.ClaudeCode, "Claude Code"), (WorkspaceConnections.AgentApp.Codex, "Codex")];
        // The apps' own configuration files are read, never written, and off the UI thread.
        var states = await Task.Run(() => apps.Select(a =>
            (Installed: WorkspaceConnections.IsInstalled(a.App), Connected: WorkspaceConnections.IsConnected(a.App))).ToArray());
        if (_disposed) return;
        var menu = new ContextMenu { PlacementTarget = ConnectNav, Placement = PlacementMode.Right };
        for (int i = 0; i < apps.Length; i++)
        {
            var (app, label) = apps[i];
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = states[i].Connected,
                IsEnabled = states[i].Installed || states[i].Connected };
            System.Windows.Automation.AutomationProperties.SetName(item, (states[i].Connected ? "Disconnect " : "Connect ") + label);
            item.Click += async (_, _) =>
            {
                try { if (await WorkspaceConnections.SetConnected(app, item.IsChecked) is { } why) SetError(why); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException
                    or System.ComponentModel.Win32Exception)
                { SetError(label + " could not be changed. " + ex.Message); }
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy setup for another agent" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(JsonSerializer.Serialize(WorkspaceConnections.AppConfiguration, new JsonSerializerOptions { WriteIndented = true })); }
            catch (System.Runtime.InteropServices.ExternalException) { SetError("The clipboard is busy. Try again."); }
        };
        menu.Items.Add(copy);
        menu.IsOpen = true;
    }

    void RenameWorkspace(string id)
    {
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        if (workspace is null) { RefreshWorkspaces(); return; }
        if (_focus) ShowOverview();
        _editingId = id;
        RenameStrip.Visibility = Visibility.Visible;
        RenameBox.Text = workspace.Name;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void OpenWorkspaceFolder(string id)
    {
        try
        {
            string folder = WorkspaceStore.FolderOf(id);
            if (!Directory.Exists(folder)) { SetError("This workspace folder is no longer on disk."); return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { SetError("The folder could not be opened. " + ex.Message); }
    }

    void DeleteWorkspace(string id)
    {
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        if (workspace is null) { RefreshWorkspaces(); return; }
        if (MessageBox.Show(this, $"Delete {workspace.Name} and all files inside its workspace folder?\n\n"
                + "Its running desktop and active work will stop. This cannot be undone.",
                "Delete workspace", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            // Stop the exact runtime before removing its files. Unrelated workspaces keep running.
            WorkspaceRuntime.Of(id)?.Dispose();
            WorkspaceAccessStore.Write(id, new WorkspaceAccessPolicy());
            WorkspaceAccessStore.Withdraw(id);
            if (_panel?.SelectedWorkspaceId == id)
            {
                _panel.WorkspaceChanged -= PanelWorkspaceChanged;
                _panel.Dispose();
                _panel = null;
                FocusSlot.Content = null;
            }
            if (!WorkspaceStore.Delete(id))
            {
                RefreshWorkspaces();
                SetError("Windows is still holding files in this workspace. Close those files and try again.");
                return;
            }
            if (_selected == id) _selected = null;
            ShowOverview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError("The workspace could not be deleted. " + ex.Message); }
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Rename_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelRename_Click(sender, new RoutedEventArgs()); e.Handled = true; }
    }
    void CancelRename_Click(object sender, RoutedEventArgs e) { _editingId = null; RenameStrip.Visibility = Visibility.Collapsed; NewButton.Focus(); }
    void WorkspaceTile_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) OpenWorkspace(id); }

    void TileStart_Click(object sender, RoutedEventArgs e)
    {
        // Handled, or the tile underneath would also open the workspace.
        e.Handled = true;
        if (sender is Button { Tag: string id }) StartWorkspace(id);
    }

    internal void StartWorkspace(string id)
    {
        if (WorkspaceStore.Find(id) is not { } workspace) { RefreshWorkspaces(); return; }
        try { WorkspaceRuntime.Start(workspace); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { SetError("The computer could not start. " + ex.Message); }
        RefreshWorkspaces();
    }

    internal void StopWorkspace(string id)
    {
        // Keep the last screen, so a stopped workspace is still recognisable in the overview.
        if (_tiles.TryGetValue(id, out WorkspaceTile? tile)) SaveLastFrame(id, tile.Preview);
        WorkspaceRuntime.Of(id)?.Dispose();
        RefreshWorkspaces();
    }

    static void SaveLastFrame(string id, BitmapSource? frame)
    {
        if (frame is null) return;
        try
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(frame));
            using var file = File.Create(WorkspaceStore.LastFrameOf(id));
            png.Save(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
    void Overview_Click(object sender, RoutedEventArgs e) { _filter = "all"; ShowOverview(); }
    void Running_Click(object sender, RoutedEventArgs e) { _filter = "running"; ShowOverview(); }
    void Attention_Click(object sender, RoutedEventArgs e) { _filter = "attention"; ShowOverview(); }
    void Focus_Click(object sender, RoutedEventArgs e) { if (_selected is { } id) OpenWorkspace(id); }
    void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    void Help_Click(object sender, RoutedEventArgs e)
    {
        if (_collapsed) SetCollapsed(false);
        HelpPanel.Visibility = Visibility.Visible;
        FitLayout();
    }
    void CloseHelp_Click(object sender, RoutedEventArgs e) { HelpPanel.Visibility = Visibility.Collapsed; HelpButton.Focus(); }
    void Quit_Click(object sender, RoutedEventArgs e) { if (Application.Current is App app) app.RequestQuit(); }
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePin();
        QueuePreferenceSave();
    }
    void UpdatePin()
    {
        PinButton.Foreground = (Brush)FindResource(Topmost ? "ShellAccentBrush" : "ShellMutedBrush");
        PinButton.ToolTip = Topmost ? "Stop keeping this window on top" : "Keep this window on top";
    }
    void Compact_Click(object sender, RoutedEventArgs e) { if (_collapsed) SetCollapsed(false); SetCompact(!_compact); }
    void Collapse_Click(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    void SetCompact(bool compact)
    {
        if (_collapsed) SetCollapsed(false);
        if (_compact == compact) return;
        if (compact)
        {
            _wasMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) _fullPlacement = ShellPlacement.Capture(this);
            WindowState = WindowState.Normal;
            MinWidth = 410; MinHeight = 430;
            Width = 440; Height = _compactHeight;
            _compactPlacement?.Restore(this, 410, 430);
            _compact = true;
            ShowOverview();
        }
        else
        {
            _compactHeight = Height;
            _compactPlacement = ShellPlacement.Capture(this);
            _compact = false;
            MinWidth = 900; MinHeight = 620;
            Width = 1280; Height = 820;
            _fullPlacement?.Restore(this, 900, 620);
            if (_wasMaximized) WindowState = WindowState.Maximized;
        }
        FitLayout();
        ShowOverview();
        QueuePreferenceSave();
    }

    void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        if (collapsed)
        {
            if (!_compact) SetCompact(true);
            _compactHeight = Height;
            _compactPlacement = ShellPlacement.Capture(this);
            _collapsed = true;
            MinHeight = 50; MaxHeight = 50; Height = 50;
            ResizeMode = ResizeMode.CanMinimize;
            ExpandedBody.Visibility = Visibility.Collapsed;
        }
        else
        {
            _collapsed = false;
            MaxHeight = double.PositiveInfinity; MinHeight = 430; Height = _compactHeight;
            ResizeMode = ResizeMode.CanResize;
            ExpandedBody.Visibility = Visibility.Visible;
            _compactPlacement?.Restore(this, 410, 430);
        }
        CollapseButton.Content = collapsed ? "\uE70D" : "\uE70E";
        CollapseButton.ToolTip = collapsed ? "Expand workspace monitor" : "Collapse to a small bar";
        UpdateVisibleWork();
        QueuePreferenceSave();
    }

    void FitLayout()
    {
        if (Sidebar is null) return;
        Sidebar.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(_compact ? 0 : 56);
        MaximizeButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        MainArea.Margin = _compact ? new Thickness(16,17,2,0) : new Thickness(22,19,8,0);
        if (!_focus) PageTitle.Text = _filter == "running" ? "Running" : _filter == "attention" ? "Needs you" : "Workspaces";
        NewButton.Content = _compact ? "+ New" : "+  New workspace";
        SearchArea.Width = _compact ? 150 : 230;
        PageHeader.Margin = new Thickness(0,0,14,16);
        UpdateGalleryGeometry();
        HelpPanel.Width = Math.Min(510, Math.Max(300, ActualWidth - 40));
        HelpPanel.MaxHeight = Math.Max(150, ActualHeight - 130);
        ApplyFilter();
    }

    void UpdateGalleryGeometry()
    {
        if (WorkspaceTiles is null || MainArea is null) return;
        double width = MainArea.ActualWidth > 0 ? MainArea.ActualWidth : Math.Max(350, Width - (_compact ? 20 : 88));
        int columns = _compact ? 1 : width >= 1120 && _visible.Count >= 3 ? 3 : 2;
        if (FindVisual<UniformGrid>(WorkspaceTiles) is { } grid) grid.Columns = columns;
        double tileWidth = (width - 20 - 14 * columns) / columns;
        PreviewHeight = Math.Clamp(tileWidth * 9 / 16, 165, 360);
    }

    static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    void Window_SizeChanged(object sender, SizeChangedEventArgs e) { FitLayout(); QueuePreferenceSave(); }
    void Window_StateChanged(object? sender, EventArgs e) { UpdateVisibleWork(); QueuePreferenceSave(); }
    void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateVisibleWork();
        if (!IsVisible) SavePreferences();
    }
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (HelpPanel.Visibility == Visibility.Visible) { CloseHelp_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (RenameStrip.Visibility == Visibility.Visible) { CancelRename_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            return;
        }
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && e.Key == Key.F1) { Help_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.M)
        { Compact_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.N) { New_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.F) { if (_collapsed) SetCollapsed(false); ShowOverview(); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.D1) { if (_collapsed) SetCollapsed(false); Overview_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.D2 && _selected is { } id) { OpenWorkspace(id); e.Handled = true; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SavePreferences();
        _disposed = true;
        AppearanceManager.Changed -= Repaint;
        _previewGeneration++;
        _previews.Stop();
        _previews.Tick -= Preview_Tick;
        _savePreferences.Stop();
        _savePreferences.Tick -= SavePreferences_Tick;
        WorkspaceStore.Changed -= QueueRefresh;
        WorkspaceRuntime.AttentionChanged -= QueueRefresh;
        ModuleEntry.DashboardOpenRequested -= EngineRequestedWorkspace;
        foreach (RuntimeSubscription subscription in _subscriptions.Values) subscription.Dispose();
        _subscriptions.Clear();
        if (_panel is not null) { _panel.WorkspaceChanged -= PanelWorkspaceChanged; _panel.Dispose(); }
        FocusSlot.Content = null;
        _panel = null;
    }

    sealed class RuntimeSubscription : IDisposable
    {
        readonly Action _changed;
        public WorkspaceRuntime Runtime { get; }
        public RuntimeSubscription(WorkspaceRuntime runtime, Action changed)
        {
            Runtime = runtime; _changed = changed;
            runtime.Moved += Moved; runtime.DriverChanged += DriverChanged; runtime.Ended += changed;
        }
        void Moved(MissionState _) => _changed();
        void DriverChanged(Driver _) => _changed();
        public void Dispose() { Runtime.Moved -= Moved; Runtime.DriverChanged -= DriverChanged; Runtime.Ended -= _changed; }
    }
}

internal sealed class WorkspaceTile(StoredWorkspace workspace) : INotifyPropertyChanged
{
    public StoredWorkspace Workspace { get; set; } = workspace;
    public string Id => Workspace.Id;
    public string Name => Workspace.Name;
    // The dot is the only on-screen status; screen readers get it in words here.
    public string OpenLabel => "Open workspace " + Name + (NeedsAttention ? ", needs you" : Running ? ", running" : "")
        + (_agents.Length > 0 ? ", " + _agents : "");
    string _agents = "";
    Brush _agentsBrush = Brushes.Gray;
    /// <summary>Who works here, beside the name: the agent driving now, else who the workspace is kept for.</summary>
    public string AgentsText
    {
        get => _agents;
        set { if (_agents == value) return; _agents = value; Changed(nameof(AgentsText)); Changed(nameof(AgentsVisibility)); Changed(nameof(OpenLabel)); }
    }
    public Brush AgentsBrush { get => _agentsBrush; set => Set(ref _agentsBrush, value); }
    public Visibility AgentsVisibility => _agents.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string StartLabel => "Start the computer for " + Name;
    bool _running;
    public bool Running
    {
        get => _running;
        set { if (_running == value) return; _running = value; Changed(nameof(Running)); Changed(nameof(StoppedVisibility)); Changed(nameof(OpenLabel)); }
    }
    // A stopped workspace shows the button that starts it instead of an empty rectangle.
    public Visibility StoppedVisibility => Running ? Visibility.Collapsed : Visibility.Visible;
    public bool NeedsAttention { get; set; }
    Brush _statusBrush = Brushes.LightGray;
    BitmapSource? _preview;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public BitmapSource? Preview { get => _preview; set => Set(ref _preview, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyRecord() { Changed(nameof(Name)); Changed(nameof(OpenLabel)); Changed(nameof(StartLabel)); }
    void Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(property); }
    void Changed(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
