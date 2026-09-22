using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// Settings inside the hub's own card (owner's pick, 2026-09-22): the stack no longer jumps to a
/// 1200 DIP window. The card's home is three quick tiles (Corner window, Pause agents, Night) over
/// one row per category, each with a line saying what is set; a category's page slides in from
/// the right over it, and back slides it away. The pages are the same ones the wide window shows.
/// </summary>
public partial class SettingsView
{
    static readonly Duration SlideTime = TimeSpan.FromMilliseconds(260);

    readonly Dictionary<string, (Button Row, TextBlock Summary)> _homeRows = new(StringComparer.Ordinal);
    ToggleButton? _cornerTile, _pauseTile, _nightTile;

    /// <summary>True while the card shows its home rather than a category's page.</summary>
    internal bool AtCardHome { get; private set; } = true;

    /// <summary>What the host's title bar says: "Settings" at home, else the page's name.</summary>
    internal string CardTitle => AtCardHome ? "Settings" : Titles[Category];

    /// <summary>Raised when <see cref="CardTitle"/> changes.</summary>
    internal event Action? CardTitleChanged;

    void BuildCard()
    {
        SidebarColumn.Width = new GridLength(0);
        Sidebar.Visibility = Visibility.Collapsed;
        PageTitle.Visibility = Visibility.Collapsed;
        PageFrame.Margin = new Thickness(16, 8, 16, 16);
        CardHome.Visibility = Visibility.Visible;

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 14) };
        _cornerTile = Tile("Corner window", "Icon.Pip", null, "Show the corner window");
        _pauseTile = Tile("Pause agents", null, Geometry.Parse("M5.5,3.5 V12.5 M10.5,3.5 V12.5"), "Pause every agent");
        _nightTile = Tile("Night", null, Geometry.Parse("M13.3,9.9 A5.7,5.7 0 1 1 6.1,2.7 A4.5,4.5 0 0 0 13.3,9.9 Z"), "Night");
        _cornerTile.Margin = new Thickness(0, 0, 4, 0);
        _pauseTile.Margin = new Thickness(2, 0, 2, 0);
        _nightTile.Margin = new Thickness(4, 0, 0, 0);
        _cornerTile.Click += (_, _) =>
        {
            bool on = _cornerTile.IsChecked == true;
            Write(s => s with { CornerShow = on ? CornerShow.ComesAndGoes : CornerShow.Off });
        };
        // The corner window owns pausing; it answers through AllPausedChanged, which sets the tile.
        _pauseTile.Click += (_, _) => { _pauseTile.IsChecked = ModuleEntry.AllPaused; ModuleEntry.RequestPauseAll(); };
        _nightTile.Click += (_, _) =>
        {
            bool dark = _nightTile.IsChecked == true;
            Write(s => s with { Theme = dark ? ThemeChoice.Dark : ThemeChoice.Light });
        };
        tiles.Children.Add(_cornerTile);
        tiles.Children.Add(_pauseTile);
        tiles.Children.Add(_nightTile);

        (string Id, string Icon)[] categories = [("general", "Icon.Sliders"), ("agents", "Icon.Agents"), ("accounts", "Icon.Globe"), ("history", "Icon.Shield")];
        var rows = new List<UIElement>();
        foreach (var (id, icon) in categories)
        {
            (Button row, TextBlock summary) = HomeRow(id, icon);
            _homeRows[id] = (row, summary);
            // Set before Group() so a hidden category leaves no hairline behind (see Group).
            if (id == "accounts" && !SettingsFeatures.Accounts) row.Visibility = Visibility.Collapsed;
            rows.Add(row);
        }
        CardHomeContent.Children.Add(tiles);
        CardHomeContent.Children.Add(Group(rows.ToArray()));

        void Paused() => Dispatcher.BeginInvoke(() => { if (_pauseTile is not null) _pauseTile.IsChecked = ModuleEntry.AllPaused; });
        void Repainted() { if (_nightTile is not null) _nightTile.IsChecked = AppearanceManager.Dark; }
        Loaded += (_, _) => { ModuleEntry.AllPausedChanged += Paused; AppearanceManager.Changed += Repainted; Paused(); Repainted(); };
        Unloaded += (_, _) => { ModuleEntry.AllPausedChanged -= Paused; AppearanceManager.Changed -= Repainted; };
    }

    ToggleButton Tile(string label, string? iconKey, Geometry? iconData, string name)
    {
        var icon = new Path();
        icon.SetResourceReference(StyleProperty, "Icon");
        if (iconKey is not null) icon.SetResourceReference(Path.DataProperty, iconKey); else icon.Data = iconData;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 9, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Children = { icon, text } };
        var tile = new ToggleButton { Content = content };
        tile.SetResourceReference(StyleProperty, "QuickTile");
        AutomationProperties.SetName(tile, name);
        return tile;
    }

    (Button Row, TextBlock Summary) HomeRow(string id, string iconKey)
    {
        var icon = new Path { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(Path.DataProperty, iconKey);
        icon.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        var title = Styled(Titles[id], "RowLabel");
        title.TextWrapping = TextWrapping.NoWrap;
        var summary = Styled("", "RowHint");
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, summary } };
        var chevron = new Path { Data = Geometry.Parse("M6,3.5 L10.5,8 L6,12.5"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        chevron.SetResourceReference(StyleProperty, "Icon13");
        chevron.SetResourceReference(TextElement.ForegroundProperty, "MutedInkBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(chevron);
        var row = new Button { Content = grid, Tag = id, Padding = new Thickness(14, 10, 12, 10) };
        row.SetResourceReference(StyleProperty, "RowButton");
        AutomationProperties.SetName(row, Titles[id]);
        row.Click += (_, _) => Show(id);
        return (row, summary);
    }

    /// <summary>The line under each category on the card's home, and the tiles, from the store.</summary>
    void RefreshCardHome(AppSettings settings)
    {
        if (_cornerTile is not null) _cornerTile.IsChecked = settings.CornerShow != CornerShow.Off;
        if (_nightTile is not null) _nightTile.IsChecked = AppearanceManager.Dark;
        if (_homeRows.Count == 0) return;

        string theme = settings.Theme switch { ThemeChoice.Light => "Light", ThemeChoice.Dark => "Dark", _ => "Follows Windows" };
        _homeRows["general"].Summary.Text = (settings.StartWithWindows ? "Starts with Windows" : "Starts when you open it") + " · " + theme;

        (WorkspaceConnections.AgentApp App, string Name)[] agents = [(WorkspaceConnections.AgentApp.ClaudeCode, "Claude Code"), (WorkspaceConnections.AgentApp.Codex, "Codex")];
        string[] connected = agents.Where(a => SettingsActions.ReadAgent(a.App) == AgentState.Connected).Select(a => a.Name).ToArray();
        _homeRows["agents"].Summary.Text = connected.Length > 0 ? string.Join(", ", connected) + " connected" : "No agent connected yet";

        _homeRows["accounts"].Row.Visibility = SettingsFeatures.Accounts ? Visibility.Visible : Visibility.Collapsed;
        _homeRows["accounts"].Summary.Text = "Sign-ins for the agent's browser";
        _homeRows["history"].Summary.Text = "Storage, and deleting Deskweave's data";
    }

    void SlidePageIn()
    {
        bool wasHome = AtCardHome;
        AtCardHome = false;
        PagePanel.Visibility = Visibility.Visible;
        IsHitTestVisibleCardHome(false);
        if (wasHome) Slide(PagePanel, ActualWidth, 0, null);
        Slide(CardHome, 0, -0.3 * ActualWidth, null);
        CardTitleChanged?.Invoke();
        if (IsKeyboardFocusWithin) Dispatcher.BeginInvoke(() => PagePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }

    /// <summary>Back to the card's home: the page slides off to the right.</summary>
    internal void ShowCardHome(bool animate)
    {
        string from = Category;
        AtCardHome = true;
        IsHitTestVisibleCardHome(true);
        if (animate && IsLoaded)
        {
            Slide(CardHome, -0.3 * ActualWidth, 0, null);
            Slide(PagePanel, 0, ActualWidth, () => { if (AtCardHome) PagePanel.Visibility = Visibility.Collapsed; });
        }
        else
        {
            ((TranslateTransform)CardHome.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
            ((TranslateTransform)CardHome.RenderTransform).X = 0;
            PagePanel.Visibility = Visibility.Collapsed;
        }
        CardHome.ScrollToTop();
        CardTitleChanged?.Invoke();
        if (IsKeyboardFocusWithin && _homeRows.TryGetValue(from, out var row)) row.Row.Focus();
    }

    void IsHitTestVisibleCardHome(bool on) => CardHome.IsHitTestVisible = on;

    static void Slide(FrameworkElement element, double from, double to, Action? done)
    {
        var shift = (TranslateTransform)element.RenderTransform;
        if (!SystemParameters.ClientAreaAnimation || !element.IsLoaded)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = to;
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(from, to, SlideTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done is not null) animation.Completed += (_, _) => done();
        shift.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
