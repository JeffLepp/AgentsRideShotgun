using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// What one tick of the corner view draws. Wave 0's spike found the whole screen far too costly to
/// print at a rate worth watching, but one window prints fast and cheap - so idle prints the whole
/// screen at a slow pace, and in use prints only the front window laid over the last whole frame,
/// refreshing that background once a second. Pure enough to call from the host's timer and from the
/// probe that measures it, and it touches no window of its own.
/// </summary>
internal static class WorkspacePeekCapture
{
    static readonly TimeSpan BackgroundAge = TimeSpan.FromSeconds(1);

    internal static BitmapSource? Take(AgentDesktop desktop, ref BitmapSource? background,
        ref DateTimeOffset backgroundAt, bool inUse)
    {
        if (!inUse)
        {
            if (desktop.CaptureScreen() is { } whole) { background = whole; backgroundAt = DateTimeOffset.Now; }
            return background;
        }
        if (background is null || DateTimeOffset.Now - backgroundAt >= BackgroundAge)
            if (desktop.CaptureScreen() is { } whole) { background = whole; backgroundAt = DateTimeOffset.Now; }
        if (background is not { } frame) return null;
        IReadOnlyList<AgentWindow> windows = desktop.Windows();
        if (windows.Count == 0) return frame;
        AgentWindow front = windows[0];
        return desktop.CaptureWindow(front.Handle) is { } patch ? Over(frame, patch, front.X, front.Y) : frame;
    }

    static BitmapSource Over(BitmapSource background, BitmapSource patch, int x, int y)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(background, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
            context.DrawImage(patch, new Rect(x, y, patch.PixelWidth, patch.PixelHeight));
        }
        var target = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
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
