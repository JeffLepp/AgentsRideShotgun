using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Deskweave.AgentWorkspaces;

/// <summary>The dot color and the tone of the words beside it - the same three states used
/// everywhere a workspace shows its status.</summary>
internal enum PeekTone { Working, Attention, Quiet }

/// <summary>One workspace's tab: what it is called, how it is doing, and whether it is the one the
/// card is showing right now.</summary>
internal readonly record struct PeekTab(string Id, string Name, PeekTone Tone, bool Active);

/// <summary>
/// The corner view: a small always-on-top picture of the workspace that is working, in a corner of
/// the owner's own screen. Clicking into the picture is using it, through
/// <see cref="WorkspaceScreenInput"/> - the same input path the workspace page uses, so there is one
/// path to trust whichever view the owner is looking at. This window only shows and reports what the
/// owner did to it; every decision about what that means (what is pinned, what is asked, what gets
/// dropped where) belongs to <see cref="WorkspacePeekHost"/>.
///
/// It never takes focus when it merely appears. <see cref="Window.ShowActivated"/> is false, so it
    /// can appear over an editor without stealing the keystroke the owner was in the middle of;
    /// a deliberate click still activates it normally. The host suppresses automatic fullscreen overlays.
/// </summary>
public partial class WorkspacePeekWindow : Window
{
    static readonly Duration Arriving = new(TimeSpan.FromMilliseconds(220));
    // Motion at most 220 ms: this used to be 420, past the limit every other move in this window
    // keeps to.
    static readonly Duration Leaving = new(TimeSpan.FromMilliseconds(220));

    bool _leaving;
    bool _moving;
    bool _activityRequested;
    bool _activityAnimated;
    bool _hovered;
    double _stackExtra;
    string? _chipPath;
    bool _chipPressed, _chipDragging, _dragInside;
    Point _chipStart;
    WorkspaceScreenInput? _input;
    PeekEdges _dragEdges;
    Rect _dragStart;

    internal WorkspacePeekWindow()
    {
        ShowActivated = false;
        InitializeComponent();
        // Border does not clip its content to its own rounded corners; the live picture would
        // otherwise show square corners poking past the card's radius.
        CardBody.SizeChanged += (_, e) => CardBody.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
        // The hover actions are also how a keyboard user reaches pin/shrink/open/hide: show them
        // whenever focus is anywhere inside the window, not only under the mouse.
        IsKeyboardFocusWithinChanged += (_, _) => UpdateChrome();
        IsVisibleChanged += (_, _) => UpdateActivityAnimation();
        StateChanged += (_, _) => UpdateActivityAnimation();
        Closed += (_, _) => { SetActive(false); CloseEdgeTab(); };
    }

    internal event Action? OpenRequested;
    internal event Action? PinClicked;
    internal event Action? ShrinkClicked;
    internal event Action? HideRequested;
    internal event Action? StopRequested;
    internal event Action? OwnerActed;
    internal event Action<Rect>? Moved;
    internal event Action<Rect>? Resized;
    internal event Action<string[]>? FilesDropped;
    internal event Action? SheetOpenClicked;
    internal event Action? SheetKeepClicked;
    internal event Action? HoverChanged;

    /// <summary>The owner picked another workspace's tab, by mouse or by keyboard.</summary>
    internal event Action<string>? ShowRequested;
    internal event Action? ResumeClicked;
    internal event Action<string>? ChipOpenRequested;

    /// <summary>Whether the owner's pointer is over the card right now. Held, in the policy's terms.</summary>
    internal bool Hovered => _hovered;
    /// <summary>Moving, resizing, or holding a file drag over the card. A drag from Explorer sends no
    /// mouse events, so it never counts as hover; without this the card could tuck away or swap
    /// workspace under the file before it lands.</summary>
    internal bool Manipulating => _moving || _dragEdges != PeekEdges.None || Dropping;
    internal bool Dropping => DropOverlay.Visibility == Visibility.Visible;

    /// <summary>The visible card's own bounding size, back-card peek included but shadow margin
    /// excluded - what <see cref="Configure"/> just laid out and the corner rule should place.</summary>
    internal Size VisibleSize { get; private set; }

    /// <summary>Owner clicks, scrolls and keys on the live picture, through the one input path every
    /// view of a workspace shares. Bound once: the runtime it reads can change from call to call.</summary>
    internal void BindInput(Func<WorkspaceRuntime?> runtime)
    {
        _input?.Dispose();
        _input = new WorkspaceScreenInput(LiveScreen, runtime);
        _input.OwnerActed += () => OwnerActed?.Invoke();
        _input.PoppedOut += ShowPoppedOut;
        LiveScreen.MouseMove -= LiveScreen_PopOutHover;
        LiveScreen.MouseMove += LiveScreen_PopOutHover;
        if (_taskbar is not null) CardBody.Children.Remove(_taskbar);
        _runtime = runtime;
        _taskbar = new WorkspaceTaskbar(runtime, () => _input?.Touch(), 30) { Visibility = Visibility.Collapsed };
        // Above the live picture and the activity line, under the pill, actions, chip, sheet and toast.
        CardBody.Children.Insert(CardBody.Children.IndexOf(ActivityLine) + 1, _taskbar);
        UpdateChrome();
    }

    WorkspaceTaskbar? _taskbar;
    Func<WorkspaceRuntime?>? _runtime;

    /// <summary>The strip, for the gate.</summary>
    internal WorkspaceTaskbar? Taskbar => _taskbar;

    /// <summary>Gives back whatever the input is holding. Called before the window moves to another
    /// workspace or goes away, so no agent is left waiting on a view that stopped looking.</summary>
    internal void HandBackInput() => _input?.Release();

    /// <summary>
    /// How far the window's own bounds reach past the card on every side, reserved for the shadow
    /// so it is not clipped at the window edge. Left transparent and excluded from hit testing, so
    /// a click just outside the visible card lands on the owner's own desktop, not on this window.
    /// </summary>
    internal const double ShadowMargin = 60;

    /// <summary>The band the tab strip takes above the card: a tab and the gap down to the card.</summary>
    internal const double TabBand = 26;

    IReadOnlyList<PeekTab> _tabs = [];
    string _tabKey = "";

    /// <summary>
    /// The tabs above the card, in the order the workspaces started. Conventional tabs rather than a
    /// deeper pile of cards: with three running, all three are named and reachable in one click,
    /// which a stack of replicas stops being past two. One workspace shows no strip - it names itself
    /// on the card's own pill, and a strip with one tab in it is filler. Rebuilt only when the set
    /// really changes, so a preview tick does not throw away the tab the owner is pointing at.
    /// </summary>
    internal void SetTabs(IReadOnlyList<PeekTab> tabs)
    {
        string key = string.Join('\u001f', tabs.Select(t => $"{t.Id}|{t.Name}|{t.Tone}|{t.Active}"));
        if (key == _tabKey) return;
        _tabKey = key;
        _tabs = tabs;
        bool many = tabs.Count > 1;
        TabStrip.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        NameText.Visibility = many ? Visibility.Collapsed : Visibility.Visible;
        TabStrip.Children.Clear();
        if (!many) return;
        foreach (PeekTab tab in tabs) TabStrip.Children.Add(MakeTab(tab));
    }

    System.Windows.Controls.Button MakeTab(PeekTab tab)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new System.Windows.Controls.TextBlock
        {
            Text = tab.Name, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        SetTone(dot, text, tab.Tone);
        var button = new System.Windows.Controls.Button
        {
            Style = (Style)Resources["TabPill"],
            Tag = tab.Id,
            ToolTip = tab.Active ? tab.Name : "Show " + tab.Name,
            Content = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Children = { dot, text },
            },
        };
        if (tab.Active)
        {
            // The tab you are on is made of the same stuff as the card under it and outlined in the
            // accent - the ordinary tab mark. Not a wash of AccentSoftBrush: that is 13% alpha, which
            // reads as selected over a solid panel and as nothing at all over the owner's wallpaper.
            text.FontWeight = FontWeights.SemiBold;
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "InkBrush");
            button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CardBrush");
            button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "AccentBrush");
        }
        AutomationProperties.SetName(button, tab.Active ? tab.Name + ", showing" : "Show " + tab.Name);
        button.Click += (_, _) => ShowRequested?.Invoke(tab.Id);
        return button;
    }

    /// <summary>
    /// Lays out the card, and the tab strip above it once <see cref="SetTabs"/> has more than one.
    /// <see cref="VisibleSize"/> is the card's own bounding size afterward - what the corner rule
    /// should place; <see cref="Place"/> adds the shadow margin around whatever rect it is given.
    /// <paramref name="canGrow"/> is whether a remembered grown size exists to return to - the
    /// shrink/grow button shows while grown, and while small only if there is somewhere to grow back to.
    /// </summary>
    internal void Configure(Size card, bool grown, bool canGrow)
    {
        double extra = TabStrip.Visibility == Visibility.Visible ? TabBand : 0;
        _stackExtra = extra;
        VisibleSize = new Size(card.Width, card.Height + extra);
        FrontCard.Width = card.Width;
        FrontCard.Height = card.Height;
        FrontCard.Margin = new Thickness(ShadowMargin, extra + ShadowMargin, 0, 0);
        TabStrip.Margin = new Thickness(ShadowMargin, ShadowMargin, 0, 0);
        // Share the card's width between the tabs rather than let the strip run off the side of it:
        // a tab that does not fit ellipsizes its name, which is still a tab you can see and click.
        foreach (UIElement child in TabStrip.Children)
            if (child is FrameworkElement tab) tab.MaxWidth = Math.Max(48, card.Width / TabStrip.Children.Count - 4);
        bool showSizeButton = grown || canGrow;
        ShrinkButton.Visibility = showSizeButton ? Visibility.Visible : Visibility.Collapsed;
        if (showSizeButton)
        {
            ShrinkGlyph.SetResourceReference(System.Windows.Shapes.Path.DataProperty, grown ? "Icon.Shrink" : "Icon.Max");
            ShrinkButton.ToolTip = grown ? "Shrink to small" : "Grow back to the last size";
            AutomationProperties.SetName(ShrinkButton, grown
                ? "Shrink the corner view to small" : "Grow the corner view back to its last size");
        }
    }

    /// <summary>Puts the visible card exactly where the corner rule decided, in DIPs on the virtual
    /// desktop. Never animated: sliding across the screen while an agent works would be motion for
    /// its own sake. The window itself grows a shadow margin around this rect.</summary>
    internal void Place(Rect where)
    {
        Width = where.Width + ShadowMargin * 2;
        Height = where.Height + ShadowMargin * 2;
        Left = where.Left - ShadowMargin;
        Top = where.Top - ShadowMargin;
    }

    /// <summary>The front card's own visible rect, in DIPs on the virtual desktop - what settings
    /// should remember, since the window's own bounds grow for both the shadow and a stack that
    /// comes and goes.</summary>
    internal Rect FrontRect => new(Left + FrontCard.Margin.Left, Top + FrontCard.Margin.Top, FrontCard.Width, FrontCard.Height);

    /// <summary>Who is on screen and what they are doing, in the words every other surface uses.</summary>
    internal void Describe(string workspaceName, string message, PeekTone tone)
    {
        NameText.Text = workspaceName;
        WhoText.Text = message;
        DropText.Text = "Copy into " + workspaceName;
        SetTone(StateDot, WhoText, tone);
        AutomationProperties.SetName(Chip, "Open or drag the newest file " + workspaceName + " saved");
    }

    static void SetTone(Ellipse dot, System.Windows.Controls.TextBlock who, PeekTone tone)
    {
        dot.SetResourceReference(Shape.FillProperty, tone switch
        {
            PeekTone.Working => "AccentBrush",
            PeekTone.Attention => "NeedsYouBrush",
            _ => "AsleepBrush",
        });
        who.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            tone == PeekTone.Attention ? "NeedsYouInkBrush" : "MutedInkBrush");
    }

    /// <summary>The whole-screen background: idle's only picture, and in-use's backdrop under the
    /// window patch. The host passes null to blank it - leaving a failed capture on screen is the
    /// caller's decision, made by not calling this at all.</summary>
    internal void ShowFrame(BitmapSource? frame) => LiveScreen.Source = frame;

    /// <summary>
    /// The front window's own capture, laid over the last whole frame at its screen position - the
    /// whole point of not compositing a full frame every in-use tick. <paramref name="x"/> and
    /// <paramref name="y"/> are screen pixels on the background <see cref="ShowFrame"/> last set;
    /// scaled and offset here to match however that background is currently stretched into the card.
    /// Null (idle, or no window yet) hides the patch and leaves the plain background showing.
    /// </summary>
    internal void ShowFramePatch(BitmapSource? patch, int x, int y)
    {
        if (patch is null || LiveScreen.Source is not BitmapSource background
            || CardBody.ActualWidth <= 0 || CardBody.ActualHeight <= 0)
        {
            FrontPatch.Visibility = Visibility.Collapsed;
            FrontPatch.Source = null;
            return;
        }
        // The same UniformToFill math LiveScreen's own Stretch applies: the larger of the two axis
        // scales wins, and the shorter axis is centered rather than letterboxed.
        double scale = Math.Max(CardBody.ActualWidth / background.PixelWidth, CardBody.ActualHeight / background.PixelHeight);
        double left = (CardBody.ActualWidth - background.PixelWidth * scale) / 2;
        double top = (CardBody.ActualHeight - background.PixelHeight * scale) / 2;
        FrontPatch.Source = patch;
        FrontPatch.Width = patch.PixelWidth * scale;
        FrontPatch.Height = patch.PixelHeight * scale;
        FrontPatch.Margin = new Thickness(left + x * scale, top + y * scale, 0, 0);
        FrontPatch.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the patch without touching the background - used when going idle or switching
    /// to a workspace with no picture of its own yet.</summary>
    internal void HideFramePatch()
    {
        FrontPatch.Visibility = Visibility.Collapsed;
        FrontPatch.Source = null;
    }

    /// <summary>The 2 DIP line along the bottom while the agent works, sweeping unless Windows'
    /// animations are off.</summary>
    internal void SetActive(bool active)
    {
        _activityRequested = active;
        ActivityLine.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateActivityAnimation();
    }

    void UpdateActivityAnimation()
    {
        bool animate = _activityRequested && Watching && WindowState != WindowState.Minimized
            && SystemParameters.ClientAreaAnimation;
        if (animate == _activityAnimated) return;
        _activityAnimated = animate;
        if (animate)
        {
            var transform = new TranslateTransform();
            ActivityBrush.RelativeTransform = transform;
            var animation = new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromSeconds(3)))
                { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(animation, 20);
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else if (ActivityBrush.RelativeTransform is TranslateTransform sweep)
            sweep.BeginAnimation(TranslateTransform.XProperty, null);
    }

    /// <summary>Held up, or free to fade when the workspace goes quiet.</summary>
    internal void SetPinned(bool pinned)
    {
        if (pinned)
        {
            PinButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush");
            PinButton.BorderBrush = Brushes.Transparent;
            PinGlyph.SetResourceReference(Shape.StrokeProperty, "OnAccentBrush");
        }
        else
        {
            PinButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "GlassBrush");
            PinButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "GlassEdgeBrush");
            PinGlyph.SetResourceReference(Shape.StrokeProperty, "InkBrush");
        }
        PinButton.ToolTip = pinned ? "Let it fade when the workspace is quiet" : "Keep this on screen";
        AutomationProperties.SetName(PinButton, pinned
            ? "Let the corner view fade when the workspace is quiet" : "Keep the corner view on screen");
    }

    /// <summary>The newest file the agent saved this run, or null to hide the chip.</summary>
    internal void ShowResultChip(string? name, string? path)
    {
        _chipPath = path;
        ChipText.Text = name ?? string.Empty;
        Chip.Visibility = name is not null && DropOverlay.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A pending desktop request as a question, or null while there is none.</summary>
    internal void ShowNeedsYou(string? question)
    {
        Sheet.Visibility = question is null ? Visibility.Collapsed : Visibility.Visible;
        SheetQuestion.Text = question ?? string.Empty;
    }

    internal void ShowUsingToast(string agent)
    {
        Toast.Visibility = Visibility.Visible;
        ToastText.Text = $"You're using it · {agent} waits";
        ToastLink.Visibility = Visibility.Collapsed;
    }

    internal void ShowPausedToast()
    {
        Toast.Visibility = Visibility.Visible;
        ToastText.Text = "Paused";
        ToastLink.Visibility = Visibility.Visible;
    }

    internal void HideToast()
    {
        Toast.Visibility = Visibility.Collapsed;
        ToastLink.Visibility = Visibility.Collapsed;
    }

    internal bool PausedToastVisible => Toast.Visibility == Visibility.Visible
        && ToastText.Text == "Paused" && ToastLink.Visibility == Visibility.Visible;

    internal bool DropHidesChrome => DropOverlay.Visibility == Visibility.Visible
        && Pill.Visibility == Visibility.Collapsed && Actions.Visibility == Visibility.Collapsed
        && Grip.Visibility == Visibility.Collapsed;

    /// <summary>Forces the hover chrome on or off without a real pointer, for photographing it.</summary>
    internal void ForceHoverForTests(bool hovered) => SetHover(hovered);

    /// <summary>Forces the drop-target overlay on or off without a real drag, for photographing it.</summary>
    internal void ForceDropOverlayForTests(bool shown) => SetDropOverlay(shown);

    /// <summary>What a scene should photograph: the visible card (or the stacked pair), cropped out of
    /// a real render of this window so every pixel - live picture, glass, hover chrome - is genuine,
    /// with the shadow's transparent margin left out.</summary>
    internal FrameworkElement PhotographCard()
    {
        UpdateLayout();
        int totalWidth = Math.Max(1, (int)Math.Round(ActualWidth));
        int totalHeight = Math.Max(1, (int)Math.Round(ActualHeight));
        var full = new RenderTargetBitmap(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
        full.Render(this);
        var crop = new CroppedBitmap(full, new Int32Rect((int)Math.Round(ShadowMargin), (int)Math.Round(ShadowMargin),
            Math.Max(1, (int)Math.Round(VisibleSize.Width)), Math.Max(1, (int)Math.Round(VisibleSize.Height))));
        crop.Freeze();
        var image = new System.Windows.Controls.Image
        {
            Source = crop, Width = VisibleSize.Width, Height = VisibleSize.Height, Stretch = Stretch.None,
        };
        // Never added to any tree, so nothing lays it out on its own - measure and arrange it here
        // rather than trust Mvp's later UpdateLayout() call to do that for an orphan element.
        image.Measure(VisibleSize);
        image.Arrange(new Rect(VisibleSize));
        return image;
    }

    void SetHover(bool hovered)
    {
        _hovered = hovered;
        UpdateChrome();
    }

    /// <summary>The hover actions and grip: shown under the mouse, same as always, and now also
    /// while keyboard focus is anywhere inside the window - a keyboard user reaches pin, shrink,
    /// open and hide the same way a mouse user does, not only by pointing at the card.</summary>
    void UpdateChrome()
    {
        bool dropping = DropOverlay.Visibility == Visibility.Visible;
        bool show = (_hovered || IsKeyboardFocusWithin) && !dropping;
        Actions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Pill.Visibility = dropping ? Visibility.Collapsed : Visibility.Visible;
        Chip.Visibility = !dropping && !string.IsNullOrEmpty(ChipText.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        Pill.Margin = new Thickness(show ? 22 : 8, 8, 0, 0);
        // The taskbar comes with the hover chrome: a permanent strip at this size would eat the view.
        bool strip = show && _taskbar is not null && _runtime?.Invoke() is not null
            && AppSettingsStore.Current.AgentScreen == AgentScreenLook.Full && Sheet.Visibility != Visibility.Visible;
        if (_taskbar is not null) _taskbar.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        double lift = strip ? _taskbar!.Height : 0;
        Chip.Margin = new Thickness(8, 0, 0, 9 + lift);
        Toast.Margin = new Thickness(0, 0, 0, 10 + lift);
    }

    void SetDropOverlay(bool shown)
    {
        bool changed = Dropping != shown;
        DropOverlay.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateChrome();
        // The host treats a drag like a hand on the card: it holds on the way in, and the quiet
        // time starts over on the way out.
        if (changed) HoverChanged?.Invoke();
    }

    /// <summary>Fades in, rising a little, from wherever it is now. Interrupts a fade out. No motion
    /// at all with Windows' own animations off.</summary>
    internal void Arrive()
    {
        if (ReturnFromEdge()) return;
        // The host calls this on every preview tick. Only a visibility transition gets motion.
        if (Watching) return;
        bool wasHidden = !IsVisible;
        _leaving = false;
        if (wasHidden) { Opacity = 0; Show(); }
        UpdateActivityAnimation();
        // Another program may have gone topmost since this last appeared - a game, an installer.
        Topmost = true;
        bool animate = SystemParameters.ClientAreaAnimation;
        BeginAnimation(OpacityProperty, animate
            ? new DoubleAnimation(1, Arriving) : new DoubleAnimation(1, new Duration(TimeSpan.Zero)));
        var rise = Root.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Root.RenderTransform = rise;
        rise.BeginAnimation(TranslateTransform.YProperty, animate
            ? new DoubleAnimation(wasHidden ? 10 : rise.Y, 0, Arriving) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(0, new Duration(TimeSpan.Zero)));
    }

    /// <summary>Fades out and hides. Hidden rather than closed: the next activity is seconds away.</summary>
    internal void Leave()
    {
        if (Docked || Sliding) { HideImmediately(); return; }
        _edgeTab?.Hide();
        if (!IsVisible || _leaving) return;
        _leaving = true;
        UpdateActivityAnimation();
        bool animate = SystemParameters.ClientAreaAnimation;
        var fade = new DoubleAnimation(0, animate ? Leaving : new Duration(TimeSpan.Zero));
        fade.Completed += (_, _) =>
        {
            if (!_leaving) return;
            _leaving = false;
            Hide();
            ShowFrame(null);
            HideFramePatch();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Whether it is on screen and not on its way off it.</summary>
    internal bool Watching => IsVisible && !_leaving;

    /// <summary>A game or presentation takes the screen: remove this surface without an exit animation.</summary>
    internal void HideImmediately()
    {
        CancelDock();
        _leaving = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
        ShowFrame(null);
        HideFramePatch();
    }

    /// <summary>
    /// The shadow margin needs real window bounds to render into, but that margin is not the card:
    /// a click there must fall through to whatever is really at that point on the owner's desktop,
    /// not land on this window. HTTRANSPARENT outside the card (or the tab strip) tells Windows to
    /// keep looking; everything else hit-tests normally.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(HitTest);
    }

    nint HitTest(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084, HtTransparent = -1;
        if (message == WmSizing && _dragEdges != PeekEdges.None)
        {
            Sizing(lParam);
            handled = true;
            return 1;
        }
        if (message != WmNcHitTest) return 0;
        long raw = lParam.ToInt64();
        var screen = new Point(unchecked((short)(raw & 0xFFFF)), unchecked((short)((raw >> 16) & 0xFFFF)));
        Point local = PointFromScreen(screen);
        // During a slide, the painted card moves inside a larger, clipped viewport.
        // Keep hit testing attached to the picture, including at adjoining monitors.
        if (DockViewport.Clip is { } clip && !clip.FillContains(local))
        {
            handled = true;
            return HtTransparent;
        }
        local = DockViewport.TranslatePoint(local, Root);
        if (!Within(local, FrontCard) && !(TabStrip.Visibility == Visibility.Visible && Within(local, TabStrip)))
        {
            handled = true;
            return HtTransparent;
        }
        return 0;
    }

    static bool Within(Point point, FrameworkElement element)
    {
        // The card is laid out at an exact size; the strip is as wide as its tabs came out.
        double width = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        double height = double.IsNaN(element.Height) ? element.ActualHeight : element.Height;
        return point.X >= element.Margin.Left && point.X < element.Margin.Left + width
            && point.Y >= element.Margin.Top && point.Y < element.Margin.Top + height;
    }

    /// <summary>
    /// Left and right move between workspaces, and so does Ctrl+Tab, which is what a row of tabs does
    /// everywhere else. Only once the owner has clicked the corner: it never takes focus by appearing,
    /// so it can never swallow a key meant for the window they are actually working in. A key the live
    /// picture already sent into the workspace arrives here handled, and is left alone.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _tabs.Count < 2) return;
        int step = e.Key switch
        {
            Key.Right => 1,
            Key.Left => -1,
            Key.Tab when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) =>
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1,
            _ => 0,
        };
        if (step == 0) return;
        int at = 0;
        for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Active) at = i;
        ShowRequested?.Invoke(_tabs[(at + step + _tabs.Count) % _tabs.Count].Id);
        e.Handled = true;
    }

    void Root_MouseEnter(object sender, MouseEventArgs e) { if (!Sliding && !Docked) { SetHover(true); HoverChanged?.Invoke(); } }
    void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        HidePopOut();
        if (!Sliding && !Docked) { SetHover(false); HoverChanged?.Invoke(); }
    }

    // --- Open on my desktop -------------------------------------------------------------------

    nint _popOutWindow;
    Rect _popOutArea;
    long _popOutAsked;

    /// <summary>
    /// Hovering the picture puts Open on my desktop on the window under the pointer, at that
    /// window's top right, when ARS can start it again on the owner's desktop. Asked at most
    /// a few times a second, and never while a button is held: a drag is its own way out.
    /// </summary>
    async void LiveScreen_PopOutHover(object sender, MouseEventArgs e)
    {
        if (_input is null || e.LeftButton == MouseButtonState.Pressed || DropOverlay.Visibility == Visibility.Visible
            || Sheet.Visibility == Visibility.Visible) { HidePopOut(); return; }
        Point at = e.GetPosition(LiveScreen);
        // Still on the window the button is for: it stays put, whatever the pointer does inside it.
        if (_popOutWindow != 0 && _popOutArea.Contains(at)) return;
        long now = Environment.TickCount64;
        if (now - _popOutAsked < 150) return;
        _popOutAsked = now;
        (nint Window, Rect Area)? found = await _input.PoppableAt(at);
        if (found is not { } hit || !Root.IsMouseOver || Mouse.LeftButton == MouseButtonState.Pressed) { HidePopOut(); return; }
        PlacePopOut(hit.Window, hit.Area);
    }

    /// <summary>The button inside the top right of a window at this place on the picture.</summary>
    void PlacePopOut(nint window, Rect area)
    {
        _popOutWindow = window;
        _popOutArea = area;
        PopOutButton.Visibility = Visibility.Visible;
        PopOutButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = PopOutButton.DesiredSize;
        Point corner = LiveScreen.TranslatePoint(new Point(area.Right, area.Top), CardBody);
        // Inside the window's top right, kept on the card and clear of the pill and the actions row.
        double left = Math.Clamp(corner.X - size.Width - 6, 6, Math.Max(6, CardBody.ActualWidth - size.Width - 6));
        double top = Math.Clamp(corner.Y + 6, 40, Math.Max(40, CardBody.ActualHeight - size.Height - 8));
        PopOutButton.Margin = new Thickness(left, top, 0, 0);
    }

    void HidePopOut()
    {
        PopOutButton.Visibility = Visibility.Collapsed;
        _popOutWindow = 0;
    }

    async void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        nint window = _popOutWindow;
        HidePopOut();
        if (window != 0 && _input is not null) await _input.PopOut(window);
    }

    System.Windows.Threading.DispatcherTimer? _poppedTimer;

    /// <summary>What taking a window out did, for a few seconds, in the card's own toast.</summary>
    void ShowPoppedOut(string said)
    {
        Toast.Visibility = Visibility.Visible;
        ToastText.Text = said;
        ToastLink.Visibility = Visibility.Collapsed;
        _notice = said;
        _poppedTimer?.Stop();
        _poppedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _poppedTimer.Tick += (_, _) =>
        {
            _poppedTimer?.Stop();
            _notice = null;
            if (ToastText.Text == said) HideToast();
        };
        _poppedTimer.Start();
    }

    string? _notice;

    /// <summary>A one-off line in the card's toast for a few seconds, such as a drop that did not copy.</summary>
    internal void ShowNotice(string said) => ShowPoppedOut(said);

    /// <summary>The line <see cref="ShowNotice"/> is showing right now, which the host's refresh leaves up.</summary>
    internal string? Notice => _notice;

    internal bool PopOutButtonVisible => PopOutButton.Visibility == Visibility.Visible;

    /// <summary>The button as hovering a window at this place on the picture shows it, for the gate.</summary>
    internal void ShowPopOutForTests(Rect area)
    {
        UpdateLayout();
        PlacePopOut(1, area);
    }

    void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => MoveWindow();

    /// <summary>Moves the window with the pointer until the button comes up. Only a real move is
    /// reported: a click that went nowhere is not choosing where the card lives.</summary>
    void MoveWindow()
    {
        // Mid-slide the window's bounds are the slide's, and the slide's end puts back the bounds it
        // started from - a drag begun now would be undone under the pointer.
        if (Sliding) return;
        Rect before = FrontRect;
        _moving = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally
        {
            _moving = false;
            if (Differs(FrontRect, before)) Moved?.Invoke(FrontRect);
        }
    }

    /// <summary>Whether a gesture really moved or resized the card. Read back after Windows moved
    /// the window, a DIP rect can come back a fraction off at 125% or 150% without anything moving.</summary>
    static bool Differs(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) >= 1 || Math.Abs(a.Top - b.Top) >= 1
        || Math.Abs(a.Width - b.Width) >= 1 || Math.Abs(a.Height - b.Height) >= 1;

    // --- The top bar --------------------------------------------------------------------------

    bool _barPressed, _barSecondClick;
    Point _barStart;

    /// <summary>
    /// A press on the bar is not yet a move or a click. Past the system's drag distance it becomes
    /// a move, the way a title bar does; let go before that and it is a click on the picture under
    /// the bar, handed to the same input path the rest of the picture uses.
    /// </summary>
    void MoveBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Sliding) return;
        _barPressed = true;
        // A double-click reaches the workspace as one: the top of its screen is where title bars
        // are, and a double-click there is how a window is maximized.
        _barSecondClick = e.ClickCount > 1;
        _barStart = e.GetPosition(MoveBar);
        MoveBar.CaptureMouse();
        e.Handled = true;
    }

    void MoveBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_barPressed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndBarPress(); return; }
        Point now = e.GetPosition(MoveBar);
        if (Math.Abs(now.X - _barStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _barStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        EndBarPress();
        MoveWindow();
        e.Handled = true;
    }

    void MoveBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_barPressed) return;
        EndBarPress();
        // Keys follow the click into the workspace, as they do after a click anywhere else on it.
        if (_input?.Press(e.GetPosition(LiveScreen), rightButton: false, _barSecondClick) == true) Keyboard.Focus(LiveScreen);
        e.Handled = true;
    }

    void MoveBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _input?.Press(e.GetPosition(LiveScreen), rightButton: true);
        e.Handled = true;
    }

    /// <summary>The wheel scrolls the picture under the bar, as it does everywhere else on it.</summary>
    void MoveBar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var wheel = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent };
        LiveScreen.RaiseEvent(wheel);
        e.Handled = true;
    }

    void MoveBar_MouseEnter(object sender, MouseEventArgs e) => HidePopOut();
    void MoveBar_LostMouseCapture(object sender, MouseEventArgs e) => _barPressed = false;

    void EndBarPress()
    {
        _barPressed = false;
        if (MoveBar.IsMouseCaptured) MoveBar.ReleaseMouseCapture();
    }

    void PinButton_Click(object sender, RoutedEventArgs e) => PinClicked?.Invoke();
    void ShrinkButton_Click(object sender, RoutedEventArgs e) => ShrinkClicked?.Invoke();
    void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    void StopMenu_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    void SheetOpen_Click(object sender, RoutedEventArgs e) => SheetOpenClicked?.Invoke();
    void SheetKeep_Click(object sender, RoutedEventArgs e) => SheetKeepClicked?.Invoke();
    void ToastLink_Click(object sender, RoutedEventArgs e) => ResumeClicked?.Invoke();

    void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _chipPressed = true;
        _chipDragging = false;
        _chipStart = e.GetPosition(Chip);
        Chip.CaptureMouse();
        e.Handled = true;
    }

    void Chip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _chipPressed && !_chipDragging;
        _chipPressed = false;
        Chip.ReleaseMouseCapture();
        if (click && _chipPath is { } path) ChipOpenRequested?.Invoke(path);
        e.Handled = true;
    }

    internal void ClickChipForTests()
    {
        if (_chipPath is { } path) ChipOpenRequested?.Invoke(path);
    }

    void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_chipPressed || _chipDragging || e.LeftButton != MouseButtonState.Pressed
            || _chipPath is not { } path || !File.Exists(path)) return;
        Point now = e.GetPosition(Chip);
        if (Math.Abs(now.X - _chipStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _chipStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _chipDragging = true;
        Chip.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(Chip, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
        finally { _chipPressed = false; _chipDragging = false; }
    }

    protected override void OnDragEnter(DragEventArgs e) => UpdateDrop(e);
    protected override void OnDragOver(DragEventArgs e) => UpdateDrop(e);
    // Crossing from one element of the card to the next leaves one and enters the other at once.
    // Only a leave that no enter follows ends the drag, so the hold does not flicker off between them.
    protected override void OnDragLeave(DragEventArgs e)
    {
        _dragInside = false;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (!_dragInside) SetDropOverlay(false); });
    }
    protected override void OnDrop(DragEventArgs e)
    {
        _dragInside = false;
        // Hand the files over while the drag still holds the card, so they go to the workspace it shows.
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            FilesDropped?.Invoke(paths);
        SetDropOverlay(false);
        e.Handled = true;
    }

    void UpdateDrop(DragEventArgs e)
    {
        bool has = e.Data.GetDataPresent(DataFormats.FileDrop);
        _dragInside = has;
        e.Effects = has ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropOverlay(has);
        e.Handled = true;
    }

    void ResizeLeft_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Left);
    void ResizeRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Right);
    void ResizeTop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Top);
    void ResizeBottom_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Bottom);
    void ResizeBottomRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Right | PeekEdges.Bottom);
    void ResizeBottomLeft_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Left | PeekEdges.Bottom);
    void ResizeTopRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Right | PeekEdges.Top);
    void ResizeTopLeft_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginResize(sender, e, PeekEdges.Left | PeekEdges.Top);

    /// <summary>
    /// Hands the drag to Windows' own sizing loop, the way <see cref="MoveWindow"/> hands a move to
    /// DragMove. Resizing by hand meant four separate window changes per mouse move on a layered
    /// window, and any stray loss of capture ended the drag early and put the card back at its
    /// saved size. The loop keeps the pointer for the whole gesture and moves and sizes the window
    /// in one step; WM_SIZING below holds it to 16:10 and to the monitor.
    /// </summary>
    void BeginResize(object sender, MouseButtonEventArgs e, PeekEdges edges)
    {
        if (Sliding || PresentationSource.FromVisual(this) is not HwndSource source) return;
        e.Handled = true;
        Rect before = FrontRect;
        _dragEdges = edges;
        _dragStart = before;
        try
        {
            Mouse.Capture(null);
            ReleaseCapture();
            SendMessage(source.Handle, WmSysCommand, ScSize | SizingEdge(edges), 0);
        }
        finally
        {
            _dragEdges = PeekEdges.None;
            if (Differs(FrontRect, before)) Resized?.Invoke(FrontRect);
        }
    }

    static nint SizingEdge(PeekEdges edges) => edges switch
    {
        PeekEdges.Left => 1,
        PeekEdges.Right => 2,
        PeekEdges.Top => 3,
        PeekEdges.Left | PeekEdges.Top => 4,
        PeekEdges.Right | PeekEdges.Top => 5,
        PeekEdges.Bottom => 6,
        PeekEdges.Left | PeekEdges.Bottom => 7,
        _ => 8,
    };

    /// <summary>
    /// Windows proposes a window rect for where the pointer has got to; this answers with the
    /// card's own rule instead - 16:10, clamped to the monitor it started on - and lays the card out
    /// at that size before the window takes it, so picture and bounds change together.
    /// </summary>
    void Sizing(nint lParam)
    {
        var proposed = Marshal.PtrToStructure<SizingRect>(lParam);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        // The window is the card plus the shadow margin all round and the tab strip above it.
        double left = proposed.Left / sx + ShadowMargin, right = proposed.Right / sx - ShadowMargin;
        double top = proposed.Top / sy + ShadowMargin + _stackExtra, bottom = proposed.Bottom / sy - ShadowMargin;
        var delta = new Vector(
            _dragEdges.HasFlag(PeekEdges.Left) ? left - _dragStart.Left : right - _dragStart.Right,
            _dragEdges.HasFlag(PeekEdges.Top) ? top - _dragStart.Top : bottom - _dragStart.Bottom);
        Rect work = WorkspacePeekPlacement.MonitorFor(_dragStart).WorkArea;
        Rect next = WorkspacePeekPlacement.Resize(_dragStart, _dragEdges, delta, work);
        FrontCard.Width = next.Width;
        FrontCard.Height = next.Height;
        VisibleSize = new Size(next.Width, next.Height + _stackExtra);
        var answer = new SizingRect
        {
            Left = (int)Math.Round((next.Left - ShadowMargin) * sx),
            Top = (int)Math.Round((next.Top - _stackExtra - ShadowMargin) * sy),
            Right = (int)Math.Round((next.Right + ShadowMargin) * sx),
            Bottom = (int)Math.Round((next.Bottom + ShadowMargin) * sy),
        };
        Marshal.StructureToPtr(answer, lParam, false);
    }

    const int WmSysCommand = 0x0112, WmSizing = 0x0214;
    const nint ScSize = 0xF000;

    [StructLayout(LayoutKind.Sequential)] struct SizingRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
