using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

public partial class WorkspaceWidget : UserControl, IDisposable
{
    /// <summary>
    /// One frame a second, half the panel's rate. A card is a thumbnail on a page nobody is driving
    /// through, and the capture blocks this thread while the workspace's own pump prints its
    /// windows, so the dashboard buys the slower one.
    /// </summary>
    static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1);

    IReadOnlyList<WorkspaceCard> _cards = [];
    DispatcherTimer? _frames;
    int _workspaceCount;
    int _columns = 1;

    public WorkspaceWidget()
    {
        InitializeComponent();
        Loaded += WorkspaceWidget_Loaded;
        IsVisibleChanged += WorkspaceWidget_IsVisibleChanged;
        WorkspaceRuntime.AttentionChanged += AttentionChanged;
        WorkspaceStore.Changed += StoreChanged;
    }

    void WorkspaceWidget_Loaded(object sender, RoutedEventArgs e) => ShowStoredWorkspaces();

    void WorkspaceWidget_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) ShowStoredWorkspaces();
        else StopFrames();
    }

    void AttentionChanged() => Dispatcher.BeginInvoke(() => { if (IsVisible) ShowStoredWorkspaces(); });

    /// <summary>
    /// A workspace was created, deleted or cleared somewhere else - the panel, nearly always. The
    /// cards are read off the disk rather than kept, so this is the only thing that takes a deleted
    /// workspace off the dashboard. Without it, one deleted in the panel stayed on the cards until
    /// HiveMind was restarted.
    /// </summary>
    void StoreChanged() => Dispatcher.BeginInvoke(() => { if (IsVisible) ShowStoredWorkspaces(); });

    /// <summary>The real stored workspaces, read from disk every time the dashboard shows them.</summary>
    void ShowStoredWorkspaces()
    {
        IReadOnlyList<StoredWorkspace> stored = WorkspaceStore.All();
        _workspaceCount = stored.Count;
        _cards = stored.Select(workspace => new WorkspaceCard(Card(workspace))).ToList();
        WorkspaceList.ItemsSource = _cards;
        EmptyState.Visibility = stored.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardLayout(WorkspaceList.ActualWidth, WorkspaceList.ActualHeight);
        UpdateFrames();
    }

    // No size on the card. A card does not show one, and asking for it walked every file in every
    // workspace on the dashboard's own thread - measured at 825 ms with one workspace of 2000 files
    // in it, for a string nothing ever draws.
    //
    // The runtime is asked as well as the record, because a workspace can now be running with no
    // panel anywhere. Until it was, a card called a working workspace asleep.
    /// <summary>What a card would say about one workspace right now. Only the probes call it.</summary>
    internal string CardLineForTests(string id) =>
        WorkspaceStore.Find(id) is { } stored ? Card(stored).MainAgent : "no such workspace";

    static DemoWorkspace Card(StoredWorkspace workspace)
    {
        WorkspaceRuntime? live = WorkspaceRuntime.Of(workspace.Id);
        bool working = live?.Agent?.State == MissionState.Working || live?.Access?.HasDriver == true;
        bool requests = live?.Access?.Handoffs.All.Any(r => r.State == "pending") == true;
        // An outcome the owner has not seen yet outranks "Running": a finished, failed, interrupted
        // or waiting-on-you mission is the whole reason to look at the card, and until this the card
        // said "Running" over a workspace whose mission had stopped hours ago.
        MissionState outcome = live?.Agent?.State is { } running && running != MissionState.Idle
            ? running : workspace.Mission;
        bool tell = !working && WorkspaceMissions.WorthTelling(outcome);
        return new(
            workspace.Id,
            workspace.Name,
            requests ? "Desktop request · Needs you"
                : working ? live?.Access?.Controller is { Length: > 0 } controller ? controller + " is working" : "The agent is working"
                : tell ? WorkspaceMissions.Word(outcome)
                    + (workspace.MissionAt is { } when ? " · " + when.LocalDateTime.ToString("d MMM HH:mm") : "")
                    + (outcome == MissionState.Interrupted ? " · open it to resume" : "")
                : workspace.WakeAt is { } waking ? $"Waiting until {waking.LocalDateTime:d MMM HH:mm}"
                : live is not null ? "Running"
                : "No agent yet",
            string.IsNullOrWhiteSpace(workspace.Task) ? "No mission assigned" : workspace.Task,
            requests ? WorkspaceVisualState.Waiting
                : working ? WorkspaceVisualState.Working
                : tell ? outcome == MissionState.Done ? WorkspaceVisualState.Ready : WorkspaceVisualState.Waiting
                : workspace.WakeAt is not null ? WorkspaceVisualState.Waiting
                : live is not null ? WorkspaceVisualState.Ready
                : WorkspaceVisualState.Sleeping,
            live is null ? "Stored" : "Running",
            workspace.Name,
            $"Last used {workspace.LastUsed.LocalDateTime:d MMM HH:mm}");
    }

    // ---- the live picture -----------------------------------------------------
    //
    // A running workspace shows what is actually on its screen instead of the drawn stand-in. A
    // sleeping one has no desktop to photograph, so it keeps the drawing - which is the whole of
    // what the card could ever say about it.

    /// <summary>Starts or stops the capture. Nothing running means no timer at all.</summary>
    void UpdateFrames()
    {
        if (!IsVisible || !_cards.Any(card => Running(card.Id)))
        {
            StopFrames();
            return;
        }
        if (_frames is null)
        {
            _frames = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
            _frames.Tick += DrawFrames;
            _frames.Start();
        }
        DrawFrames(null, EventArgs.Empty);
    }

    void DrawFrames(object? sender, EventArgs e)
    {
        if (!OnScreen()) return;
        foreach (WorkspaceCard card in _cards) card.Screen = Capture(card.Id);
    }

    void StopFrames()
    {
        if (_frames is not null)
        {
            _frames.Stop();
            _frames.Tick -= DrawFrames;
            _frames = null;
        }
        foreach (WorkspaceCard card in _cards) card.Screen = null;
    }

    static bool Running(string id) => WorkspaceRuntime.Of(id)?.Computer is not null;

    static BitmapSource? Capture(string id)
    {
        AgentDesktop? computer = WorkspaceRuntime.Of(id)?.Computer;
        if (computer is null) return null;
        // Stopped between the look-up and the blit. The card falls back to its drawing.
        try { return computer.CaptureScreen(); }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>
    /// Whether these cards are actually being looked at. IsVisible is not enough on its own: opening
    /// an app fades the dashboard to nothing rather than collapsing it, so a widget behind an open
    /// panel still calls itself visible - and capturing for it costs a whole-screen blit a second
    /// for a picture that is on top of nothing anyone can see.
    /// </summary>
    bool OnScreen()
    {
        if (!IsVisible) return false;
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Opacity: < 0.05 }) return false;
        return true;
    }

    void WorkspaceList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCardLayout(e.NewSize.Width, e.NewSize.Height);

    void UpdateCardLayout(double width, double height)
    {
        int columns = WorkspaceWidgetLayoutPolicy.ChooseColumns(width, height, _workspaceCount);
        if (columns == _columns) return;
        _columns = columns;
        WorkspaceList.ItemsPanel = (ItemsPanelTemplate)FindResource($"Cards{NumberName(columns)}");
    }

    static string NumberName(int value) => value switch
    {
        2 => "Two",
        3 => "Three",
        _ => "One"
    };

    void Workspace_Click(object sender, RoutedEventArgs e)
    {
        // Which card was clicked decides which workspace the panel opens on.
        if (sender is FrameworkElement { DataContext: WorkspaceCard card })
            ModuleEntry.Selected = card.Id;
        ModuleEntry.RequestDashboardOpen();
    }

    /// <summary>
    /// The right-click menu on the block. It used to be a + in a header the cards have taken back;
    /// a menu on the surface itself costs no space at all and works over a card as well as beside
    /// one, because the menu is found by walking up from whatever was clicked.
    /// </summary>
    void NewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // Create raises the store's own change notice, which is what puts the card on screen.
        StoredWorkspace created = WorkspaceStore.Create($"Workspace {WorkspaceStore.All().Count + 1}");
        ModuleEntry.Selected = created.Id;
        ModuleEntry.RequestDashboardOpen();
    }

    public void Dispose()
    {
        Loaded -= WorkspaceWidget_Loaded;
        IsVisibleChanged -= WorkspaceWidget_IsVisibleChanged;
        WorkspaceRuntime.AttentionChanged -= AttentionChanged;
        WorkspaceStore.Changed -= StoreChanged;
        StopFrames();
        // Still no file watcher, network request or background task. The card list is read on
        // demand, so a workspace created elsewhere appears the next time it is shown, and the one
        // timer here exists only while a running workspace is on a dashboard somebody is looking at.
    }
}

/// <summary>
/// One dashboard card: the drawn view of a stored workspace, and - while that workspace is running -
/// the picture of its own screen. The view is a record and never changes; the picture changes every
/// second, which is why a card is a class with a notification rather than another field on the
/// record shared with the panel, the full window and the demo catalog.
/// </summary>
public sealed class WorkspaceCard(DemoWorkspace view) : INotifyPropertyChanged
{
    BitmapSource? _screen;

    public string Id => view.Id;
    public string Name => view.Name;
    public string MainAgent => view.MainAgent;
    public string Task => view.Task;
    public WorkspaceVisualState State => view.State;
    public string StateLabel => view.StateLabel;
    public string StateDetail => view.StateDetail;
    public string FrameHeading => view.FrameHeading;
    public string FrameDetail => view.FrameDetail;
    public string AccessibleName => view.AccessibleName;

    /// <summary>The workspace's screen as of the last capture, or null when it is not running.</summary>
    public BitmapSource? Screen
    {
        get => _screen;
        set
        {
            if (ReferenceEquals(_screen, value)) return;
            _screen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Screen)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Chooses the card grid that yields the largest readable 16:9 preview area.</summary>
public static class WorkspaceWidgetLayoutPolicy
{
    /// <summary>
    /// What a card costs around its picture, in each direction: the button's own margin and
    /// padding, and nothing else. It was 42 while a card carried a name, a state label and a
    /// mission line under the preview - which is most of why three workspaces used to be worth
    /// squeezing into one row, and are not any more.
    /// </summary>
    const double CardChrome = 10;

    const double PreviewAspect = 16.0 / 9.0;

    public static int ChooseColumns(double width, double height, int itemCount)
    {
        if (itemCount <= 1 || width <= 0 || height <= 0) return 1;

        int bestColumns = 1;
        double bestArea = -1;
        for (int columns = 1; columns <= Math.Min(3, itemCount); columns++)
        {
            int rows = (int)Math.Ceiling(itemCount / (double)columns);
            double cellWidth = Math.Max(1, width / columns - CardChrome);
            double previewHeight = Math.Max(1, height / rows - CardChrome);
            double previewWidth = Math.Min(cellWidth, previewHeight * PreviewAspect);
            double area = previewWidth * Math.Min(previewHeight, previewWidth / PreviewAspect);
            if (area <= bestArea) continue;
            bestArea = area;
            bestColumns = columns;
        }

        return bestColumns;
    }
}
