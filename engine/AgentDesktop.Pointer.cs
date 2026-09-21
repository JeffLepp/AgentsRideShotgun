namespace HiveMind.AgentWorkspaces;

/// <summary>Which edge of a window a press on its frame took hold of.</summary>
enum FrameGrip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public sealed partial class AgentDesktop
{
    // One gesture at a time: there is one owner and he has one pointer. An agent's own input goes
    // through Click, DoubleClick and Pointer, which complete inside a single call and never touch
    // any of this.
    nint _gestureWindow;          // the window the press landed on, kept for the whole gesture
    nint _gestureRoot;            // its top-level window, which is what a frame gesture moves
    int _gestureArea;             // what that window answered WM_NCHITTEST with at the press
    nint _gestureCommand;         // a caption button's WM_SYSCOMMAND, sent only if released on it
    FrameGrip _grip;
    bool _gestureDown;
    bool _gestureRight;
    int _grabX, _grabY;
    WorkspaceScreen.Box _grabbed;

    /// <summary>
    /// The owner's pointer pressed on the workspace screen. Unlike <see cref="Click"/> this is one
    /// end of a gesture rather than a whole one: the press, the moves and the release arrive as
    /// they happen, which is the only way dragging a window, selecting text in one and
    /// double-clicking inside one can work at all from a picture of a screen.
    ///
    /// Nothing here ever posts WM_NCLBUTTONDOWN. That message hands the window to DefWindowProc's
    /// move and size loops, which follow the real cursor - and the real cursor belongs to the
    /// window station, not to this desktop, so it is on the owner's own screen and will never
    /// arrive. Dragging a title bar or an edge is done here instead, by moving the window itself.
    /// </summary>
    public bool PointerPress(int x, int y, bool rightButton, bool secondClick, long lease = 0) => Run(() =>
    {
        EndGesture();
        if (Revoked(lease)) return false;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (!OwnsWindow(target)) return false;
        nint screen = Packed(x, y);
        nint answered = Native.SendMessageTimeoutW(target, Native.WmNcHitTest, 0, screen,
            Native.SmtoAbortIfHung, 500, out nint area);
        if (answered == 0) return false;
        _gestureWindow = target;
        _gestureRoot = Native.GetAncestor(target, 2); // GA_ROOT
        _gestureArea = (int)area;
        _gestureRight = rightButton;
        _grabX = x;
        _grabY = y;
        _lastClicked = target;
        _lastPoint = new Native.Point { X = x, Y = y };

        if (_gestureArea > Native.HtClient)
        {
            _grip = Grip(_gestureArea);
            _gestureCommand = _gestureArea switch
            {
                Native.HtMinButton => Native.ScMinimize,
                Native.HtMaxButton => Native.IsZoomed(_gestureRoot) ? Native.ScRestore : Native.ScMaximize,
                Native.HtClose => Native.ScClose,
                _ => 0,
            };
            if (_grip == FrameGrip.None && _gestureCommand == 0)
            {
                // A scrollbar or the help button: the plain pair, which is a press and not a drag.
                if (_gestureArea is Native.HtSysMenu or Native.HtMenu) return false;
                Native.PostMessageW(target, Native.WmNcMouseMove, area, screen);
                Native.PostMessageW(target, rightButton ? Native.WmNcRButtonDown : Native.WmNcLButtonDown, area, screen);
                _gestureDown = true;
                return true;
            }
            if (_grip == FrameGrip.None) return true;   // a caption button, answered on release
            if (Native.GetWindowRect(_gestureRoot, out Native.Rect rect))
                _grabbed = new WorkspaceScreen.Box(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            // Taking hold of a window's frame brings it forward, the way it does anywhere else.
            if (_grip == FrameGrip.Move) Arrange(_gestureRoot, WindowArrangement.Front, lease: lease);
            return true;
        }

        _grip = FrameGrip.None;
        if (!ClientPoint(target, x, y, out nint at)) return false;
        // Same reason as AgentDesktop.Click: without this, WPF and WinForms drop "the mouse is over
        // this control" on a desktop with no foreground window, and the press never turns into a Click.
        Activate(target);
        if (!Message(target, Native.WmMouseMove, 0, at)) return false;
        if (!Message(target, rightButton ? Native.WmRButtonDown : Native.WmLButtonDown, rightButton ? 2 : 1, at))
            return false;
        _gestureDown = true;
        // The second press of a double-click is the one the application acts on: an ordinary press
        // first, exactly as Windows sends it, then the double-click message on top.
        if (secondClick) Message(target, rightButton ? 0x0206u : 0x0203u, rightButton ? 2 : 1, at);
        return true;
    });

    /// <summary>
    /// The owner's pointer moved. Mid-gesture it goes to the window that took the press, the way a
    /// real capture does; otherwise it is a hover, which is what makes a button, a menu or a tab
    /// light up under the pointer and is how the owner can tell what he is about to press on a
    /// desktop drawn a few hundred pixels wide.
    /// </summary>
    public bool PointerMove(int x, int y, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return false;
        if (_grip != FrameGrip.None && _gestureRoot != 0) return DragFrame(x, y);
        nint target = _gestureDown && _gestureWindow != 0
            ? _gestureWindow
            : Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (target == 0 || (!_gestureDown && !OwnsWindow(target))) return false;
        if (_gestureDown && _gestureArea > Native.HtClient)
        {
            Native.PostMessageW(target, Native.WmNcMouseMove, _gestureArea, Packed(x, y));
            return true;
        }
        if (!ClientPoint(target, x, y, out nint at)) return false;
        _lastPoint = new Native.Point { X = x, Y = y };
        // MK_LBUTTON / MK_RBUTTON while held, so a drag reads as a drag and not as a hover.
        nint buttons = _gestureDown ? (_gestureRight ? 2 : 1) : 0;
        return Message(target, Native.WmMouseMove, buttons, at);
    });

    /// <summary>The owner let go. Finishes whatever the press started and nothing else.</summary>
    public bool PointerRelease(int x, int y, long lease = 0) => Run(() =>
    {
        bool done = false;
        try
        {
            if (_gestureRoot == 0 && _gestureWindow == 0) return false;
            if (_grip != FrameGrip.None) { done = true; return true; }
            if (_gestureCommand != 0)
            {
                // Like a real caption button: it acts only if the pointer is still on it.
                nint screen = Packed(x, y);
                nint answered = Native.SendMessageTimeoutW(_gestureWindow, Native.WmNcHitTest, 0, screen,
                    Native.SmtoAbortIfHung, 250, out nint area);
                done = answered != 0 && (int)area == _gestureArea
                    && Native.PostMessageW(_gestureWindow, Native.WmSysCommand, _gestureCommand, screen);
                return done;
            }
            if (!_gestureDown) return false;
            if (_gestureArea > Native.HtClient)
            {
                done = Native.PostMessageW(_gestureWindow,
                    _gestureRight ? Native.WmNcRButtonUp : Native.WmNcLButtonUp, _gestureArea, Packed(x, y));
                return done;
            }
            // The release goes to the window that took the press even when the pointer has left it,
            // which is what makes a drag that ends outside a window end rather than hang.
            if (!ClientPoint(_gestureWindow, x, y, out nint at)) at = Packed(_lastPoint.X, _lastPoint.Y);
            done = Message(_gestureWindow, _gestureRight ? Native.WmRButtonUp : Native.WmLButtonUp, 0, at);
            // Same measured gap as AgentDesktop.Click: the message route cannot raise Click on a WPF
            // window at all, so a genuine press-then-release-over-target still has to do something.
            if (!_gestureRight && IsWpfWindow(_gestureRoot) && InvokeAtPoint(x, y)) done = true;
            return done;
        }
        finally { EndGesture(); }
    });

    /// <summary>True while a press is still being held on this desktop.</summary>
    internal bool PointerHeld => _gestureDown || _grip != FrameGrip.None || _gestureCommand != 0;

    /// <summary>The owner moved a window by its title bar, or took an edge and resized it.</summary>
    bool DragFrame(int x, int y)
    {
        int dx = x - _grabX, dy = y - _grabY;
        WorkspaceScreen.Box was = _grabbed;
        WorkspaceScreen.Box want = _grip switch
        {
            FrameGrip.Move => was with { Left = was.Left + dx, Top = was.Top + dy },
            _ => Resized(was, dx, dy),
        };
        if (want.Width < 120 || want.Height < 80) return false;
        // A maximized window being dragged by its title bar comes down first, as it does anywhere.
        if (_grip == FrameGrip.Move && Native.IsZoomed(_gestureRoot)) Native.ShowWindow(_gestureRoot, Native.SwRestore);
        WorkspaceScreen.Box to = WorkspaceScreen.Fit(want, ScreenWidth, ScreenHeight);
        return Native.SetWindowPos(_gestureRoot, 0, to.Left, to.Top, to.Width, to.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
    }

    WorkspaceScreen.Box Resized(WorkspaceScreen.Box was, int dx, int dy)
    {
        int left = was.Left, top = was.Top, width = was.Width, height = was.Height;
        if (_grip is FrameGrip.Left or FrameGrip.TopLeft or FrameGrip.BottomLeft) { left += dx; width -= dx; }
        if (_grip is FrameGrip.Right or FrameGrip.TopRight or FrameGrip.BottomRight) width += dx;
        if (_grip is FrameGrip.Top or FrameGrip.TopLeft or FrameGrip.TopRight) { top += dy; height -= dy; }
        if (_grip is FrameGrip.Bottom or FrameGrip.BottomLeft or FrameGrip.BottomRight) height += dy;
        return new WorkspaceScreen.Box(left, top, width, height);
    }

    static FrameGrip Grip(int area) => area switch
    {
        Native.HtCaption => FrameGrip.Move,
        10 => FrameGrip.Left,
        11 => FrameGrip.Right,
        12 => FrameGrip.Top,
        13 => FrameGrip.TopLeft,
        14 => FrameGrip.TopRight,
        15 => FrameGrip.Bottom,
        16 => FrameGrip.BottomLeft,
        17 => FrameGrip.BottomRight,
        Native.HtGrowBox => FrameGrip.BottomRight,
        _ => FrameGrip.None,
    };

    /// <summary>
    /// Ends a gesture, releasing a button it is still holding. Called before a new press and after
    /// every release, so a press the owner never let go of - the window closed under it, the view
    /// moved to another workspace - cannot leave an application believing a button is still down.
    /// </summary>
    internal void EndGesture()
    {
        if (_gestureDown && _gestureWindow != 0 && _gestureArea <= Native.HtClient
            && ClientPoint(_gestureWindow, _lastPoint.X, _lastPoint.Y, out nint at))
            Message(_gestureWindow, _gestureRight ? Native.WmRButtonUp : Native.WmLButtonUp, 0, at);
        _gestureWindow = _gestureRoot = 0;
        _gestureArea = 0;
        _gestureCommand = 0;
        _grip = FrameGrip.None;
        _gestureDown = _gestureRight = false;
    }

    static nint Packed(int x, int y) => (y & 0xFFFF) << 16 | (x & 0xFFFF);
}
