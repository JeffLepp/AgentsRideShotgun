namespace HiveMind.AgentWorkspaces;

/// <summary>What an agent may do to a window as a whole, the way a person does with its title bar.</summary>
public enum WindowArrangement { Move, Maximize, Minimize, Restore, Front, Close }

public sealed partial class AgentDesktop
{
    /// <summary>
    /// A window that had ended up off the workspace screen was pulled back onto it. The text says
    /// which and from where, for the evidence log.
    /// </summary>
    public event Action<string>? Pulled;

    /// <summary>
    /// The windows, after any lost one has been brought back. A workspace desktop shares the owner's
    /// monitor layout, so a program that remembers its position opens wherever it last was - which
    /// can be a monitor the workspace screen does not cover. Off that screen it cannot be
    /// photographed, clicked or seen by the owner, so this runs every time the windows are read:
    /// by the agent's list, and by the panel's capture every half second. Idempotent, and it never
    /// touches a window that is at least half on the screen, so a program's own placement wins
    /// whenever that placement is visible.
    /// </summary>
    IReadOnlyList<AgentWindow> WindowsOnScreen()
    {
        IReadOnlyList<AgentWindow> windows = WindowsOnPump();
        int width = ScreenWidth, height = ScreenHeight;
        bool moved = false;
        foreach (AgentWindow window in windows)
        {
            var box = new WorkspaceScreen.Box(window.X, window.Y, window.Width, window.Height);
            if (Native.IsIconic(window.Handle) || !WorkspaceScreen.IsLost(box, width, height)) continue;
            if (Native.IsHungAppWindow(window.Handle)) continue;
            WorkspaceScreen.Box to = WorkspaceScreen.Place(box, width, height);
            // A window maximized on another monitor is restored, brought over, and maximized again
            // - which Windows does on the monitor it is now on, the workspace screen.
            bool zoomed = Native.IsZoomed(window.Handle);
            if (zoomed) Native.ShowWindow(window.Handle, Native.SwRestore);
            if (Native.SetWindowPos(window.Handle, 0, to.Left, to.Top, to.Width, to.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder))
            {
                moved = true;
                Pulled?.Invoke($"\"{(window.Title.Length > 0 ? window.Title : window.ClassName)}\" "
                    + $"from ({window.X},{window.Y}) to ({to.Left},{to.Top})");
            }
            if (zoomed) Native.ShowWindow(window.Handle, Native.SwMaximize);
        }
        return moved ? WindowsOnPump() : windows;
    }

    /// <summary>
    /// Sizes a window to the whole workspace screen. A workspace desktop has no taskbar, and
    /// maximizing leaves room for one: an empty strip along the bottom of every corner preview.
    /// </summary>
    internal bool Fill(nint window) => window != 0 && Run(() =>
    {
        if (!OwnsWindow(window)) return false;
        if (Native.IsZoomed(window)) Native.ShowWindow(window, Native.SwRestore);
        return Native.SetWindowPos(window, 0, 0, 0, ScreenWidth, ScreenHeight,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
    });

    /// <summary>
    /// Moves, sizes, maximizes, minimizes, restores, raises or closes one window. Measured 2026-09-06:
    /// without this the agent wrote a PowerShell script around SetWindowPos, in Notepad, to do what
    /// one call does. A move is fitted to the screen; zero width or height keeps the current size.
    /// </summary>
    public bool Arrange(nint window, WindowArrangement what, int x = 0, int y = 0, int width = 0, int height = 0,
        long lease = 0) => Run(() =>
    {
        if (Revoked(lease) || !OwnsWindow(window) || Native.IsHungAppWindow(window)) return false;
        ClipboardBroker.Shared.Touch(Name);
        switch (what)
        {
            case WindowArrangement.Move:
            {
                if (!Native.GetWindowRect(window, out Native.Rect rect)) return false;
                WorkspaceScreen.Box to = WorkspaceScreen.Fit(new WorkspaceScreen.Box(x, y,
                    width > 0 ? width : rect.Right - rect.Left, height > 0 ? height : rect.Bottom - rect.Top),
                    ScreenWidth, ScreenHeight);
                if (Native.IsZoomed(window)) Native.ShowWindow(window, Native.SwRestore);
                return Native.SetWindowPos(window, 0, to.Left, to.Top, to.Width, to.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
            }
            case WindowArrangement.Maximize:
                Native.ShowWindow(window, Native.SwMaximize);
                return Native.IsZoomed(window);
            case WindowArrangement.Minimize:
                Native.ShowWindow(window, Native.SwMinimize);
                return Native.IsIconic(window);
            case WindowArrangement.Restore:
                Native.ShowWindow(window, Native.SwRestore);
                return !Native.IsZoomed(window) && !Native.IsIconic(window);
            case WindowArrangement.Front:
            {
                if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SwRestore);
                // Measured 2026-09-07: HWND_TOP without activation leaves the z-order alone on a
                // desktop that has no active window. Topmost and back again is the reorder that
                // holds, and it activates nothing.
                const uint keep = Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate;
                return Native.SetWindowPos(window, Native.HwndTopmost, 0, 0, 0, 0, keep)
                    && Native.SetWindowPos(window, Native.HwndNoTopmost, 0, 0, 0, 0, keep);
            }
            case WindowArrangement.Close:
                // The same command the close button sends, so an app that asks "save changes?" asks.
                return Native.PostMessageW(window, Native.WmSysCommand, Native.ScClose, 0);
        }
        return false;
    });
}
