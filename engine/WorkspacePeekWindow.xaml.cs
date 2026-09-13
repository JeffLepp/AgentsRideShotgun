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

namespace HiveMind.AgentWorkspaces;

/// <summary>The dot color and the tone of the words beside it - the same three states the reference
/// uses everywhere a workspace shows its status.</summary>
internal enum PeekTone { Working, Attention, Quiet }

/// <summary>
/// The corner view: a small always-on-top picture of the workspace that is working, in a corner of
/// the owner's own screen. Clicking into the picture is using it, through
/// <see cref="WorkspaceScreenInput"/> - the same input path the workspace page uses, so there is one
/// path to trust whichever view the owner is looking at. This window only shows and reports what the
/// owner did to it; every decision about what that means (what is pinned, what is asked, what gets
/// dropped where) belongs to <see cref="WorkspacePeekHost"/>.
///
/// It never takes focus when it merely appears. <see cref="Window.ShowActivated"/> is false, so it
/// can fade in over a full-screen game or an editor without stealing the keystroke the owner was in
/// the middle of; a deliberate click still activates it normally, so keys typed afterward land.
/// </summary>
public partial class WorkspacePeekWindow : Window
{
    static readonly Duration Arriving = new(TimeSpan.FromMilliseconds(220));
    static readonly Duration Leaving = new(TimeSpan.FromMilliseconds(420));

    bool _leaving;
    bool _hovered;
    double _stackExtra;
    string? _chipPath;
    WorkspaceScreenInput? _input;
    PeekEdges _dragEdges;
    Rect _dragStart;
    Point _dragAnchor;

    internal WorkspacePeekWindow()
    {
        ShowActivated = false;
        InitializeComponent();
        // Border does not clip its content to its own rounded corners; the live picture would
        // otherwise show square corners poking past the card's radius.
        CardBody.SizeChanged += (_, e) => CardBody.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
        BackBody.SizeChanged += (_, e) => BackBody.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
    }

    internal event Action? OpenRequested;
    internal event Action? PinClicked;
    internal event Action? ShrinkClicked;
    internal event Action? HideRequested;
    internal event Action? OwnerActed;
    internal event Action<Rect>? Moved;
    internal event Action<Rect>? Resized;
    internal event Action<string[]>? FilesDropped;
    internal event Action? SheetOpenClicked;
    internal event Action? SheetKeepClicked;
    internal event Action? HoverChanged;
    internal event Action? PromoteRequested;
    internal event Action? HandBackClicked;

    /// <summary>Whether the owner's pointer is over the card right now. Held, in the policy's terms.</summary>
    internal bool Hovered => _hovered;

    /// <summary>The visible card's own bounding size, back-card peek included but shadow margin
    /// excluded - what <see cref="Configure"/> just laid out and the corner rule should place.</summary>
    internal Size VisibleSize { get; private set; }

    /// <summary>Where the corner rule should clamp resizing to. Set once by the host.</summary>
    internal Func<Rect>? WorkArea { get; set; }

    /// <summary>Owner clicks, scrolls and keys on the live picture, through the one input path every
    /// view of a workspace shares. Bound once: the runtime it reads can change from call to call.</summary>
    internal void BindInput(Func<WorkspaceRuntime?> runtime)
    {
        _input?.Dispose();
        _input = new WorkspaceScreenInput(LiveScreen, runtime);
        _input.OwnerActed += () => OwnerActed?.Invoke();
    }

    /// <summary>Gives back whatever the input is holding. Called before the window moves to another
    /// workspace or goes away, so no agent is left waiting on a view that stopped looking.</summary>
    internal void HandBackInput() => _input?.HandBack();

    /// <summary>
    /// How far the window's own bounds reach past the card on every side, reserved for the shadow
    /// so it is not clipped at the window edge. Left transparent and excluded from hit testing, so
    /// a click just outside the visible card lands on the owner's own desktop, not on this window.
    /// </summary>
    internal const double ShadowMargin = 60;

    /// <summary>
    /// Lays out the card (and, once a second workspace is busy, the smaller card peeking above it).
    /// <see cref="VisibleSize"/> is the card's own bounding size afterward - what the corner rule
    /// should place; <see cref="Place"/> adds the shadow margin around whatever rect it is given.
    /// <paramref name="canGrow"/> is whether a remembered grown size exists to return to - the
    /// shrink/grow button shows while grown, and while small only if there is somewhere to grow back to.
    /// </summary>
    internal void Configure(Size card, bool hasBack, bool grown, bool canGrow)
    {
        Rect front0 = new(0, 0, card.Width, card.Height);
        double extra = 0;
        if (hasBack)
        {
            Rect back0 = WorkspacePeekPlacement.Back(front0);
            extra = Math.Max(0, -back0.Top);
        }
        _stackExtra = extra;
        VisibleSize = new Size(card.Width, card.Height + extra);
        FrontCard.Width = card.Width;
        FrontCard.Height = card.Height;
        FrontCard.Margin = new Thickness(ShadowMargin, extra + ShadowMargin, 0, 0);
        BackCard.Visibility = hasBack ? Visibility.Visible : Visibility.Collapsed;
        if (hasBack)
        {
            Rect back = WorkspacePeekPlacement.Back(new Rect(0, extra, card.Width, card.Height));
            BackCard.Width = back.Width;
            BackCard.Height = back.Height;
            BackCard.Margin = new Thickness(back.Left + ShadowMargin, back.Top + ShadowMargin, 0, 0);
        }
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
        DropText.Text = "Drop into " + workspaceName;
        SetTone(StateDot, WhoText, tone);
        AutomationProperties.SetName(Chip, "Drag the newest file " + workspaceName + " saved out");
    }

    internal void DescribeBack(string workspaceName, string message, PeekTone tone)
    {
        BackName.Text = workspaceName;
        BackWho.Text = message;
        SetTone(BackDot, BackWho, tone);
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

    /// <summary>The latest picture of the front workspace.</summary>
    internal void ShowFrame(BitmapSource? frame) => LiveScreen.Source = frame;

    internal void ShowBackFrame(BitmapSource? frame) => BackScreen.Source = frame;

    /// <summary>The 2 DIP line along the bottom while the agent works, sweeping unless Windows'
    /// animations are off.</summary>
    internal void SetActive(bool active)
    {
        ActivityLine.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active && SystemParameters.ClientAreaAnimation)
        {
            var transform = new TranslateTransform();
            ActivityBrush.RelativeTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromSeconds(3))) { RepeatBehavior = RepeatBehavior.Forever });
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
        Chip.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
        ChipText.Text = name ?? string.Empty;
    }

    /// <summary>A pending desktop request as a question, or null while there is none.</summary>
    internal void ShowNeedsYou(string? question)
    {
        Sheet.Visibility = question is null ? Visibility.Collapsed : Visibility.Visible;
        SheetQuestion.Text = question ?? string.Empty;
    }

    /// <summary>The dark toast while the owner is using it under Take turns or Full stop. Full stop
    /// carries a "Hand back" link; Take turns does not, since it lets go on its own.</summary>
    internal void ShowToast(string? text, bool handBackLink = false)
    {
        Toast.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        ToastText.Text = text ?? string.Empty;
        ToastLink.Visibility = handBackLink ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Forces the hover chrome on or off without a real pointer, for photographing it.</summary>
    internal void ForceHoverForTests(bool hovered) => SetHover(hovered);

    /// <summary>Forces the drop-target overlay on or off without a real drag, for photographing it.</summary>
    internal void ForceDropOverlayForTests(bool shown) => DropOverlay.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

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
        Actions.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
        Pill.Margin = new Thickness(hovered ? 22 : 8, 8, 0, 0);
    }

    /// <summary>Fades in, rising a little, from wherever it is now. Interrupts a fade out. No motion
    /// at all with Windows' own animations off.</summary>
    internal void Arrive()
    {
        _leaving = false;
        if (!IsVisible) Show();
        // Another program may have gone topmost since this last appeared - a game, an installer.
        Topmost = true;
        bool animate = SystemParameters.ClientAreaAnimation;
        BeginAnimation(OpacityProperty, animate
            ? new DoubleAnimation(1, Arriving) : new DoubleAnimation(1, new Duration(TimeSpan.Zero)));
        var rise = Root.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Root.RenderTransform = rise;
        rise.BeginAnimation(TranslateTransform.YProperty, animate
            ? new DoubleAnimation(10, 0, Arriving) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(0, new Duration(TimeSpan.Zero)));
    }

    /// <summary>Fades out and hides. Hidden rather than closed: the next activity is seconds away.</summary>
    internal void Leave()
    {
        if (!IsVisible || _leaving) return;
        _leaving = true;
        bool animate = SystemParameters.ClientAreaAnimation;
        var fade = new DoubleAnimation(0, animate ? Leaving : new Duration(TimeSpan.Zero));
        fade.Completed += (_, _) =>
        {
            if (!_leaving) return;
            _leaving = false;
            Hide();
            ShowFrame(null);
            ShowBackFrame(null);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Whether it is on screen and not on its way off it.</summary>
    internal bool Watching => IsVisible && !_leaving;

    /// <summary>
    /// The shadow margin needs real window bounds to render into, but that margin is not the card:
    /// a click there must fall through to whatever is really at that point on the owner's desktop,
    /// not land on this window. HTTRANSPARENT outside the card (or the back card) tells Windows to
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
        if (message != WmNcHitTest) return 0;
        long raw = lParam.ToInt64();
        var screen = new Point(unchecked((short)(raw & 0xFFFF)), unchecked((short)((raw >> 16) & 0xFFFF)));
        Point local = PointFromScreen(screen);
        if (!Within(local, FrontCard) && !(BackCard.Visibility == Visibility.Visible && Within(local, BackCard)))
        {
            handled = true;
            return HtTransparent;
        }
        return 0;
    }

    static bool Within(Point point, FrameworkElement card) =>
        point.X >= card.Margin.Left && point.X < card.Margin.Left + card.Width
        && point.Y >= card.Margin.Top && point.Y < card.Margin.Top + card.Height;

    void FrontCard_MouseEnter(object sender, MouseEventArgs e) { SetHover(true); HoverChanged?.Invoke(); }
    void FrontCard_MouseLeave(object sender, MouseEventArgs e) { SetHover(false); HoverChanged?.Invoke(); }

    void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        Moved?.Invoke(FrontRect);
    }

    void BackCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PromoteRequested?.Invoke();

    void PinButton_Click(object sender, RoutedEventArgs e) => PinClicked?.Invoke();
    void ShrinkButton_Click(object sender, RoutedEventArgs e) => ShrinkClicked?.Invoke();
    void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    void SheetOpen_Click(object sender, RoutedEventArgs e) => SheetOpenClicked?.Invoke();
    void SheetKeep_Click(object sender, RoutedEventArgs e) => SheetKeepClicked?.Invoke();
    void ToastLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => HandBackClicked?.Invoke();

    void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _chipPath is not { } path || !File.Exists(path)) return;
        DragDrop.DoDragDrop(Chip, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    protected override void OnDragEnter(DragEventArgs e) => UpdateDrop(e);
    protected override void OnDragOver(DragEventArgs e) => UpdateDrop(e);
    protected override void OnDragLeave(DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;
    protected override void OnDrop(DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            FilesDropped?.Invoke(paths);
        e.Handled = true;
    }

    void UpdateDrop(DragEventArgs e)
    {
        bool has = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = has ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
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

    void BeginResize(object sender, MouseButtonEventArgs e, PeekEdges edges)
    {
        var zone = (UIElement)sender;
        zone.CaptureMouse();
        _dragEdges = edges;
        _dragStart = FrontRect;
        _dragAnchor = ScreenDip();
        zone.MouseMove += ResizeZone_MouseMove;
        zone.MouseLeftButtonUp += ResizeZone_MouseLeftButtonUp;
        e.Handled = true;
    }

    void ResizeZone_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Vector delta = ScreenDip() - _dragAnchor;
        Rect work = WorkArea?.Invoke() ?? FrontRect;
        // The drag is anchored on the front card's own rect (set in BeginResize), so the result must
        // go back through the same offset Configure gave the front card - the stacked back card's
        // peek above it - rather than being placed as if it were the whole visible box itself.
        Rect next = WorkspacePeekPlacement.Resize(_dragStart, _dragEdges, delta, work);
        FrontCard.Width = next.Width;
        FrontCard.Height = next.Height;
        VisibleSize = new Size(next.Width, next.Height + _stackExtra);
        Place(new Rect(next.Left, next.Top - _stackExtra, next.Width, next.Height + _stackExtra));
    }

    void ResizeZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var zone = (UIElement)sender;
        zone.ReleaseMouseCapture();
        zone.MouseMove -= ResizeZone_MouseMove;
        zone.MouseLeftButtonUp -= ResizeZone_MouseLeftButtonUp;
        Resized?.Invoke(FrontRect);
    }

    /// <summary>The cursor, in DIPs, wherever it is on the virtual desktop - independent of this
    /// window's own moving bounds, unlike a position relative to one of its elements.</summary>
    Point ScreenDip()
    {
        GetCursorPos(out NativePoint p);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)] readonly struct NativePoint { public readonly int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
}
