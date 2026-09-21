using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>The owner's mouse and keyboard on a picture of a workspace screen.</summary>
public sealed class WorkspaceScreenInput : IDisposable
{
    internal static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan StayDelay = TimeSpan.FromSeconds(60);

    readonly Image _screen;
    readonly Func<WorkspaceRuntime?> _runtime;
    readonly DispatcherTimer _quiet;
    Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;
    WorkspaceControl? _held;
    DateTimeOffset? _deadline;
    bool _hovering;

    public WorkspaceScreenInput(Image screen, Func<WorkspaceRuntime?> runtime)
    {
        _screen = screen;
        _runtime = runtime;
        screen.Focusable = true;
        screen.MouseDown += MouseDown;
        screen.MouseMove += MouseMove;
        screen.MouseUp += MouseUp;
        screen.MouseWheel += MouseWheel;
        screen.TextInput += TextInput;
        screen.KeyDown += KeyDown;
        screen.MouseEnter += MouseEnter;
        screen.MouseLeave += MouseLeave;
        _quiet = new DispatcherTimer(DispatcherPriority.Background, screen.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _quiet.Tick += (_, _) => CheckDeadline();
    }

    public event Action? OwnerActed;

    /// <summary>Release only the screen this view took, including when the view closes.</summary>
    internal void Release()
    {
        _quiet.Stop();
        _deadline = null;
        // A press the owner never let go of - the view closed under it, the agent handed back
        // mid-drag - must not leave an application believing a button is still down.
        if (_holding)
        {
            _holding = false;
            if (_screen.IsMouseCaptured) _screen.ReleaseMouseCapture();
            if (_runtime()?.Computer is { } computer) Send(() => { computer.EndGesture(); return true; });
        }
        WorkspaceControl? held = _held;
        _held = null;
        if (held is { Driving: Driver.Owner }) held.Release();
    }

    void TakeOver(WorkspaceControl plane)
    {
        if (_held is { } previous && !ReferenceEquals(previous, plane)) Release();
        // An existing owner lease may belong to Pause every agent or another view. This view
        // must never release that lease when its own quiet timer expires.
        if (plane.Driving != Driver.Owner)
        {
            plane.OwnerTakes();
            _held = plane;
        }
        ResetDeadline();
    }

    void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        if (!Down(e.GetPosition(_screen), e.ChangedButton == MouseButton.Right, e.ClickCount > 1)) return;
        Keyboard.Focus(_screen);
        // The picture takes the pointer for the whole gesture, so a drag that leaves it - a window
        // pulled to the edge of its screen, a selection run off the side of a page - carries on.
        _screen.CaptureMouse();
        e.Handled = true;
    }

    void MouseMove(object sender, MouseEventArgs e)
    {
        if (_holding)
        {
            Moved(e.GetPosition(_screen));
            e.Handled = true;
            return;
        }
        // Not a drag: the pointer still moves over there, so whatever is under it lights up the way
        // it would on the real machine. Only while the owner already holds the wheel - hovering is
        // not taking over - and never faster than the picture is drawn.
        if (Using() is not { } computer) return;
        long now = Environment.TickCount64;
        if (now - _lastHover < 40) return;
        _lastHover = now;
        if (!Map(e.GetPosition(_screen), out int x, out int y)) return;
        Send(() => computer.PointerMove(x, y));
    }

    void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_holding) return;
        Up(e.GetPosition(_screen));
        e.Handled = true;
    }

    /// <summary>
    /// The press half of a gesture, at a point in the picture own coordinates. The handler above is
    /// a thin wrapper around this so the gate can drive the real body without a real pointer.
    ///
    /// A workspace screen is a picture of a whole desktop drawn a few hundred pixels wide, so the
    /// owner is aiming at a tab twelve pixels tall and has no way to tell a click that missed from
    /// one the application ignored. The ring says where the click actually went and whether
    /// anything was there to take it.
    /// </summary>
    internal bool Down(Point point, bool rightButton, bool secondClick = false)
    {
        if (_runtime() is not { Computer: { } computer, Plane: { } plane }) return false;
        if (!Map(point, out int x, out int y)) return false;
        TakeOver(plane);
        _holding = true;
        ClickPing ping = ClickPing.Show(_screen, point);
        Send(() => computer.PointerPress(x, y, rightButton, secondClick), landed => ping.Answer(landed));
        Acted();
        return true;
    }

    /// <summary>The pointer moved while held. Clamped to the screen, so a drag that runs past the
    /// edge of the picture keeps dragging along it rather than stopping dead.</summary>
    internal void Moved(Point point)
    {
        if (!_holding || _runtime() is not { Computer: { } computer }) return;
        Map(point, out int x, out int y, clamp: true);
        Send(() => computer.PointerMove(x, y));
        Acted();
    }

    /// <summary>The owner let go, wherever the pointer had got to.</summary>
    internal void Up(Point point)
    {
        _holding = false;
        if (_screen.IsMouseCaptured) _screen.ReleaseMouseCapture();
        if (_runtime() is not { Computer: { } computer }) return;
        Map(point, out int x, out int y, clamp: true);
        Send(() => computer.PointerRelease(x, y));
        Acted();
    }

    /// <summary>A whole click in one call: what the gate drives, and what a caller with no pointer
    /// of its own needs.</summary>
    internal bool Press(Point point, bool rightButton)
    {
        if (!Down(point, rightButton)) return false;
        Up(point);
        return true;
    }

    bool _holding;
    long _lastHover;

    /// <summary>
    /// Input the owner gives the workspace, off the UI thread and in the order he gave it. It goes
    /// to the workspace's own pump, which may be part-way through a capture or waiting on an
    /// application's own message loop, and the window drawing the picture must never sit and wait
    /// for that: measured, an ordinary click can take a second on a busy screen, and the hub froze
    /// for exactly as long. One chain rather than one task each, so two quick clicks - or the
    /// letters of a typed word - cannot overtake one another on the way.
    /// </summary>
    void Send(Func<bool> input, Action<bool>? answered = null)
    {
        Task<bool> next = _sending.ContinueWith(_ =>
        {
            try { return input(); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return false; }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _sending = next;
        if (answered is null) return;
        next.ContinueWith(sent => _screen.Dispatcher.BeginInvoke(() => answered(sent.Result)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    Task<bool> _sending = Task.FromResult(true);

    void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Using() is not { } computer || !Map(e.GetPosition(_screen), out int x, out int y)) return;
        int delta = e.Delta;
        Send(() => computer.Scroll(x, y, delta));
        Acted();
        e.Handled = true;
    }

    void TextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || Using() is not { } computer) return;
        string text = e.Text;
        Send(() => computer.TypeText(text));
        Acted();
        e.Handled = true;
    }

    void KeyDown(object sender, KeyEventArgs e)
    {
        if (Using() is not { } computer) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.C or Key.V)
        {
            bool copy = key == Key.C;
            Send(() => { if (copy) computer.Copy(); else computer.Paste(); return true; });
        }
        // TextInput carries printable characters; desktop chord keys are handled separately.
        else if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Enter or Key.Tab or Key.Back or Key.Delete
            or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Insert or (>= Key.F1 and <= Key.F12))
        {
            int code = KeyInterop.VirtualKeyFromKey(key);
            Send(() => computer.SendKey(code));
        }
        else return;
        Acted();
        e.Handled = true;
    }

    AgentDesktop? Using() => _runtime() is { Computer: { } computer, Plane: { Driving: Driver.Owner } }
        ? computer : null;

    void MouseEnter(object sender, MouseEventArgs e) => Enter();
    void MouseLeave(object sender, MouseEventArgs e) => Leave();
    void Enter() { _hovering = true; ResetDeadline(); }
    void Leave() { _hovering = false; ResetDeadline(); }

    void Acted()
    {
        ResetDeadline();
        OwnerActed?.Invoke();
    }

    void ResetDeadline()
    {
        if (_held is null) return;
        _deadline = _now() + (_hovering ? StayDelay : LeaveDelay);
        _quiet.Start();
    }

    void CheckDeadline()
    {
        if (ModuleEntry.AllPaused) return;
        if (_held is not null && _deadline is { } deadline && _now() >= deadline) Release();
    }

    /// <summary>
    /// The owner acted on this workspace from beside the picture (its taskbar): the agent waits,
    /// and carries on as it does after the pointer leaves the screen.
    /// </summary>
    internal void Touch()
    {
        if (_runtime()?.Plane is { } plane) { TakeOver(plane); Acted(); }
    }

    internal void UseClockForTests(Func<DateTimeOffset> now) => _now = now;
    internal void SimulateEnterForTests() => Enter();
    internal void SimulateLeaveForTests() => Leave();
    internal void SimulateInputForTests() => Acted();
    internal void SimulateClickForTests()
    {
        if (_runtime()?.Plane is { } plane) { TakeOver(plane); Acted(); }
    }
    internal void CheckForTests() => CheckDeadline();

    /// <param name="clamp">True once a gesture is under way: the pointer has left the picture and
    /// the drag follows the edge of the workspace screen rather than being dropped.</param>
    bool Map(Point point, out int x, out int y, bool clamp = false)
    {
        x = y = 0;
        double width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
        if (_screen.ActualWidth <= 0 || _screen.ActualHeight <= 0) return false;
        double scale = _screen.Stretch == Stretch.UniformToFill
            ? Math.Max(_screen.ActualWidth / width, _screen.ActualHeight / height)
            : Math.Min(_screen.ActualWidth / width, _screen.ActualHeight / height);
        double left = (_screen.ActualWidth - width * scale) / 2, top = (_screen.ActualHeight - height * scale) / 2;
        x = (int)Math.Round((point.X - left) / scale);
        y = (int)Math.Round((point.Y - top) / scale);
        if (!clamp) return x >= 0 && y >= 0 && x < width && y < height;
        x = (int)Math.Clamp(x, 0, width - 1);
        y = (int)Math.Clamp(y, 0, height - 1);
        return true;
    }

    public void Dispose()
    {
        Release();
        _screen.MouseDown -= MouseDown;
        _screen.MouseMove -= MouseMove;
        _screen.MouseUp -= MouseUp;
        _screen.MouseWheel -= MouseWheel;
        _screen.TextInput -= TextInput;
        _screen.KeyDown -= KeyDown;
        _screen.MouseEnter -= MouseEnter;
        _screen.MouseLeave -= MouseLeave;
    }
}

/// <summary>
/// The mark left where a click landed on a workspace screen: a ring that grows and fades over
/// about a third of a second, drawn over the picture and never in the way of the next click. Green
/// while it is on its way, and left red when the workspace answers that there was nothing at that
/// point to press - a window's resize edge, the wallpaper, an application that had already gone.
/// Without it a picture of a desktop is the one surface in Windows that answers a click with
/// nothing whatsoever, and every miss reads as the product being broken.
/// </summary>
sealed class ClickPing : Adorner
{
    static readonly TimeSpan Life = TimeSpan.FromMilliseconds(360);
    static readonly Brush Landed = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50));
    static readonly Brush Missed = Frozen(Color.FromRgb(0xE0, 0x5A, 0x4F));
    static readonly Brush Edge = Frozen(Color.FromArgb(0x8C, 0, 0, 0));

    readonly Point _at;
    readonly DispatcherTimer _clock;
    readonly DateTime _born = DateTime.UtcNow;
    Brush _ink = Landed;
    bool _held;

    ClickPing(UIElement screen, Point at) : base(screen)
    {
        _at = at;
        IsHitTestVisible = false;
        _clock = new DispatcherTimer(DispatcherPriority.Render, screen.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _clock.Tick += (_, _) =>
        {
            // A ring the workspace has not answered yet waits at full strength rather than fading
            // out under a click that is still on its way to a busy screen.
            if (!_held && DateTime.UtcNow - _born >= Life) Done();
            else InvalidateVisual();
        };
    }

    internal static ClickPing Show(UIElement screen, Point at)
    {
        var ping = new ClickPing(screen, at);
        if (AdornerLayer.GetAdornerLayer(screen) is not { } layer) return ping;
        ping._layer = layer;
        layer.Add(ping);
        ping._held = true;
        ping._clock.Start();
        return ping;
    }

    AdornerLayer? _layer;

    /// <summary>What the workspace made of it. A click that landed fades from here; one that had
    /// nothing to land on turns red and stays a moment longer, so a miss is visibly a miss.</summary>
    internal void Answer(bool landed)
    {
        if (_layer is null) return;
        _ink = landed ? Landed : Missed;
        _held = false;
        if (!landed) _clock.Interval = TimeSpan.FromMilliseconds(24);
        InvalidateVisual();
    }

    void Done()
    {
        _clock.Stop();
        _layer?.Remove(this);
        _layer = null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double age = Math.Clamp((DateTime.UtcNow - _born) / Life, 0, 1);
        // Held rings sit at the start of the animation; once answered they run it out.
        double along = _held ? Math.Min(age, 0.35) : age;
        double radius = 5 + 13 * along;
        var ink = _ink.Clone();
        ink.Opacity = 1 - (_held ? 0 : along * along);
        var edge = Edge.Clone();
        edge.Opacity = ink.Opacity * 0.6;
        // Two rings, one dark: a single colour disappears into whatever the screen behind it is.
        drawing.DrawEllipse(null, new Pen(edge, 3.5), _at, radius, radius);
        drawing.DrawEllipse(null, new Pen(ink, 2), _at, radius, radius);
        if (_held || along < 0.5) drawing.DrawEllipse(ink, null, _at, 2, 2);
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
