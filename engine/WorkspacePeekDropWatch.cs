using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// Windows has no "a drag just started" event. While the corner view is enabled but hidden, and
/// the owner left "Open the corner window when you drag a file toward it" on, this watches for the
/// left button pressed on the desktop or an Explorer window and then moved into the corner, and
/// calls back so the window can show in time for the drop to land on it normally.
///
/// It polls the button and the cursor, 20 times a second, on its own background thread. It used to
/// be a WH_MOUSE_LL hook: with one, every mouse movement on the PC waits for ARS to answer, and
/// with the PC short of memory ARS answered late and the owner's pointer lagged (field report,
/// 0.3.10). Polling can only be late to notice a drag (at most one tick, 50 ms); it can never slow
/// the mouse. It reads positions only and sends nothing anywhere.
/// </summary>
internal sealed class WorkspacePeekDropWatch : IDisposable
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);
    const int VkLButton = 0x01, VkRButton = 0x02, SmSwapButton = 23;
    const uint GaRoot = 2;

    // The classes a desktop or (older-style) Explorer window itself carries. SysListView32 is the
    // desktop's own icon list; DirectUIHWND and SHELLDLL_DefView are the toolbar and icon-view
    // children some Explorer builds put directly under the cursor.
    static readonly HashSet<string> DirectClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "SysListView32", "CabinetWClass", "ExploreWClass",
        "DirectUIHWND", "SHELLDLL_DefView",
    };

    // Windows 11 hosts much of modern Explorer's chrome, and on some builds the desktop's, in XAML
    // island child windows with a generic class. Their top-level owner still is one of these.
    static readonly HashSet<string> RootClasses = new(StringComparer.Ordinal)
    {
        "CabinetWClass", "ExploreWClass", "Progman", "WorkerW",
    };

    readonly ManualResetEventSlim _stop = new();
    readonly Thread _thread;

    /// <param name="target">The corner rect to watch for, in screen pixels. Asked once per press,
    /// on the watch thread, so it must only read settings and monitor geometry.</param>
    /// <param name="summon">Called on the watch thread when a drag from the desktop or Explorer
    /// reaches the target. It must only post the real work (BeginInvoke) and return.</param>
    internal WorkspacePeekDropWatch(Func<Rect> target, Action summon)
    {
        _thread = new Thread(() => Watch(target, summon)) { IsBackground = true, Name = "ARS corner drag watch" };
        _thread.Start();
    }

    /// <summary>What a press is doing, between ticks.</summary>
    internal readonly record struct State(bool Down, bool Dragging, Rect Target, Point Origin);

    /// <summary>How far the pointer must move from where the button went down before it is a drag
    /// rather than a click - Windows' own default drag threshold (SM_CXDRAG).</summary>
    internal const double DragThreshold = 4;

    /// <summary>
    /// One tick, kept free of Windows calls so the probe can drive it. A press counts only when it
    /// starts on the desktop or Explorer and then moves; it summons once, on the first tick the
    /// moved cursor is inside the corner, and never again until the button comes up and goes down
    /// again. A click held still on an icon under the corner is a click, and opens nothing.
    /// </summary>
    internal static (State Next, bool Summon) Step(State state, bool down, Point cursor,
        Func<Point, bool> fromDesktopOrExplorer, Func<Rect> target)
    {
        if (!down) return (default, false);
        if (!state.Down)
        {
            bool dragging = fromDesktopOrExplorer(cursor);
            state = new State(true, dragging, dragging ? target() : Rect.Empty, cursor);
        }
        bool moved = Math.Abs(cursor.X - state.Origin.X) > DragThreshold || Math.Abs(cursor.Y - state.Origin.Y) > DragThreshold;
        if (state.Dragging && moved && !state.Target.IsEmpty && cursor.X >= state.Target.Left && cursor.X < state.Target.Right
            && cursor.Y >= state.Target.Top && cursor.Y < state.Target.Bottom)
            return (state with { Dragging = false }, true);
        return (state, false);
    }

    void Watch(Func<Rect> target, Action summon)
    {
        State state = default;
        while (!_stop.Wait(Tick))
        {
            try
            {
                // GetAsyncKeyState reads the physical button; with buttons swapped the primary
                // (the one that drags) is the physical right one.
                int primary = GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton;
                bool down = (GetAsyncKeyState(primary) & 0x8000) != 0;
                Point cursor = default;
                if (down)
                {
                    if (!GetCursorPos(out NativePoint at)) continue;
                    cursor = new Point(at.X, at.Y);
                }
                (state, bool fire) = Step(state, down, cursor, IsDesktopOrExplorer, target);
                if (fire) summon();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { state = default; }
        }
    }

    static bool IsDesktopOrExplorer(Point point)
    {
        nint window = WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y });
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

    public void Dispose()
    {
        _stop.Set();
        // The thread wakes within one tick; it is a background thread, so not waiting is safe too.
        _thread.Join(TimeSpan.FromMilliseconds(200));
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder text, int max);
}
