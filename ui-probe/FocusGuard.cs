using System.Runtime.InteropServices;

namespace Deskweave.UiProbe;

/// <summary>
/// Hands the foreground straight back to whatever the owner was using. The gate's windows live off
/// every screen, but some checks still activate one - restoring a minimized hub does, as a person
/// restoring it would expect - and each took the owner's typing for a second or more, six seconds
/// once (measured 2026-09-22 while he worked). Every 50 ms: if this process has the foreground and
/// it came from someone else's window, give it back. Being the foreground process is exactly what
/// Windows asks of a caller before it may pass the foreground on.
/// </summary>
internal sealed class FocusGuard : IDisposable
{
    readonly Timer _timer;
    readonly int _self = Environment.ProcessId;
    nint _owner;

    internal FocusGuard()
    {
        _owner = Foreign(GetForegroundWindow());
        _timer = new Timer(_ => Tick(), null, 50, 50);
    }

    void Tick()
    {
        nint now = GetForegroundWindow();
        if (Foreign(now) is not 0 and var theirs) { _owner = theirs; return; }
        if (now != 0 && _owner != 0 && IsWindow(_owner)) SetForegroundWindow(_owner);
    }

    nint Foreign(nint window)
    {
        if (window == 0) return 0;
        GetWindowThreadProcessId(window, out int pid);
        return pid == _self ? 0 : window;
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(nint window, out int processId);
}
