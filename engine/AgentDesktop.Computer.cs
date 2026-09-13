using System.Runtime.InteropServices;

namespace HiveMind.AgentWorkspaces;

public sealed partial class AgentDesktop
{
    // WM_CHAR treats both CR and LF as Enter. One logical newline must produce one Enter.
    internal static string MessageText(string text) => text.Replace("\r\n", "\r").Replace('\n', '\r');

    bool OwnsWindow(nint window)
    {
        if (window == 0 || !Native.IsWindow(window)) return false;
        nint root = Native.GetAncestor(window, 2); // GA_ROOT, includes owned popup dialogs as roots.
        bool found = false;
        Native.EnumDesktopWindows(_desktop, (candidate, _) =>
        { if (candidate != root) return true; found = true; return false; }, 0);
        return found;
    }

    internal bool ComputerInput(ComputerAction action, long lease) => action.Type switch
    {
        "click" => Click(action.X, action.Y, action.Button == "right", lease),
        "double_click" => DoubleClick(action, lease),
        "move" => Pointer(action, lease),
        "drag" => Pointer(action, lease),
        // The computer convention is positive down/right; the legacy scroll tool is positive up.
        "scroll" => action.ScrollX == 0 && Scroll(action.X, action.Y, -action.ScrollY, lease),
        "type" => TypeText(action.Text, lease: lease),
        "keypress" => action.Keys!.Length == 1 ? SendKey(WorkspaceComputer.KeyCode(action.Keys[0]), lease: lease)
            : SelectAll(lease),
        _ => false,
    };

    bool SelectAll(long lease) => Run(() =>
    {
        if (Revoked(lease)) return false;
        nint target = Focused(0);
        string kind = Text(target, Native.GetClassNameW);
        if (!kind.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            && !kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;
        return Native.SendMessageTimeoutW(target, 0x00B1 /* EM_SETSEL */, 0, -1,
            Native.SmtoAbortIfHung, 250, out _) != 0;
    });

    bool DoubleClick(ComputerAction action, long lease) => Run(() =>
    {
        if (Revoked(lease)) return false;
        nint target = Native.WindowFromPoint(new Native.Point { X = action.X, Y = action.Y });
        if (!OwnsWindow(target) || !ClientPoint(target, action.X, action.Y, out nint at)) return false;
        bool right = action.Button == "right";
        uint down = right ? Native.WmRButtonDown : Native.WmLButtonDown;
        uint up = right ? Native.WmRButtonUp : Native.WmLButtonUp;
        bool pressed = Message(target, down, right ? 2 : 1, at);
        bool released = Message(target, up, 0, at);
        if (!pressed || !released || Revoked(lease)) return false;
        pressed = Message(target, right ? 0x0206u : 0x0203u, right ? 2 : 1, at);
        _lastClicked = target;
        released = Message(target, up, 0, at);
        return pressed && released;
    });

    bool Pointer(ComputerAction action, long lease) => Run(() =>
    {
        if (Revoked(lease)) return false;
        ComputerPoint[] points = action.Path ?? [new(action.X, action.Y)];
        nint target = Native.WindowFromPoint(new Native.Point { X = points[0].X, Y = points[0].Y });
        if (!OwnsWindow(target)) return false;
        // Message-based dragging is supported within one client area, not OS drag/drop or resize loops.
        var positions = new List<nint>();
        foreach (var point in points)
        {
            if (!ClientPoint(target, point.X, point.Y, out nint at)) return false;
            positions.Add(at);
        }
        bool down = false;
        nint last = positions[0];
        try
        {
            for (int i = 0; i < positions.Count; i++)
            {
                if (Revoked(lease)) return false;
                last = positions[i];
                if (!Message(target, Native.WmMouseMove, down ? 1 : 0, last)) return false;
                if (i == 0 && action.Type == "drag")
                {
                    down = true;
                    if (!Message(target, Native.WmLButtonDown, 1, last)) return false;
                }
            }
            _lastClicked = target;
            if (!down) return true;
            down = false;
            return Message(target, Native.WmLButtonUp, 0, last);
        }
        finally
        {
            // Release only the button this call pressed, even if takeover interrupted the drag.
            if (down) Message(target, Native.WmLButtonUp, 0, last);
        }
    });

    static bool Message(nint target, uint message, nint parameter, nint point) =>
        Native.SendMessageTimeoutW(target, message, parameter, point, Native.SmtoAbortIfHung, 250, out _) != 0;

    static bool ClientPoint(nint target, int x, int y, out nint packed)
    {
        var point = new Native.Point { X = x, Y = y };
        packed = 0;
        if (!Native.ScreenToClient(target, ref point) || !Native.GetClientRect(target, out var rect)
            || point.X < 0 || point.Y < 0 || point.X >= rect.Right || point.Y >= rect.Bottom) return false;
        packed = (point.Y & 0xffff) << 16 | (point.X & 0xffff);
        return true;
    }
}
