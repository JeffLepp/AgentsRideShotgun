using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Deskweave.AgentWorkspaces;

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
        _restCursor = screen.Cursor;
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
    internal bool OwnsControl => _held is { Driving: Driver.Owner };

    /// <summary>A window was taken out onto the owner's desktop, or could not be: one short line
    /// for the view to show him.</summary>
    internal event Action<string>? PoppedOut;

    /// <summary>Release only the screen this view took, including when the view closes.</summary>
    internal void Release()
    {
        EndTear();
        EndOutline();
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
        Hover(e.GetPosition(_screen));
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
        _refused = 0;
        if (!rightButton && GripAt(point, out AgentDesktop.FrameTarget frame) is var grip and not FrameGrip.None)
        {
            // An edge: no ring, the outline that follows the pointer says where it took hold.
            nint window = frame.Handle;
            Send(() => computer.GrabFrame(window, grip, x, y));
            StartOutline(grip, frame.Shows, x, y);
            Acted();
            return true;
        }
        ClickPing ping = ClickPing.Show(_screen, point);
        Send(() => computer.PointerPress(x, y, rightButton, secondClick), landed => ping.Answer(landed));
        Acted();
        return true;
    }

    /// <summary>The pointer moved while held. Clamped to the screen, so a drag that runs past the
    /// edge of the picture keeps dragging along it rather than stopping dead.</summary>
    internal void Moved(Point point)
    {
        if (!_holding || _runtime() is not { Computer: { } computer } runtime) return;
        if (_tear is not null) { _ghost?.Follow(_screen.PointToScreen(point), Outside(point)); return; }
        // A window dragged by its title bar right off the picture is being taken out, the way a
        // browser tab dragged off its strip becomes its own window.
        if (Outside(point) && computer.MovingWindow is var moving && moving != 0 && moving != _refused)
        {
            if (WorkspacePopOut.For(runtime, moving) is { } plan) { StartTear(computer, plan, point); return; }
            _refused = moving;   // it cannot go out; the drag carries on along the edge, as before
        }
        Map(point, out int x, out int y, clamp: true);
        // A title-bar drag the workspace answered: the outline follows it from here.
        if (_outline is null && computer.MovingWindow is var held && held != 0
            && _frames.FirstOrDefault(f => f.Handle == held) is { Handle: not 0, Zoomed: false } frame)
        {
            (_, int grabX, int grabY) = computer.MoveGrab;
            StartOutline(FrameGrip.Move, frame.Shows, grabX, grabY);
        }
        _outline?.Follow(x, y);
        if (_outline is not null || computer.FrameHeld) SendLatestMove(computer, x, y);
        else Send(() => computer.PointerMove(x, y));
        Acted();
    }

    /// <summary>
    /// A frame drag sends only where the pointer is now. The workspace moves a window at its own
    /// pace; queuing every mouse move behind a slow one is what made a dragged window trail the
    /// pointer and keep going after it stopped.
    /// </summary>
    void SendLatestMove(AgentDesktop computer, int x, int y)
    {
        Interlocked.Exchange(ref _latestMove, (long)x << 32 | (uint)y);
        if (Interlocked.Exchange(ref _movePending, 1) == 1) return;
        Send(() =>
        {
            Interlocked.Exchange(ref _movePending, 0);
            long latest = Interlocked.Read(ref _latestMove);
            return computer.PointerMove((int)(latest >> 32), (int)latest);
        });
    }

    long _latestMove;
    int _movePending;

    /// <summary>The owner let go, wherever the pointer had got to.</summary>
    internal void Up(Point point)
    {
        _holding = false;
        EndOutline();
        if (_screen.IsMouseCaptured) _screen.ReleaseMouseCapture();
        ShowGripCursor(point);
        if (_tear is { } plan)
        {
            // The drag already ended in the workspace when the window left the picture.
            bool outside = Outside(point);
            Point at = _screen.PointToScreen(point);
            EndTear();
            if (outside && _runtime() is { } runtime) _ = PopOut(runtime, plan, ((int)at.X, (int)at.Y));
            Acted();
            return;
        }
        if (_runtime() is not { Computer: { } computer }) return;
        Map(point, out int x, out int y, clamp: true);
        Send(() => computer.PointerRelease(x, y));
        Acted();
    }

    /// <summary>A whole click in one call: what the gate drives, and what a caller with no pointer
    /// of its own needs.</summary>
    internal bool Press(Point point, bool rightButton, bool secondClick = false)
    {
        if (!Down(point, rightButton, secondClick)) return false;
        Up(point);
        return true;
    }

    bool _holding;
    long _lastHover;

    // --- taking a window out ------------------------------------------------------------------

    WorkspacePopOut.Plan? _tear;
    WorkspacePopOutGhost? _ghost;
    nint _refused;

    /// <summary>Off the picture by more than a few pixels: a drag that only brushes the edge is
    /// still a drag along it.</summary>
    bool Outside(Point point) => point.X < -8 || point.Y < -8
        || point.X > _screen.ActualWidth + 8 || point.Y > _screen.ActualHeight + 8;

    void StartTear(AgentDesktop computer, WorkspacePopOut.Plan plan, Point point)
    {
        _tear = plan;
        EndOutline();
        (WorkspaceScreen.Box box, int grabX, int grabY) = computer.MoveGrab;
        // The window goes back where it was in the workspace; from here it is only a picture.
        Send(computer.CancelMove);
        var grab = new Point(box.Width > 0 ? Math.Clamp((grabX - box.Left) / (double)box.Width, 0, 1) : 0.3,
            box.Height > 0 ? Math.Clamp((grabY - box.Top) / (double)box.Height, 0, 1) : 0.05);
        _ghost = new WorkspacePopOutGhost(Crop(box), new Size(box.Width, box.Height), grab);
        _ghost.Follow(_screen.PointToScreen(point), outside: true);
        // The window's own picture is sharper than a piece of the scaled-down screen: swap it in
        // when the workspace gets to it, without holding the view up.
        nint window = plan.Window;
        Task.Run(() => computer.CaptureWindow(window)).ContinueWith(shot =>
            _screen.Dispatcher.BeginInvoke(() =>
            {
                if (shot.Status == TaskStatus.RanToCompletion && shot.Result is { } picture && _tear == plan)
                    _ghost?.Refresh(picture);
            }), TaskScheduler.Default);
    }

    void EndTear()
    {
        _tear = null;
        _ghost?.Close();
        _ghost = null;
    }

    /// <summary>The part of the picture on screen that shows this window.</summary>
    ImageSource? Crop(WorkspaceScreen.Box box)
    {
        if (_screen.Source is not BitmapSource frame || box.Width <= 0 || box.Height <= 0) return null;
        double scale = frame.PixelWidth / (double)AgentDesktop.ScreenWidth;
        int left = (int)Math.Clamp(box.Left * scale, 0, frame.PixelWidth - 1);
        int top = (int)Math.Clamp(box.Top * scale, 0, frame.PixelHeight - 1);
        int width = (int)Math.Clamp(box.Width * scale, 1, frame.PixelWidth - left);
        int height = (int)Math.Clamp(box.Height * scale, 1, frame.PixelHeight - top);
        var piece = new CroppedBitmap(frame, new Int32Rect(left, top, width, height));
        piece.Freeze();
        return piece;
    }

    async Task PopOut(WorkspaceRuntime runtime, WorkspacePopOut.Plan plan, (int X, int Y)? at)
    {
        string said;
        try { said = await WorkspacePopOut.Run(runtime, plan, at); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or System.ComponentModel.Win32Exception)
        { said = "It could not open on your desktop"; }
        PoppedOut?.Invoke(said);
    }

    /// <summary>Open on my desktop, pressed over one of the workspace's windows.</summary>
    internal Task PopOut(nint window)
    {
        if (_runtime() is not { } runtime) return Task.CompletedTask;
        if (WorkspacePopOut.For(runtime, window) is { } plan) return PopOut(runtime, plan, null);
        PoppedOut?.Invoke("That one can't open on your desktop");
        return Task.CompletedTask;
    }

    readonly Dictionary<nint, (nint App, long At)> _canPopOut = [];

    /// <summary>
    /// The window under a point of the picture that can be taken out, and where it sits on the
    /// picture - for a view to put its Open on my desktop button on. A popup or dialog answers for
    /// its app's own window, so the button stays on the app rather than chasing its tooltips.
    /// Asks the workspace off the view's thread; a window's answer is remembered for a few seconds,
    /// hovering being constant.
    /// </summary>
    internal async Task<(nint Window, Rect Area)?> PoppableAt(Point point)
    {
        if (_holding || _runtime() is not { Computer: { } computer } runtime || !Map(point, out int x, out int y)) return null;
        // The picture's geometry is read here, on the view's thread, before waiting on the workspace.
        Func<double, double, Point> toPicture = PictureMapping();
        IReadOnlyList<AgentWindow> windows;
        try { windows = await Task.Run(() => computer.Windows()); }
        catch (ObjectDisposedException) { return null; }
        // Front first: the first window the point is inside is the one the owner can see there.
        AgentWindow? under = windows.FirstOrDefault(w => x >= w.X && y >= w.Y && x < w.X + w.Width && y < w.Y + w.Height);
        if (under is null) return null;
        long now = Environment.TickCount64;
        if (!_canPopOut.TryGetValue(under.Handle, out var known) || now - known.At > 4000)
        {
            known = (WorkspacePopOut.For(runtime, under.Handle)?.Window ?? 0, now);
            _canPopOut[under.Handle] = known;
        }
        if (known.App == 0) return null;
        AgentWindow app = windows.FirstOrDefault(w => w.Handle == known.App) ?? under;
        return (app.Handle, new Rect(toPicture(app.X, app.Y), toPicture(app.X + app.Width, app.Y + app.Height)));
    }

    // --- taking a window by its frame ---------------------------------------------------------

    readonly Cursor? _restCursor;
    IReadOnlyList<AgentDesktop.FrameTarget> _frames = [];
    long _framesAt;
    bool _framesAsked;
    FrameOutline? _outline;

    /// <summary>A window's edge shows it can be taken, whoever is driving: the resize cursor is the
    /// only way to find a border that is a pixel wide on the picture.</summary>
    internal void Hover(Point point)
    {
        if (_runtime()?.Computer is not { } computer) return;
        RefreshFrames(computer);
        ShowGripCursor(point);
    }

    /// <summary>The picture's cursor right now, for the probe.</summary>
    internal Cursor? CursorForTests => _screen.Cursor;

    /// <summary>The windows on the screen, asked of the workspace off the view's thread at most a
    /// few times a second: hovering is constant, and the answer only changes when a window moves.</summary>
    void RefreshFrames(AgentDesktop computer)
    {
        if (_framesAsked || Environment.TickCount64 - _framesAt < 200) return;
        _framesAsked = true;
        Task.Run(computer.FrameTargets).ContinueWith(asked => _screen.Dispatcher.BeginInvoke(() =>
        {
            _framesAsked = false;
            _framesAt = Environment.TickCount64;
            if (asked.Status == TaskStatus.RanToCompletion) _frames = asked.Result;
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Which edge of which window a point on the picture takes, front window first. The band is
    /// measured on the picture, not the screen: a few pixels either side of the frame the owner
    /// can see, mostly outside it where Windows' own border is, and longer at the corners.
    /// </summary>
    FrameGrip GripAt(Point point, out AgentDesktop.FrameTarget found)
    {
        found = default;
        double scale = PictureScale();
        if (_frames.Count == 0 || scale <= 0 || !Map(point, out int x, out int y, clamp: true)) return FrameGrip.None;
        int outside = (int)Math.Max(8, 6 / scale), inside = (int)Math.Max(2, 1.5 / scale);
        int corner = (int)Math.Max(16, 12 / scale);
        foreach (AgentDesktop.FrameTarget frame in _frames)
        {
            WorkspaceScreen.Box box = frame.Shows;
            if (x < box.Left - outside || x >= box.Right + outside || y < box.Top - outside || y >= box.Bottom + outside)
                continue;
            FrameGrip grip = frame.Sizable ? GripFor(box, x, y, inside, corner) : FrameGrip.None;
            if (grip != FrameGrip.None) { found = frame; return grip; }
            // Inside a window is that window's, whatever is behind it; beside one, look further back.
            if (x >= box.Left && x < box.Right && y >= box.Top && y < box.Bottom) return FrameGrip.None;
        }
        return FrameGrip.None;
    }

    /// <summary>The edge a point near a window's frame takes: within <paramref name="inside"/> of
    /// a side, or anywhere past it, and a corner within <paramref name="corner"/> of one.</summary>
    internal static FrameGrip GripFor(WorkspaceScreen.Box box, int x, int y, int inside, int corner)
    {
        bool left = x < box.Left + inside, right = x >= box.Right - inside;
        bool top = y < box.Top + inside, bottom = y >= box.Bottom - inside;
        if (!(left || right || top || bottom)) return FrameGrip.None;
        if (left || right) { top |= y < box.Top + corner; bottom |= y >= box.Bottom - corner; }
        if (top || bottom) { left |= x < box.Left + corner; right |= x >= box.Right - corner; }
        // A window small enough for both bands to meet: the nearer edge.
        if (left && right) { left = x - box.Left < box.Right - x; right = !left; }
        if (top && bottom) { top = y - box.Top < box.Bottom - y; bottom = !top; }
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => FrameGrip.TopLeft,
            (_, true, true, _) => FrameGrip.TopRight,
            (true, _, _, true) => FrameGrip.BottomLeft,
            (_, true, _, true) => FrameGrip.BottomRight,
            (true, _, _, _) => FrameGrip.Left,
            (_, true, _, _) => FrameGrip.Right,
            (_, _, true, _) => FrameGrip.Top,
            _ => FrameGrip.Bottom,
        };
    }

    void ShowGripCursor(Point point)
    {
        if (_holding) return;
        _screen.Cursor = GripAt(point, out _) switch
        {
            FrameGrip.Left or FrameGrip.Right => Cursors.SizeWE,
            FrameGrip.Top or FrameGrip.Bottom => Cursors.SizeNS,
            FrameGrip.TopLeft or FrameGrip.BottomRight => Cursors.SizeNWSE,
            FrameGrip.TopRight or FrameGrip.BottomLeft => Cursors.SizeNESW,
            _ => _restCursor,
        };
    }

    void StartOutline(FrameGrip grip, WorkspaceScreen.Box shows, int grabX, int grabY)
    {
        EndOutline();
        _outline = FrameOutline.Show(_screen, PictureMapping(), grip, shows, grabX, grabY);
    }

    void EndOutline()
    {
        _outline?.Close();
        _outline = null;
    }

    /// <summary>Picture pixels per workspace screen pixel, as the picture is laid out now.</summary>
    double PictureScale()
    {
        double width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
        if (_screen.ActualWidth <= 0 || _screen.ActualHeight <= 0) return 0;
        return _screen.Stretch == Stretch.UniformToFill
            ? Math.Max(_screen.ActualWidth / width, _screen.ActualHeight / height)
            : Math.Min(_screen.ActualWidth / width, _screen.ActualHeight / height);
    }

    /// <summary>Workspace screen points onto the picture as it is laid out now: the inverse of Map.</summary>
    Func<double, double, Point> PictureMapping()
    {
        double width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
        double shownWidth = _screen.ActualWidth, shownHeight = _screen.ActualHeight;
        double scale = _screen.Stretch == Stretch.UniformToFill
            ? Math.Max(shownWidth / width, shownHeight / height)
            : Math.Min(shownWidth / width, shownHeight / height);
        double left = (shownWidth - width * scale) / 2, top = (shownHeight - height * scale) / 2;
        return (x, y) => new Point(left + x * scale, top + y * scale);
    }

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
/// Where a window dragged by its frame is going, drawn over the picture the moment the pointer
/// moves. The picture is a dozen frames a second at best and the window follows at whatever pace
/// its application keeps; without this a resize felt like pulling through syrup.
/// </summary>
sealed class FrameOutline : Adorner
{
    static readonly Pen Dark = FrozenPen(Color.FromArgb(0x99, 0, 0, 0), 3.5);
    static readonly Brush Wash = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    readonly Func<double, double, Point> _toPicture;
    readonly FrameGrip _grip;
    readonly WorkspaceScreen.Box _from;
    readonly int _grabX, _grabY;
    readonly Pen _ink;
    AdornerLayer? _layer;
    Rect _shown;

    FrameOutline(FrameworkElement screen, Func<double, double, Point> toPicture, FrameGrip grip,
        WorkspaceScreen.Box from, int grabX, int grabY) : base(screen)
    {
        IsHitTestVisible = false;
        _toPicture = toPicture;
        _grip = grip;
        _from = from;
        _grabX = grabX;
        _grabY = grabY;
        _ink = FrozenPen(screen.TryFindResource("AccentColor") is Color accent ? accent : Color.FromRgb(0x4C, 0x8D, 0xFF), 2);
        Place(grabX, grabY);
    }

    internal static FrameOutline? Show(FrameworkElement screen, Func<double, double, Point> toPicture, FrameGrip grip,
        WorkspaceScreen.Box from, int grabX, int grabY)
    {
        if (AdornerLayer.GetAdornerLayer(screen) is not { } layer) return null;
        var outline = new FrameOutline(screen, toPicture, grip, from, grabX, grabY) { _layer = layer };
        layer.Add(outline);
        return outline;
    }

    /// <summary>The pointer is here now, in workspace screen pixels.</summary>
    internal void Follow(int x, int y)
    {
        Place(x, y);
        InvalidateVisual();
    }

    void Place(int x, int y)
    {
        WorkspaceScreen.Box to = AgentDesktop.Dragged(_grip, _from, x - _grabX, y - _grabY,
            AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight);
        _shown = new Rect(_toPicture(to.Left, to.Top), _toPicture(to.Right, to.Bottom));
        var picture = (FrameworkElement)AdornedElement;
        _shown.Intersect(new Rect(0, 0, picture.ActualWidth, picture.ActualHeight));
    }

    internal void Close()
    {
        _layer?.Remove(this);
        _layer = null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (_shown.IsEmpty || _shown.Width < 1 || _shown.Height < 1) return;
        drawing.DrawRoundedRectangle(Wash, Dark, _shown, 3, 3);
        drawing.DrawRoundedRectangle(null, _ink, _shown, 3, 3);
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Frozen(colour), thickness);
        pen.Freeze();
        return pen;
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
