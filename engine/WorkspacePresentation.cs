using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Deskweave.AgentWorkspaces;

/// <summary>Keep automatic surfaces out of games, fullscreen video and Windows presentation mode.</summary>
internal static class WorkspacePresentation
{
    internal static Func<bool>? SuppressedForTests;
    internal static nint ForegroundWindow => GetForegroundWindow();
    internal static bool Suppressed => SuppressedForTests?.Invoke() ?? ReadSuppressed();
    static readonly object ScanGate = new();
    static long _nextScan;
    static Rect[] _fullscreenMonitors = [];

    // Native queries only; never activate, move or capture the owner's windows. Cache the
    // cross-monitor scan so preview frames do not repeat it more than four times a second.
    internal static IReadOnlyList<Rect> FullscreenMonitors
    {
        get
        {
            lock (ScanGate)
            {
                long now = Environment.TickCount64;
                if (now >= _nextScan)
                {
                    _fullscreenMonitors = VisibleFullscreenMonitors(ReadWindows());
                    _nextScan = now + 250;
                }
                return _fullscreenMonitors;
            }
        }
    }

    static bool ReadSuppressed()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0 && QuietState(state)) return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return FullscreenMonitors.Count > 0;
    }

    // NOT_PRESENT, BUSY, exclusive D3D and presentation mode. A normal Store app (7) is allowed.
    internal static bool QuietState(int state) => state is 1 or 2 or 3 or 4;

    internal static bool CoversMonitor(Rect client, Rect monitor) => !client.IsEmpty && !monitor.IsEmpty
        && client.Width > 0 && client.Height > 0 && monitor.Width > 0 && monitor.Height > 0
        && client.Left <= monitor.Left + 2 && client.Top <= monitor.Top + 2
        && client.Right >= monitor.Right - 2 && client.Bottom >= monitor.Bottom - 2;

    internal readonly record struct PresentationWindow(Rect Bounds, Rect Client, Rect Monitor, Rect WorkArea,
        bool Visible = true, bool Minimized = false, bool Cloaked = false, bool NormalMaximized = false,
        bool Opaque = true, bool OwnProcess = false);

    // Windows arrive from front to back. A normal maximized browser covering the game's
    // work area makes that game irrelevant; a window on another monitor or a small overlay
    // does not. Layered/transparent windows cannot establish occlusion conservatively.
    internal static Rect[] VisibleFullscreenMonitors(IEnumerable<PresentationWindow> windows)
    {
        var above = new List<Rect>();
        var fullscreen = new HashSet<Rect>();
        foreach (PresentationWindow window in windows)
        {
            if (!window.Visible || window.Minimized || window.Cloaked) continue;
            if (!window.OwnProcess && !window.NormalMaximized && CoversMonitor(window.Client, window.Monitor)
                && !above.Any(bounds => CoversMonitor(bounds, window.WorkArea)))
                fullscreen.Add(window.Monitor);
            if (window.Opaque) above.Add(window.Bounds);
        }
        return fullscreen.ToArray();
    }

    static List<PresentationWindow> ReadWindows()
    {
        var windows = new List<PresentationWindow>();
        nint previous = 0;
        try
        {
            // Client and monitor rectangles must both be physical pixels on mixed-DPI displays.
            previous = SetThreadDpiAwarenessContext(new nint(-4));
            nint shell = GetShellWindow();
            var seen = new HashSet<nint>();
            // A bounded, de-duplicated walk remains finite if windows disappear or reorder.
            for (nint window = GetTopWindow(0); window != 0 && seen.Count < 2048 && seen.Add(window); window = GetWindow(window, 2))
            {
                if (window == shell || !IsWindowVisible(window) || IsIconic(window)) continue;
                if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
                if (!GetClientRect(window, out NativeRect client)) continue;
                var origin = new NativePoint { X = client.Left, Y = client.Top };
                var end = new NativePoint { X = client.Right, Y = client.Bottom };
                if (!ClientToScreen(window, ref origin) || !ClientToScreen(window, ref end)) continue;
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) continue;
                if (DwmGetWindowAttribute(window, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0
                    && !GetWindowRect(window, out bounds)) continue;
                GetWindowThreadProcessId(window, out int process);
                // A regular maximized window retains a caption even when the taskbar auto-hides.
                bool normalMaximized = IsZoomed(window) && (GetWindowLong(window, -16) & 0x00C00000) != 0;
                bool opaque = (GetWindowLong(window, -20) & (0x00080000 | 0x00000020)) == 0;
                windows.Add(new PresentationWindow(bounds.Rect,
                    new Rect(origin.X, origin.Y, Math.Max(0, end.X - origin.X), Math.Max(0, end.Y - origin.Y)),
                    monitor.Bounds.Rect, monitor.Work.Rect, NormalMaximized: normalMaximized,
                    Opaque: opaque, OwnProcess: process == Environment.ProcessId));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        return windows;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rect Rect => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { internal int Size; internal NativeRect Bounds, Work; internal int Flags; }
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, int flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
}
