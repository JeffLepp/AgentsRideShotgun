using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Windows has no "a drag just started" event. While the corner view is enabled but hidden, this
/// watches the desktop's own mouse for a left button pressed on the desktop or an Explorer window
/// and moved toward the corner - then calls back so the window can show in time for the drop to
/// land on it normally. It reads positions only: no click is ever sent anywhere by this hook, and
/// it exists only for as long as something holds it.
/// </summary>
internal sealed class WorkspacePeekDropHook : IDisposable
{
    const int WhMouseLl = 14, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmMouseMove = 0x0200;

    // Kept alive: a delegate handed to native code must not be collected while the hook holds it.
    readonly HookProc _proc;
    readonly nint _hook;
    bool _dragging;

    /// <param name="target">The corner rect to watch for, in screen pixels, asked fresh each time.</param>
    /// <param name="summon">Called once, on the hook's own thread, when a drag from the desktop or
    /// Explorer reaches the target. The caller hops to its own thread before touching a window.</param>
    internal WorkspacePeekDropHook(Func<Rect> target, Action summon)
    {
        _proc = (code, wparam, lparam) =>
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lparam);
                int message = (int)wparam;
                if (message == WmLButtonDown) _dragging = IsDesktopOrExplorer(info.Point);
                else if (message == WmLButtonUp) _dragging = false;
                else if (message == WmMouseMove && _dragging)
                {
                    Rect at = target();
                    if (info.Point.X >= at.Left && info.Point.X < at.Right && info.Point.Y >= at.Top && info.Point.Y < at.Bottom)
                    {
                        _dragging = false; // one summon per press, so a lingering drag does not refire
                        summon();
                    }
                }
            }
            return CallNextHookEx(0, code, wparam, lparam);
        };
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
    }

    static bool IsDesktopOrExplorer(NativePoint point)
    {
        nint window = WindowFromPoint(point);
        if (window == 0) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString() switch
        {
            "Progman" or "WorkerW" or "SysListView32" or "CabinetWClass" or "ExploreWClass" => true,
            _ => false,
        };
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWindowsHookEx(_hook);
    }

    [StructLayout(LayoutKind.Sequential)] readonly struct NativePoint { public readonly int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    readonly struct MsLlHookStruct { public readonly NativePoint Point; readonly uint Data, Flags, Time; readonly nint Extra; }

    delegate nint HookProc(int code, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder text, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? module);
}
