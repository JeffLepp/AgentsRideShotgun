using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using HiveMind.Product;

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
    const uint GaRoot = 2;

    // The classes a desktop or (older-style) Explorer window itself carries. SysListView32 is the
    // desktop's own icon list; DirectUIHWND and SHELLDLL_DefView are the toolbar and icon-view
    // children some Explorer builds put directly under the cursor.
    static readonly HashSet<string> DirectClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "SysListView32", "CabinetWClass", "ExploreWClass",
        "DirectUIHWND", "SHELLDLL_DefView",
    };

    // Windows 11 hosts much of modern Explorer's own chrome - and, on some builds, the desktop's -
    // in XAML island child windows whose own class name is a generic composition/bridge one, not
    // in the list above. Their top-level owner still is, so a miss on the immediate class walks up
    // to the root with GetAncestor(GA_ROOT) and checks that instead of giving up.
    static readonly HashSet<string> RootClasses = new(StringComparer.Ordinal)
    {
        "CabinetWClass", "ExploreWClass", "Progman", "WorkerW",
    };

    // Kept alive: a delegate handed to native code must not be collected while the hook holds it.
    readonly HookProc _proc;
    readonly nint _hook;
    bool _dragging;

    /// <summary>Whether the hook actually installed. False when Windows refused it (SetLastError is
    /// logged); the caller is expected to simply have no hidden-window drop summon rather than
    /// crash or retry in a loop.</summary>
    internal bool Installed => _hook != 0;

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
        if (_hook == 0) LogInstallFailure(Marshal.GetLastWin32Error());
    }

    static bool IsDesktopOrExplorer(NativePoint point)
    {
        nint window = WindowFromPoint(point);
        if (window == 0) return false;
        if (DirectClasses.Contains(ClassOf(window))) return true;
        nint root = GetAncestor(window, GaRoot);
        return root != 0 && root != window && RootClasses.Contains(ClassOf(root));
    }

    static string ClassOf(nint window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    /// <summary>The same diagnostic path <c>App.LogFailure</c> writes real crashes to
    /// (<c>ProductContext.Local("logs")</c>): a hook that never installs would otherwise fail
    /// silently, leaving a hidden corner window that never wakes for a drag started on the desktop
    /// with no trace of why. Never throws: a logging failure must not become the caller's problem.</summary>
    static void LogInstallFailure(int win32Error)
    {
        try
        {
            string root = ProductContext.Local("logs");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "corner-drop-hook.txt"),
                $"{DateTimeOffset.Now}: SetWindowsHookEx(WH_MOUSE_LL) failed, Win32 error {win32Error}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
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
    [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder text, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? module);
}
