# Read-only host observation shared by validation scripts. This deliberately does not load
# Deskweave's implementation: a live check must independently establish why UI should be quiet.
if (-not ('DeskweaveValidationPresentation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskweaveValidationPresentation {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Bounds, Work; public int Flags; }
    public sealed class FullscreenWindow {
        public long Handle;
        public uint ProcessId;
        public string Title;
        public Rect Client, Monitor;
    }
    public sealed class Observation {
        public int NotificationState = -1;
        public bool ShellQuiet, ScanComplete;
        public List<FullscreenWindow> VisibleFullscreenWindows = new List<FullscreenWindow>();
        public List<string> Errors = new List<string>();
        public bool Quiet { get { return ShellQuiet || VisibleFullscreenWindows.Count > 0; } }
    }
    delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll", SetLastError = true)] static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    static bool Covers(Rect outer, Rect area) {
        return outer.Right > outer.Left && outer.Bottom > outer.Top && area.Right > area.Left && area.Bottom > area.Top &&
            outer.Left <= area.Left + 2 && outer.Top <= area.Top + 2 && outer.Right >= area.Right - 2 && outer.Bottom >= area.Bottom - 2;
    }
    static void Unreadable(Observation result, IntPtr window, string what) {
        // A window disappearing during enumeration is normal; a still-visible unreadable one
        // makes a launch guard uncertain, so Sandbox must defer instead of assuming it is safe.
        if (IsWindow(window) && IsWindowVisible(window) && !IsIconic(window))
            result.Errors.Add(what + " unavailable for HWND " + window.ToInt64());
    }
    public static Observation Read() {
        Observation result = new Observation();
        IntPtr previousDpi = IntPtr.Zero;
        try {
            int state;
            if (SHQueryUserNotificationState(out state) == 0) result.NotificationState = state;
            else result.Errors.Add("Windows notification state unavailable");
            result.ShellQuiet = result.NotificationState >= 1 && result.NotificationState <= 4;
            // All client, monitor, and DWM rectangles below must use physical pixels, including
            // negative monitor coordinates and monitors with different scaling percentages.
            previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (previousDpi == IntPtr.Zero) {
                result.Errors.Add("Physical-pixel DPI context unavailable");
                return result;
            }
            IntPtr shell = GetShellWindow();
            uint ownProcess = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            List<Rect> opaqueAbove = new List<Rect>();
            int windows = 0;
            // EnumWindows visits the current desktop's top-level windows from front to back.
            bool complete = EnumWindows(delegate(IntPtr window, IntPtr data) {
                if (++windows > 2048) { result.Errors.Add("Window enumeration exceeded its bound"); return false; }
                if (window == shell || !IsWindowVisible(window) || IsIconic(window)) return true;
                StringBuilder className = new StringBuilder(256);
                GetClassName(window, className, className.Capacity);
                string name = className.ToString();
                if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) != 0) {
                    Unreadable(result, window, "Cloaking state"); return true;
                }
                if (cloaked != 0) return true;
                Rect client;
                if (!GetClientRect(window, out client)) { Unreadable(result, window, "Client rectangle"); return true; }
                Point first = new Point { X = client.Left, Y = client.Top }, last = new Point { X = client.Right, Y = client.Bottom };
                if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last)) {
                    Unreadable(result, window, "Physical client coordinates"); return true;
                }
                client = new Rect { Left = first.X, Top = first.Y, Right = last.X, Bottom = last.Y };
                MonitorInfo monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) { Unreadable(result, window, "Monitor geometry"); return true; }
                Rect bounds;
                if (DwmGetWindowAttribute(window, 9, out bounds, Marshal.SizeOf(typeof(Rect))) != 0 && !GetWindowRect(window, out bounds)) {
                    Unreadable(result, window, "Window bounds"); return true;
                }
                uint process;
                GetWindowThreadProcessId(window, out process);
                // A normal maximized browser remains usable, including with an auto-hidden
                // taskbar. Its opaque frame can cover a fullscreen game lower in the Z order.
                bool normalMaximized = IsZoomed(window) && (GetWindowLong(window, -16) & 0x00C00000) != 0;
                bool covered = opaqueAbove.Exists(delegate(Rect above) { return Covers(above, monitor.Work); });
                if (process != ownProcess && !normalMaximized && Covers(client, monitor.Bounds) && !covered) {
                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(window, title, title.Capacity);
                    result.VisibleFullscreenWindows.Add(new FullscreenWindow {
                        Handle = window.ToInt64(), ProcessId = process, Title = title.ToString(), Client = client, Monitor = monitor.Bounds
                    });
                }
                // Layered/transparent windows and small overlays cannot prove the game is
                // covered. Multiple partial covers conservatively leave suppression enabled.
                if ((GetWindowLong(window, -20) & (0x00080000 | 0x00000020)) == 0) opaqueAbove.Add(bounds);
                return true;
            }, IntPtr.Zero);
            if (!complete && result.Errors.Count == 0) result.Errors.Add("Window enumeration failed: " + Marshal.GetLastWin32Error());
            result.ScanComplete = complete && result.Errors.Count == 0;
        } catch (Exception error) {
            result.Errors.Add(error.GetType().Name + ": " + error.Message);
        } finally {
            if (previousDpi != IntPtr.Zero && SetThreadDpiAwarenessContext(previousDpi) == IntPtr.Zero) {
                result.Errors.Add("Could not restore thread DPI context");
                result.ScanComplete = false;
            }
        }
        return result;
    }
}
'@
}

function Get-DeskweavePresentationState {
    [DeskweaveValidationPresentation]::Read()
}
