using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// What one tick of the corner view draws. Wave 0's spike found the whole screen far too costly to
/// print at a rate worth watching, but one window prints fast and cheap - so idle prints the whole
/// screen at a slow pace, and in use prints only the front window, refreshing the whole-screen
/// background once a second underneath it. Pure enough to call from the host's timer and from the
/// probe that measures it, and it touches no window of its own.
///
/// Wave 1's fix round found the first cut of "in use" still compositing a full 1920x1080
/// <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/> every tick to draw the window over
/// the background - most of an idle tick's own cost, paid again on every one of the much faster
/// in-use ticks. There is no composite any more: the two bitmaps are handed back separately and the
/// window draws them as two layered images, so an in-use tick costs one window print and one small
/// pixel copy, nothing whole-screen sized.
/// </summary>
internal static class WorkspacePeekCapture
{
    static readonly TimeSpan BackgroundAge = TimeSpan.FromSeconds(1);

    /// <summary>One tick's picture: the whole-screen background to show underneath, and - only while
    /// in use and a front window exists - the small patch to lay over it at its own screen position.
    /// <see cref="Background"/> is null only when even the background capture failed; the caller
    /// should then leave whatever was on screen alone rather than blank it.</summary>
    internal readonly struct Frame(BitmapSource? background, BitmapSource? patch, int patchX, int patchY)
    {
        internal BitmapSource? Background { get; } = background;
        internal BitmapSource? Patch { get; } = patch;
        internal int PatchX { get; } = patchX;
        internal int PatchY { get; } = patchY;
    }

    internal static Frame Take(AgentDesktop desktop, ref BitmapSource? background,
        ref DateTimeOffset backgroundAt, bool inUse)
    {
        if (!inUse)
        {
            if (desktop.CaptureScreen() is { } whole) { background = whole; backgroundAt = DateTimeOffset.Now; }
            return new Frame(background, null, 0, 0);
        }
        if (background is null || DateTimeOffset.Now - backgroundAt >= BackgroundAge)
            if (desktop.CaptureScreen() is { } whole) { background = whole; backgroundAt = DateTimeOffset.Now; }
        if (background is not { } frame) return new Frame(null, null, 0, 0);
        IReadOnlyList<AgentWindow> windows = desktop.Windows();
        if (windows.Count == 0) return new Frame(frame, null, 0, 0);
        AgentWindow front = windows[0];
        BitmapSource? patch = desktop.CaptureWindow(front.Handle);
        return new Frame(frame, patch, front.X, front.Y);
    }

    /// <summary>Whether Windows reports the PC running on battery right now. False (never pause) if
    /// it cannot be read - a laptop that is actually plugged in is the safer default to assume.</summary>
    internal static bool OnBattery()
    {
        try { return GetSystemPowerStatus(out SystemPowerStatus status) && status.ACLineStatus == 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
