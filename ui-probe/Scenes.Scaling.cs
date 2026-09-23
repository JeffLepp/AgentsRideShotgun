using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// 100%, 125% and 150%, every surface at its minimum size, plus the keyboard and reduced-motion
/// passes MVP_SPEC's "Done means" asks for and earlier validation rounds deferred three times.
///
/// The lever is <see cref="VisualTreeHelper.SetRootDpi"/>: it hands the whole tree the DPI a real
/// 125% or 150% monitor gives it, so layout rounding, glyph metrics and the device-pixel grid are
/// that monitor's. A LayoutTransform would only magnify a 96 DPI layout and prove nothing, and the
/// owner's own display scaling is not this gate's to change. Each surface is photographed at all
/// three onto one sheet under scaling/, but the sheets are not the claim: a sheet proves a human
/// could have seen a defect, and what is asserted below is what a machine decides - text that needs
/// more room than it was given, a control pushed off the side, an element squeezed under its own
/// desired size, a tab stop Tab never reaches, a focus ring that draws no pixels.
/// </summary>
static class ScalingScenes
{
    static readonly double[] Scales = [1, 1.25, 1.5];

    static string Percent(double scale) => (scale * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    internal static async Task Gate()
    {
        string sheets = Path.Combine(Program.Output, "scaling");
        Directory.CreateDirectory(sheets);
        BitmapSource site = Mvp.Load(Path.Combine(Mvp.References(), "sites", "shop.png"));

        LeverCheck();
        await Hub(sheets);
        await WorkspacePage(sheets, site);
        await Settings(sheets);
        await FirstRun(sheets);
        await Keyboard(sheets);
        await ReducedMotion();
        // Last, and read-only: engine/WorkspacePeek* is owned elsewhere this session.
        await Corner(sheets);
    }

    /// <summary>
    /// Everything this file claims rests on one API doing what it says, so it is checked before it
    /// is used rather than assumed: the tree really is laid out at the new DPI, and the numbers
    /// really do change with it.
    /// </summary>
    static void LeverCheck()
    {
        var text = new TextBlock { Text = "Deskweave", FontSize = 13 };
        var window = new Window
        {
            Content = text, Width = 320, Height = 240, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true,
            Left = SceneContext.OffScreen.X, Top = SceneContext.OffScreen.Y,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var seen = new List<double>();
            foreach (double scale in Scales)
            {
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                window.UpdateLayout();
                seen.Add(VisualTreeHelper.GetDpi(text).DpiScaleX);
            }
            Program.Check(seen.SequenceEqual(Scales),
                "SetRootDpi really lays the whole tree out at 100%, 125% and 150%, not a magnified 96 DPI copy");
            Program.Check(Math.Abs(window.ActualWidth - 320) < 0.5,
                "...and a window keeps its size in DIPs across the three, the way a real monitor's scaling works");
        }
        finally { window.Close(); }
    }

    // --- the surfaces -----------------------------------------------------------------------

    static async Task Hub(string sheets)
    {
        var window = Shell();
        try
        {
            Load(window, "blog");
            window.ShowStack();
            Program.Check(ProbeWindow.IsOffScreen(window), "The scaling fixture's stack remains off every monitor after restoring placement");
            await AtEveryScale(window, window, "hub-stack", 320, 480, sheets);

            window.ShowWide("shop");
            Program.Check(ProbeWindow.IsOffScreen(window), "The scaling fixture's wide view remains off every monitor after restoring placement");
            await AtEveryScale(window, window, "hub-wide", 960, 600, sheets);

            // The same two surfaces with a name as long as a real project folder's. Recorded, not
            // claimed: at the 320 minimum a long name pushes the card's own Sleep control off the
            // right of the card, because WorkingCardTemplate's name column (app/MainWindow.xaml,
            // about line 169) is Auto with no MaxWidth, so its CharacterEllipsis never engages and
            // the star column after it has nothing left to give. That file belongs to another
            // session this round; when its owner caps the name, assert becomes true here.
            Load(window, "a-rather-long-workspace-name");
            window.ShowStack();
            await AtEveryScale(window, window, "hub-stack-long-name", 320, 480, sheets, assert: false);
            window.ShowWide("a-rather-long-workspace-name");
            await AtEveryScale(window, window, "hub-wide-long-name", 960, 600, sheets, assert: false);
        }
        finally { window.Dispose(); window.Close(); }

        static void Load(MainWindow window, string second) => window.Hub.LoadFixture(
        [
            new HubEntry("shop") { Name = "shop", Working = true, AgentText = "Claude Code" },
            new HubEntry(second) { Name = second, Working = true, NeedsYou = true, AgentText = "Codex wants you" },
        ],
        [
            new HubEntry("landing-page") { Name = "landing-page", Age = "2h", SidebarAge = "2h ago" },
            new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday" },
        ]);
    }

    static async Task WorkspacePage(string sheets, BitmapSource site)
    {
        var window = Shell();
        try
        {
            window.Hub.LoadFixture([new HubEntry("shop") { Name = "shop", Working = true, AgentText = "Claude Code" }], []);
            window.ShowWide("shop");
            await Task.Delay(200);
            window.OpenWorkspaceView!.LoadFixture("shop", @"C:\code\shop",
                [("Claude Code", true), ("Codex waiting", false)], site,
                [
                    ("10:41:52", "Opened ", "localhost:5173", site),
                    ("10:42:03", "Clicked Add to cart", "", site),
                    ("10:42:09", "Clicked Checkout", "", site),
                ],
                [("checkout.png", "Claude \u00b7 10:42"), ("hero.png", "You \u00b7 10:39"), ("test-notes.md", "Claude \u00b7 10:31")]);
            await AtEveryScale(window, window, "workspace-page", 960, 600, sheets);
        }
        finally { window.Dispose(); window.Close(); }
    }

    static async Task Settings(string sheets)
    {
        using IDisposable flags = SettingsFeatures.AllOnForScenes();
        using IDisposable seams = Program.AgentSeams();
        Program.Agents(_ => AgentState.Connected);
        var window = Shell();
        try
        {
            window.ShowSettings();
            await Task.Delay(200);
            var slot = (ContentControl)window.FindName("SettingsSlot")!;
            var view = (SettingsView)slot.Content;
            foreach (string category in new[] { "general", "agents", "accounts", "history" })
            {
                view.Show(category);
                await Task.Delay(150);
                await AtEveryScale(window, window, "settings-" + category, 960, 600, sheets);
            }
        }
        finally { window.Dispose(); window.Close(); }
    }

    static async Task FirstRun(string sheets)
    {
        using IDisposable seams = Program.AgentSeams();
        Program.Agents(_ => AgentState.Found);
        AppSettings before = AppSettingsStore.Current;
        var window = new FirstRunWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false,
            Left = SceneContext.OffScreen.X, Top = SceneContext.OffScreen.Y,
        };
        try
        {
            window.Show();
            await Task.Delay(300);
            // SizeToContent.Height: 520 wide is both its size and its minimum.
            await AtEveryScale(window, window, "first-run", 520, window.ActualHeight, sheets);

            // Escape is the obvious thing here - the window is answered by closing it either way.
            window.RaiseEvent(new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await Task.Delay(150);
            Program.Check(!window.IsVisible, "Escape closes first launch, the same as its close button");
        }
        finally
        {
            window.Close();
            AppSettingsStore.Update(_ => before);
        }
    }

    static async Task Corner(string sheets)
    {
        // 220 is WorkspacePeekPlacement.MinWidth, the narrowest card the owner can drag it to.
        foreach (double width in new[] { 220.0, 344.0 })
        {
            var window = new WorkspacePeekWindow();
            try
            {
                Size card = WorkspacePeekPlacement.Card(width);
                window.Configure(card, grown: false, canGrow: false);
                window.SetTabs([new PeekTab("shop", "shop", PeekTone.Working, true), new PeekTab("blog", "blog", PeekTone.Quiet, false)]);
                window.Describe("a-rather-long-workspace-name", "Claude Code", PeekTone.Working);
                window.SetActive(true);
                window.ForceHoverForTests(true);
                window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
                window.Arrive();
                await Task.Delay(300);
                // Place's own size, not the card's: the window is the card plus its shadow margin
                // and, with two tabs, the strip above it, and holding it to the card instead would
                // squeeze the hover actions off the side and blame the window for it.
                await AtEveryScale(window, window, "corner-" + width.ToString("F0", CultureInfo.InvariantCulture),
                    window.Width, window.Height, sheets);
            }
            finally { window.Close(); }
        }
    }

    /// <summary>A real hub window, shown: an unshown Window never lays out, so every measurement
    /// below would be zero and every claim would pass without looking at anything.</summary>
    static MainWindow Shell()
    {
        var window = ProbeWindow.OffScreen(new MainWindow());
        window.Show();
        return window;
    }

    // --- the pass itself --------------------------------------------------------------------

    /// <summary>
    /// One surface at its minimum size, at each of the three scalings: set the root DPI, lay it out,
    /// read every fault a machine can read, and keep the three photographs on one sheet.
    /// </summary>
    /// <summary><paramref name="assert"/> false records what was found without claiming it: a
    /// defect whose fix belongs to a file this session must not touch still belongs in the
    /// evidence, and turns into a claim the moment its owner fixes it.</summary>
    static async Task AtEveryScale(Window window, FrameworkElement surface, string name,
        double minWidth, double minHeight, string sheets, bool assert = true)
    {
        var shots = new List<(double Scale, BitmapSource Shot)>();
        try
        {
            foreach (double scale in Scales)
            {
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                // ShowStack/ShowWide restore the owner's saved placement, which is on a monitor.
                // Back off every screen before each pass: nothing here should ever be seen.
                window.Left = SceneContext.OffScreen.X;
                window.Top = SceneContext.OffScreen.Y;
                window.Width = minWidth;
                // A window that sizes to its content has no height of its own to set, and at 125%
                // and 150% it is entitled to a DIP or two more of it.
                bool ownHeight = !window.SizeToContent.HasFlag(SizeToContent.Height);
                if (ownHeight) window.Height = minHeight;
                // A tree that has already been laid out keeps its measurements; a real monitor lays
                // the app out at its own DPI from the start. Throw the old pass away so what is
                // measured below is this DPI's layout and not the last one's.
                foreach (FrameworkElement element in Tree(window)) { element.InvalidateMeasure(); element.InvalidateArrange(); }
                window.UpdateLayout();
                await Task.Delay(120);
                window.UpdateLayout();
                if (assert)
                    Program.Check(window.ActualWidth <= minWidth + 1 && (!ownHeight || window.ActualHeight <= minHeight + 1),
                        $"{name} at {Percent(scale)} still sizes to its {minWidth:F0} DIP minimum"
                            + (ownHeight ? $" by {minHeight:F0}" : " and its own content's height"));
                List<string> faults = Faults(surface, window);
                // Written before the claim: a Check that fails throws, and the run that most needs
                // the numbers is the one that never gets to write them down.
                File.AppendAllLines(Path.Combine(sheets, "faults.txt"),
                    [$"{name} at {Percent(scale)}, {window.ActualWidth:F0}x{window.ActualHeight:F0} DIP: "
                        + (faults.Count == 0 ? "nothing" : faults.Count + " found"), .. faults.Select(f => "    " + f)]);
                if (assert)
                    Program.Check(faults.Count == 0,
                        $"{name} at {Percent(scale)} and its minimum size clips no text, squeezes nothing and pushes no control off the side"
                            + (faults.Count == 0 ? "" : " - " + string.Join("; ", faults.Take(6))));
                shots.Add((scale, Mvp.Photograph(surface, scale)));
            }
        }
        finally
        {
            VisualTreeHelper.SetRootDpi(window, new DpiScale(1, 1));
            window.UpdateLayout();
        }
        Sheet(shots, name, Path.Combine(sheets, name + ".scales.png"));
    }

    /// <summary>The three photographs side by side at their real device-pixel sizes, labelled.</summary>
    static void Sheet(List<(double Scale, BitmapSource Shot)> shots, string name, string path)
    {
        const int gap = 16, strip = 22;
        int width = shots.Sum(s => s.Shot.PixelWidth) + gap * (shots.Count + 1);
        int height = shots.Max(s => s.Shot.PixelHeight) + strip + gap;
        var page = new DrawingVisual();
        var face = new Typeface("Segoe UI");
        using (DrawingContext context = page.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            double x = gap;
            foreach (var (scale, shot) in shots)
            {
                context.DrawText(new FormattedText($"{name} at {Percent(scale)} - {shot.PixelWidth}x{shot.PixelHeight} device pixels",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 12, Brushes.Black, 1), new Point(x, 4));
                context.DrawImage(shot, new Rect(x, strip, shot.PixelWidth, shot.PixelHeight));
                x += shot.PixelWidth + gap;
            }
        }
        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(page);
        Mvp.Save(sheet, path);
    }

    // --- what a machine can decide about a layout -------------------------------------------

    /// <summary>
    /// Every way this tree says it did not fit, in words. Text is asked what it would need and
    /// compared with what it got; a control is asked where it landed and compared with the
    /// viewport it is in. DesiredSize carries the element's own margin and ActualWidth does not, so
    /// the margin comes off before they are compared. Measuring dirties layout, so the caller's
    /// tree is put back at the end.
    /// </summary>
    static List<string> Faults(FrameworkElement root, Window window)
    {
        var faults = new List<string>();
        foreach (FrameworkElement element in Tree(root))
        {
            if (element is TextBlock text && text.ActualWidth > 0 && text.ActualHeight > 0)
            {
                double across = text.Margin.Left + text.Margin.Right, down = text.Margin.Top + text.Margin.Bottom;
                // Trimming is a decision ("C:\...\RedTe…" with the whole path on the tooltip);
                // having no room and no trimming is not.
                if (text.TextTrimming == TextTrimming.None && text.TextWrapping == TextWrapping.NoWrap)
                {
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (text.DesiredSize.Width - across > text.ActualWidth + 0.5)
                        faults.Add($"{Which(text)} \"{Short(text.Text)}\" needs {text.DesiredSize.Width - across:F1} DIP across and has {text.ActualWidth:F1}");
                }
                text.Measure(new Size(text.ActualWidth + across, double.PositiveInfinity));
                if (text.DesiredSize.Height - down > text.ActualHeight + 0.5)
                    faults.Add($"{Which(text)} \"{Short(text.Text)}\" needs {text.DesiredSize.Height - down:F1} DIP down and has {text.ActualHeight:F1}");
                text.InvalidateMeasure();
            }

            // A control the owner is meant to reach, outside the viewport it lives in. Horizontal
            // scrolling is disabled on every scroller in this app, so anything past the right edge
            // is unreachable rather than merely out of sight.
            if (element is Button or TextBox or ComboBox or ToggleButton && element.IsEnabled)
            {
                ScrollViewer? scroll = Ancestor<ScrollViewer>(element);
                Visual box = scroll ?? (Visual)window;
                Rect where = element.TransformToAncestor(box).TransformBounds(new Rect(element.RenderSize));
                double across = scroll?.ViewportWidth ?? window.ActualWidth;
                if (where.Left < -0.5 || where.Right > across + 0.5)
                    faults.Add($"{Which(element)} sits at {where.Left:F1}..{where.Right:F1} across a {across:F1} DIP viewport");
                if (scroll is null && (where.Top < -0.5 || where.Bottom > window.ActualHeight + 0.5))
                    faults.Add($"{Which(element)} sits at {where.Top:F1}..{where.Bottom:F1} down a {window.ActualHeight:F1} DIP window");
            }

            // Squeezed under what it asked for. Named parts and controls only: an unnamed presenter
            // inside a scrolling list is squeezed by design and says nothing about this surface.
            if ((element.Name.Length > 0 || element is Control) && element.RenderSize.Width > 0
                && element.DesiredSize.Width - element.Margin.Left - element.Margin.Right > element.RenderSize.Width + 0.5)
                faults.Add($"{Which(element)} wanted {element.DesiredSize.Width - element.Margin.Left - element.Margin.Right:F1}"
                    + $" DIP across and was given {element.RenderSize.Width:F1}");
        }
        root.UpdateLayout();
        return faults;
    }

    static string Which(FrameworkElement element) =>
        (element.Name.Length > 0 ? element.Name : System.Windows.Automation.AutomationProperties.GetName(element)) is { Length: > 0 } known
            ? known + " (" + element.GetType().Name + ")" : element.GetType().Name;

    static string Short(string? text) =>
        text is null ? "" : text.Length <= 28 ? text : text[..27] + "\u2026";

    static IEnumerable<FrameworkElement> Tree(DependencyObject root)
    {
        if (root is UIElement { Visibility: not Visibility.Visible }) yield break;
        if (root is FrameworkElement self) yield return self;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (FrameworkElement found in Tree(VisualTreeHelper.GetChild(root, i))) yield return found;
    }

    static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    // --- keyboard ---------------------------------------------------------------------------

    /// <summary>
    /// Tab reaches every stop the tree declares, and the focus ring actually draws. The ring is
    /// checked in pixels rather than by reading FocusVisualStyle: a style that resolves to nothing,
    /// or an adorner drawn outside the window, would still read as set. Windows only shows focus
    /// rings once a keyboard has been used, which never happens inside a probe, so the same switch
    /// a Tab press would flip is flipped here - anything else photographs a lie.
    /// </summary>
    static async Task Keyboard(string sheets)
    {
        PropertyInfo cues = typeof(KeyboardNavigation).GetProperty("AlwaysShowFocusVisual",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException(
            "WPF no longer exposes KeyboardNavigation.AlwaysShowFocusVisual, so the focus ring cannot be photographed.");
        object? had = cues.GetValue(null);
        var window = Shell();
        try
        {
            cues.SetValue(null, true);
            window.Hub.LoadFixture(
                [new HubEntry("shop") { Name = "shop", Working = true, AgentText = "Claude Code" }],
                [new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday" }]);
            window.ShowStack();
            window.Width = 320;
            window.Height = 480;
            window.UpdateLayout();

            foreach (double scale in Scales)
            {
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                window.UpdateLayout();

                List<Control> stops = TabWalk(window, "the stack", scale);

                // The ring, in pixels: the same window with and without focus on one control.
                Control target = stops.First(c => c.Name == "SettingsButton");
                window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                System.Windows.Input.Keyboard.ClearFocus();
                window.UpdateLayout();
                BitmapSource blank = Mvp.Photograph(window, scale);
                target.Focus();
                window.UpdateLayout();
                BitmapSource lit = Mvp.Photograph(window, scale);
                Rect ring = target.TransformToAncestor(window).TransformBounds(new Rect(target.RenderSize));
                ring.Inflate(6, 6);
                Program.Check(target.IsKeyboardFocused && Differs(blank, lit, ring, scale) > 40,
                    $"At {Percent(scale)} the focused control draws a visible focus ring around itself");
                Sheet([(scale, lit)], "focus-ring", Path.Combine(sheets, $"focus-ring-{scale * 100:F0}.png"));
            }

            // The other two hub surfaces, at 150% only: which stops Tab reaches is a focus-scope
            // question, and the three scales above already answer whether the scaling changes it.
            window.ShowWide("shop");
            await Task.Delay(200);
            window.UpdateLayout();
            TabWalk(window, "the workspace page", 1.5);
            window.ShowSettings();
            await Task.Delay(300);
            window.UpdateLayout();
            TabWalk(window, "Settings", 1.5);
        }
        finally
        {
            cues.SetValue(null, had);
            window.Dispose();
            window.Close();
        }
    }

    /// <summary>
    /// Tab from the top until it comes back round, against every action on the surface. "Every
    /// action reachable" is the bar (MVP_SPEC, "Done means"), so what is counted is what the owner
    /// can do something with - press, type in, choose from - and not a read-only list that is a tab
    /// stop by WPF's defaults and a dead end to anyone who lands on it.
    /// </summary>
    static List<Control> TabWalk(Window window, string surface, double scale)
    {
        var actions = Tree(window).OfType<Control>()
            .Where(c => c is Button or TextBox or ComboBox or ToggleButton or ListBoxItem or MenuItem)
            .Where(c => c.Focusable && KeyboardNavigation.GetIsTabStop(c) && c.IsEnabled).ToList();
        var reached = new List<FrameworkElement>();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        for (int i = 0; i < actions.Count * 3 + 16; i++)
        {
            // Any focusable element, not only a Control: stopping at the first one that is not a
            // Control cut the walk short and blamed the page for the stops it never got to.
            if (System.Windows.Input.Keyboard.FocusedElement is not FrameworkElement focused) break;
            if (reached.Contains(focused)) break;
            reached.Add(focused);
            focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        List<Control> missed = actions.Except(reached.OfType<Control>()).ToList();
        Program.Check(actions.Count > 0 && missed.Count == 0,
            $"At {Percent(scale)} Tab reaches every one of {surface}'s {actions.Count} actions"
                + (missed.Count == 0 ? "" : " - missed " + string.Join(", ", missed.Take(5).Select(Which))));
        return actions;
    }

    /// <summary>Device pixels inside <paramref name="area"/> (in DIPs) that the two shots disagree on.</summary>
    static int Differs(BitmapSource a, BitmapSource b, Rect area, double scale)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return int.MaxValue;
        byte[] left = Pixels(a), right = Pixels(b);
        int x0 = Math.Max(0, (int)(area.Left * scale)), x1 = Math.Min(a.PixelWidth, (int)Math.Ceiling(area.Right * scale));
        int y0 = Math.Max(0, (int)(area.Top * scale)), y1 = Math.Min(a.PixelHeight, (int)Math.Ceiling(area.Bottom * scale));
        int count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * a.PixelWidth + x) * 4;
                if (left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2]) count++;
            }
        return count;
    }

    static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    // --- reduced motion ---------------------------------------------------------------------

    /// <summary>
    /// Every animation in this app is behind SystemParameters.ClientAreaAnimation, which reads a
    /// Windows setting the gate has no business changing on the owner's PC. It caches, though, and
    /// the cache is per process, so the probe answers the question for itself and puts it back. The
    /// claims then exercise the real paths: with motion off each one must land at its end state
    /// immediately, and with motion on it must not, or the guard is guarding nothing.
    /// </summary>
    static async Task ReducedMotion()
    {
        Type parameters = typeof(SystemParameters);
        Type slot = parameters.GetNestedType("CacheSlot", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SystemParameters no longer caches ClientAreaAnimation by slot.");
        FieldInfo value = parameters.GetField("_clientAreaAnimation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var valid = (BitArray)parameters.GetField("_cacheValid", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        int index = (int)Enum.Parse(slot, "ClientAreaAnimation");
        bool had = SystemParameters.ClientAreaAnimation;
        void Motion(bool on) { value.SetValue(null, on); valid[index] = true; }

        var window = Shell();
        try
        {
            window.ShowSettings();
            // Settings only animates once it is loaded, which takes a dispatcher turn.
            await Task.Delay(300);
            var slotControl = (ContentControl)window.FindName("SettingsSlot")!;
            var view = (SettingsView)slotControl.Content;
            var page = (ContentControl)view.FindName("Page")!;

            // Whether a clock is attached, not what it currently reads: a value sampled a few
            // milliseconds into a 150 ms fade is a race, and this is not.
            Motion(true);
            Program.Check(SystemParameters.ClientAreaAnimation, "The probe can ask Windows' reduced-motion question for itself");
            page.BeginAnimation(UIElement.OpacityProperty, null);
            view.Show("agents");
            Program.Check(page.HasAnimatedProperties, "With animations on, changing a Settings page fades it in");

            Motion(false);
            page.BeginAnimation(UIElement.OpacityProperty, null);
            view.Show("accounts");
            Program.Check(!page.HasAnimatedProperties && page.Opacity == 1,
                "With reduced motion, a Settings page is simply there, at full opacity");

            // The switch knob (SettingsMotion): the template's trigger has already put it where it
            // belongs, so with motion off nothing may shift it away from there.
            view.Show("general");
            window.UpdateLayout();
            // SettingsMotion only animates a switch that is loaded, which is a dispatcher turn away.
            await Task.Delay(300);
            ToggleButton toggle = Tree(view).OfType<ToggleButton>().First(t => t.IsEnabled && SettingsMotion.GetSlide(t));
            var knob = (FrameworkElement)toggle.Template.FindName("Knob", toggle);
            Motion(true);
            knob.RenderTransform = new TranslateTransform();
            toggle.IsChecked = toggle.IsChecked != true;
            Program.Check((knob.RenderTransform as TranslateTransform)?.HasAnimatedProperties == true,
                "With animations on, a switch knob slides to where the switch says");
            Motion(false);
            knob.RenderTransform = new TranslateTransform();
            toggle.IsChecked = toggle.IsChecked != true;
            Program.Check(knob.RenderTransform is TranslateTransform { HasAnimatedProperties: false, X: 0 },
                "With reduced motion, the knob is simply where the switch says, with nothing sliding it there");

            // The corner window's own arrival, read and not touched: engine/WorkspacePeek* belongs
            // to another session, so this asks it a question rather than changing it. The hub has to
            // be out of the way first, the same as the corner gate itself does.
            window.Hide();
            await Task.Delay(200);
            var corner = new WorkspacePeekWindow();
            try
            {
                corner.Configure(WorkspacePeekPlacement.Card(344), grown: false, canGrow: false);
                corner.Describe("shop", "Claude Code", PeekTone.Working);
                corner.SetActive(true);
                corner.Place(new Rect(SceneContext.OffScreen, corner.VisibleSize));
                corner.Arrive();
                await Task.Delay(200);
                Program.Check(corner.Opacity == 1,
                    "With reduced motion, the corner window is simply there instead of fading in");
                corner.Leave();
                await Task.Delay(200);
                Program.Check(corner.Opacity == 0, "...and simply gone instead of fading out");
            }
            finally { corner.Close(); }
        }
        finally
        {
            Motion(had);
            window.Dispose();
            window.Close();
        }
    }
}
