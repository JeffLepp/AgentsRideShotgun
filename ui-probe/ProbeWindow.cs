using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Deskweave.UiProbe;

/// <summary>Scene windows render off-screen even when the real shell restores saved placement.</summary>
internal static class ProbeWindow
{
    internal static T OffScreen<T>(T window) where T : Window
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SceneContext.OffScreen.X;
        window.Top = SceneContext.OffScreen.Y;
        void Attach()
        {
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            source.AddHook(KeepOffScreen);
            // The window owns/disposes this source and its hooks together.
        }
        if (new WindowInteropHelper(window).Handle != 0) Attach();
        else window.SourceInitialized += (_, _) => Attach();
        return window;
    }

    static nint KeepOffScreen(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0046) // WM_WINDOWPOSCHANGING: alter the proposed move before it is visible.
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            // Physical pixels, matching WINDOWPOS. Root-DPI fixtures can change the window's DIP scale.
            position.X = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds.Right).DefaultIfEmpty(1920).Max() + 400;
            position.Y = 40;
            position.Flags = (position.Flags & ~0x0002u) | 0x0010u; // move, without activation
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    internal static bool IsOffScreen(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out NativeRect bounds)) return false;
        return !System.Windows.Forms.Screen.AllScreens.Any(screen => bounds.Left < screen.Bounds.Right
            && bounds.Right > screen.Bounds.Left && bounds.Top < screen.Bounds.Bottom && bounds.Bottom > screen.Bounds.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPosition { internal nint Window, InsertAfter; internal int X, Y, Width, Height; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out NativeRect bounds);
}
