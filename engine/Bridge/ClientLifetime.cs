using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

// Watch the actual parent process handle, not a reusable PID. EOF is watched separately,
// including while startup/forwarding is blocked. No desktop or controller is ever killed.
internal static class ClientLifetime
{
    static ParentWait? _parent;
    static RegisteredWaitHandle? _parentDeath;

    internal static void WatchParent()
    {
        if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, out Basic info,
            Marshal.SizeOf<Basic>(), out _) != 0) throw new IOException("Cannot identify MCP client.");
        nint handle = OpenProcess(0x00101000, false, checked((int)info.Parent)); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (handle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) { Environment.Exit(0); return; }
            throw new Win32Exception(error);
        }
        var parent = new ParentWait(handle);
        if (!GetProcessTimes(parent.SafeWaitHandle, out long created, out _, out _, out _))
        {
            int error = Marshal.GetLastWin32Error();
            parent.Dispose();
            throw new Win32Exception(error);
        }
        if (created > Process.GetCurrentProcess().StartTime.ToFileTimeUtc())
        { parent.Dispose(); Environment.Exit(0); return; }
        _parent = parent;
        _parentDeath = ThreadPool.RegisterWaitForSingleObject(parent, (_, _) =>
        {
            _parentDeath?.Unregister(null);
            _parent?.Dispose();
            Environment.Exit(0);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    sealed class ParentWait : WaitHandle
    {
        internal ParentWait(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(SafeWaitHandle process, out long created, out long exited, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct Basic { internal nint Reserved, Peb, Reserved2, Reserved3, Pid, Parent; }
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(nint process, int informationClass, out Basic info, int size, out int returned);
}
