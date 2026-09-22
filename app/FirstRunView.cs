using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// First launch (design/MVP_SPEC.md, Surfaces 5; reference 07-first-launch.png). It shows what
/// Deskweave does before it asks for anything, lists the supported agents found on this PC with
/// their switches already on, and has one button. Nothing reaches an agent's own configuration
/// until Start is pressed: closing the window connects nothing, and there is no second prompt
/// either way. Start is also the consent for agents installed later
/// (<see cref="AppSettings.ConnectAgents"/>), so this screen is never shown again.
/// </summary>
public sealed class FirstRunWindow : Window
{
    internal const string Headline = "Give your agents their own screen";
    internal const string Explanation = "Connect Claude Code or Codex to test apps in a corner window while you use your desktop. Deskweave stays in the background. Restart agent sessions already open to pick it up.";
    internal const string LocalLine = "Deskweave runs only on this PC";
    internal const string NoAgentLine = "No Claude Code or Codex yet. Install one and Deskweave connects it.";
    internal const string StartLabel = "Start";
    internal const string TryAgainLabel = "Try again";
    internal const string ConnectedTitle = "Your agents are connected";
    internal const string RestartLine = "Restart agent sessions already open. Deskweave stays in the tray; open it there for history and settings.";

    /// <summary>The agent apps this screen offers, in reference order, with their tile.</summary>
    static readonly (WorkspaceConnections.AgentApp App, string Letter, Color Tile)[] Candidates =
    [
        (WorkspaceConnections.AgentApp.ClaudeCode, "C", Color.FromRgb(0x8A, 0x5A, 0x44)),
        (WorkspaceConnections.AgentApp.Codex, "X", Color.FromRgb(0x2B, 0x2F, 0x37)),
    ];

    /// <summary>Whether this launch is the first one that has not been answered.</summary>
    internal static bool Needed => !AppSettingsStore.Current.FirstRunDone;

    readonly List<(WorkspaceConnections.AgentApp App, CheckBox Switch, TextBlock Error)> _rows = [];
    readonly HashSet<WorkspaceConnections.AgentApp> _connected = [];
    readonly Button _start;
    readonly Border _illustration;
    readonly System.Windows.Media.Effects.DropShadowEffect _cornerShadow = new()
    {
        // The reference's window shadow: 0 24px 60px rgba(15,23,42,.20), deeper in the dark theme.
        BlurRadius = 60, ShadowDepth = 24, Direction = 270, Color = Color.FromRgb(0x0F, 0x17, 0x2A),
    };
    bool _started;

    public FirstRunWindow()
    {
        Title = "Deskweave";
        Width = 520;
        // As tall as what it says: one agent row or two, or an error line under one.
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        // Deliberately not UseLayoutRounding: a settings row is 51.075 DIP tall (13 and 11.5 DIP
        // text at 1.35 over 9 DIP padding), and rounding each row to a whole pixel walks the card
        // and everything under it away from the reference by a pixel a row.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(FontFamilyProperty, "SkinFontFamily");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "InkBrush");
        SetResourceReference(BackgroundProperty, "WindowBrush");
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
        });

        var body = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        _illustration = Illustration();
        body.Children.Add(_illustration);
        body.Children.Add(Headline22());
        body.Children.Add(Sentence());
        body.Children.Add(AgentCard());
        body.Children.Add(Foot(out _start));

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        rows.RowDefinitions.Add(new RowDefinition());
        FrameworkElement bar = TitleBar();
        Grid.SetRow(bar, 0);
        rows.Children.Add(bar);
        Grid.SetRow(body, 1);
        rows.Children.Add(body);

        var frame = new Border { BorderThickness = new Thickness(1), Child = rows };
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "WindowBrush");
        Content = frame;

        Repaint();
        AppearanceManager.Changed += Repaint;
        Closed += (_, _) => AppearanceManager.Changed -= Repaint;
        // Answered either way: by Start, or by closing it. There is no second prompt (MVP_SPEC,
        // Surfaces 5). Only Start writes to an agent's configuration.
        Closing += (_, _) => AppSettingsStore.Update(s => s with { FirstRunDone = true });
        // Escape is the close button. The window has no title bar of its own to press Alt+F4 on and
        // no other way out from the keyboard, which left first launch the one surface a keyboard
        // could open and not leave.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    void Repaint()
    {
        _illustration.Background = Wall();
        _cornerShadow.Opacity = AppearanceManager.Dark ? 0.55 : 0.20;
    }

    // --- the chrome -------------------------------------------------------------------------

    FrameworkElement TitleBar()
    {
        var grid = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new Path { Width = 15, Height = 15, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(StyleProperty, "MarkIcon");   // the hub's title bar, to the DIP
        grid.Children.Add(mark);

        var name = new TextBlock
        {
            Text = "Deskweave", FontWeight = FontWeights.SemiBold, FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var close = new Button { ToolTip = "Close" };
        close.SetResourceReference(StyleProperty, "CloseCaptionButton");
        AutomationProperties.SetName(close, "Close Deskweave");
        var glyph = new Path();
        glyph.SetResourceReference(StyleProperty, "CaptionIcon");
        glyph.SetResourceReference(Path.DataProperty, "Icon.Close");
        close.Content = glyph;
        close.Click += (_, _) => Close();
        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(caps, true);
        caps.Children.Add(close);
        Grid.SetColumn(caps, 3);
        grid.Children.Add(caps);
        return grid;
    }

    // --- the illustration -------------------------------------------------------------------

    /// <summary>
    /// What Deskweave does, before a word of explanation: the owner's editor with an agent's own
    /// screen beside it in a corner window. Drawn, not photographed, so it costs one small image.
    /// </summary>
    Border Illustration()
    {
        var canvas = new Canvas();

        var code = new Border
        {
            Width = 250, Height = 116, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x24)),
            Padding = new Thickness(12, 14, 12, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 22, ShadowDepth = 8, Direction = 270, Opacity = 0.25, Color = Colors.Black },
        };
        var lines = new StackPanel();
        (double Width, string Fill)[] bars =
        [
            (0.70, "#3A404C"), (0.50, "#5A4A7A"), (0.70, "#3A404C"), (0.40, "#3E5C84"), (0.70, "#3A404C"),
        ];
        for (int i = 0; i < bars.Length; i++)
            lines.Children.Add(new Border
            {
                Height = 5, Width = Math.Round(226 * bars[i].Width, 1), CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, i == 0 ? 0 : 7, 0, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bars[i].Fill)),
            });
        code.Child = lines;
        Canvas.SetLeft(code, 18);
        Canvas.SetTop(code, 16);
        canvas.Children.Add(code);

        var corner = new Border
        {
            Width = 150, Height = 94, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Effect = _cornerShadow,
        };
        corner.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        var inside = new Grid { ClipToBounds = true };
        // The rounded corners are a clip, not a CornerRadius: a Border does not round what is in it.
        inside.Clip = new RectangleGeometry(new Rect(0, 0, 150, 94), 8, 8);
        inside.Children.Add(new Image { Source = Preview, Stretch = Stretch.Fill });
        inside.Children.Add(Pill());
        corner.Child = inside;
        // Right 14, bottom 14 of the 150-tall illustration, whose width is set when it is measured.
        Canvas.SetBottom(corner, 14);
        Canvas.SetRight(corner, 14);
        canvas.Children.Add(corner);
        // A Canvas gives its children no size of its own, and Canvas.Right needs a measured width.
        canvas.SizeChanged += (_, e) =>
        {
            Canvas.SetLeft(corner, e.NewSize.Width - 14 - 150);
            Canvas.SetTop(corner, e.NewSize.Height - 14 - 94);
        };

        var illo = new Border
        {
            Height = 150, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Wall(), Child = canvas, Margin = new Thickness(0, 0, 0, 16),
        };
        illo.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        return illo;
    }

    static BitmapImage? _preview;

    static BitmapImage Preview
    {
        get
        {
            if (_preview is not null) return _preview;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Deskweave;component/Assets/first-run-preview.png");
            // Shown at 150 x 94; decoding the whole fixture would keep four megabytes for it.
            image.DecodePixelWidth = 300;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return _preview = image;
        }
    }

    UIElement Pill()
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
        text.Children.Add(dot);
        var name = new TextBlock
        {
            Text = "shop", FontSize = 9, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        text.Children.Add(name);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2), Child = text,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5, 5, 0, 0),
        };
        pill.SetResourceReference(Border.BackgroundProperty, "GlassBrush");
        pill.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        return pill;
    }

    /// <summary>
    /// The desktop behind the illustration: the reference page's wallpaper, two soft washes over a
    /// flat ground. Built here rather than as a theme token; nothing else in the app draws a desktop.
    /// </summary>
    static Brush Wall()
    {
        bool dark = AppearanceManager.Dark;
        var wall = new DrawingGroup();
        void Wash(string color, double x, double y, double radiusX, double radiusY)
        {
            Color rgb = (Color)ColorConverter.ConvertFromString(color);
            wall.Children.Add(new GeometryDrawing(new RadialGradientBrush
            {
                // CSS fades to transparent, which keeps the same colour at zero alpha; fading to a
                // bare Transparent would drag every pixel between them towards black instead.
                GradientStops = [new GradientStop(rgb, 0), new GradientStop(Color.FromArgb(0, rgb.R, rgb.G, rgb.B), 1)],
                Center = new Point(x, y), GradientOrigin = new Point(x, y),
                RadiusX = radiusX, RadiusY = radiusY,
            }, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }
        wall.Children.Add(new GeometryDrawing(
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#0E1422" : "#E6ECF5")),
            null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        // radial-gradient(1100px 700px at 16% 18%) over the 462 x 150 illustration, and the second
        // at 88% 86%; the stop is CSS's 60%, so the radii below are already six tenths of those.
        // CSS paints the first one it lists last, on top, so these go on in the other order.
        Wash(dark ? "#18264A" : "#D2DDF3", 0.88, 0.86, 900 * 0.6 / 462, 700 * 0.6 / 150);
        Wash(dark ? "#1C2E52" : "#C3D4F1", 0.16, 0.18, 1100 * 0.6 / 462, 700 * 0.6 / 150);
        var brush = new DrawingBrush(wall) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    // --- the words and the agents -----------------------------------------------------------

    static TextBlock Headline22()
    {
        var text = new TextBlock
        {
            Text = Headline, FontSize = 22, FontWeight = FontWeights.SemiBold,
            LineHeight = 26.4, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        Tighten(text, 0.01);
        return text;
    }

    /// <summary>
    /// The reference sets its headings -0.01em. WPF has no letter spacing, so take the same width
    /// out of the line as a whole: at this size it is a fortieth of a pixel per glyph, invisible,
    /// and the line ends where the reference ends it instead of nine pixels further on.
    /// </summary>
    static void Tighten(TextBlock text, double em)
    {
        text.Loaded += (_, _) =>
        {
            if (text.LayoutTransform is ScaleTransform) return;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = text.DesiredSize.Width;
            double taken = em * text.FontSize * Math.Max(0, text.Text.Length - 1);
            if (natural > taken) text.LayoutTransform = new ScaleTransform((natural - taken) / natural, 1);
        };
    }

    static TextBlock Sentence()
    {
        var text = new TextBlock
        {
            Text = Explanation, FontSize = 13.5, LineHeight = 20.25,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        return text;
    }

    /// <summary>
    /// One row per supported agent found on this PC, switch already on. An agent that is not here
    /// has no row, and with none installed there is nothing to switch and Start simply closes.
    /// </summary>
    Border AgentCard()
    {
        var stack = new StackPanel();
        bool first = true;
        foreach (var (app, letter, tile) in Candidates)
        {
            if (SettingsActions.ReadAgent(app) == AgentState.NotInstalled) continue;
            UIElement row = AgentRow(app, letter, tile);
            stack.Children.Add(first ? row : Seam(row));
            first = false;
        }
        if (first) stack.Children.Add(Plain(NoAgentLine));
        var card = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 16) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    /// <summary>The hairline between two rows, drawn in the seam so no row grows by its border.</summary>
    static Border Seam(UIElement row)
    {
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, -1, 0, 0), Child = row };
        line.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        return line;
    }

    static Border Plain(string label)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "RowLabel");
        var row = new Border { Child = text };
        row.SetResourceReference(StyleProperty, "SettingRow");
        return row;
    }

    UIElement AgentRow(WorkspaceConnections.AgentApp app, string letter, Color tileColor)
    {
        string name = WorkspaceConnections.DisplayName(app);
        var tile = new ContentControl { Content = letter, Background = new SolidColorBrush(tileColor), Margin = new Thickness(0, 0, 12, 0) };
        tile.SetResourceReference(StyleProperty, "LetterTile");

        var found = new TextBlock { Text = "Found on this PC" };
        found.SetResourceReference(StyleProperty, "RowHint");
        var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(StyleProperty, "RowError");

        var label = new TextBlock { Text = name };
        label.SetResourceReference(StyleProperty, "RowLabel");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(label);
        text.Children.Add(found);
        text.Children.Add(error);

        // On by default: the owner came here to connect what he has, not to pick from a list.
        var toggle = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(toggle, "Connect " + name);
        _rows.Add((app, toggle, error));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(tile);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        var row = new Border { Child = grid };
        row.SetResourceReference(StyleProperty, "SettingRow");
        return row;
    }

    FrameworkElement Foot(out Button start)
    {
        var padlock = new Path { VerticalAlignment = VerticalAlignment.Center };
        padlock.SetResourceReference(StyleProperty, "Icon14");
        padlock.SetResourceReference(Path.DataProperty, "Icon.Lock");
        padlock.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        var local = new TextBlock { Text = LocalLine, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        local.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(padlock);
        left.Children.Add(local);

        start = new Button { Content = StartLabel, FontSize = 13, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        start.SetResourceReference(StyleProperty, "PrimaryButton");
        start.Click += Start;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(start, 1);
        grid.Children.Add(left);
        grid.Children.Add(start);
        return grid;
    }

    // --- Start ------------------------------------------------------------------------------

    /// <summary>
    /// The one button: it is the consent and the connection in a single press. Every switched-on
    /// agent gets Deskweave in its own configuration, replacing an entry from an earlier launch
    /// rather than adding a second. An agent that refuses says so on its own row and the window
    /// stays open; the ones that worked are connected and stay connected.
    /// </summary>
    async void Start(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        _start.IsEnabled = false;
        foreach (var (_, toggle, error) in _rows) { toggle.IsEnabled = false; error.Visibility = Visibility.Collapsed; }
        // Consent first: it is what the press means, and it is what connects an agent installed
        // later even if the one on this PC refuses right now.
        AppSettingsStore.Update(s => s with { ConnectAgents = true, FirstRunDone = true });
        // A switch left off is the owner saying no to that agent: remembered, so nothing connects
        // it later either. The ones left on are remembered by the connection itself.
        foreach (var (app, toggle, _) in _rows)
            if (toggle.IsChecked != true) WorkspaceConnections.Remember(app, false);
        foreach (var (app, toggle, error) in _rows)
        {
            if (toggle.IsChecked != true || !_connected.Add(app)) continue;
            if (await SettingsActions.Connect(app, true) is not { } why) continue;
            _connected.Remove(app);
            error.Text = why;
            error.Visibility = Visibility.Visible;
        }
        WorkspaceConnections.KeepUp();
        // Agents read their tools when a session starts, so one already open has not heard of
        // Deskweave yet. Said once, where it cannot clutter a screen: the tray's notification.
        if (_connected.Count > 0 && Application.Current is App deskweave) deskweave.Tell(ConnectedTitle, RestartLine);
        if (_rows.All(row => row.Error.Visibility != Visibility.Visible)) { Close(); return; }
        // Recovery, not a dead end: one more try for the rows that said why, their switch back so
        // the owner can leave one out instead, and closing still keeps whatever did connect.
        foreach (var (_, toggle, error) in _rows) toggle.IsEnabled = error.Visibility == Visibility.Visible;
        _started = false;
        _start.Content = TryAgainLabel;
        _start.IsEnabled = true;
    }
}
