namespace Deskweave.AgentWorkspaces;

/// <summary>
/// The workspace screen, as a rule rather than a fact: one monitor, the primary one, from (0,0) to
/// (width,height). The owner's PC may have three monitors, and a workspace desktop shares his
/// monitor layout, so a program that remembers where it was last opens on whichever of them it
/// was on. Measured 2026-09-06: a large WPF app opened at (-1684,119), on the monitor to the left
/// of the primary, where the whole-screen capture could not see it and no click could reach it.
///
/// Everything here is pure arithmetic so it can be tested without a desktop.
/// </summary>
internal static class WorkspaceScreen
{
    internal readonly record struct Box(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
    }

    /// <summary>The fraction of a window's area that is on the screen, 0 to 1.</summary>
    internal static double VisibleFraction(Box window, int width, int height)
    {
        long area = (long)window.Width * window.Height;
        if (area <= 0) return 0;
        long left = Math.Max(window.Left, 0), top = Math.Max(window.Top, 0);
        long right = Math.Min(window.Right, width), bottom = Math.Min(window.Bottom, height);
        if (right <= left || bottom <= top) return 0;
        return (double)((right - left) * (bottom - top)) / area;
    }

    /// <summary>
    /// Whether a window has been lost to the screen. Less than half of it visible is the line: a
    /// maximized window's invisible eight-pixel border hangs over every edge and is not lost, a
    /// window that opened on the next monitor along is.
    /// </summary>
    internal static bool IsLost(Box window, int width, int height) =>
        VisibleFraction(window, width, height) < 0.5;

    /// <summary>
    /// Where a lost window goes. One that is entirely off the screen comes back to the middle,
    /// the way Deskweave's own shell treats a position saved on a monitor that is no longer there;
    /// one that is partly on is slid in, which keeps the part the agent was looking at where it was.
    /// Either way the result fits inside the screen.
    /// </summary>
    internal static Box Place(Box window, int width, int height)
    {
        if (VisibleFraction(window, width, height) > 0) return Fit(window, width, height);
        Box sized = Fit(window, width, height);
        return Fit(new Box((width - sized.Width) / 2, (height - sized.Height) / 2, sized.Width, sized.Height),
            width, height);
    }

    /// <summary>Clamps a box to fit wholly inside the screen: too big shrinks, outside moves in.</summary>
    internal static Box Fit(Box window, int width, int height)
    {
        int w = Math.Clamp(window.Width, Math.Min(64, width), width);
        int h = Math.Clamp(window.Height, Math.Min(64, height), height);
        int left = Math.Clamp(window.Left, 0, Math.Max(0, width - w));
        int top = Math.Clamp(window.Top, 0, Math.Max(0, height - h));
        return new Box(left, top, w, h);
    }
}
