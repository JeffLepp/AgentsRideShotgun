using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Deskweave;

// Inspect an install's processes without requesting their memory or full-access handles.
internal static class ControllerProcess
{
    internal static string? ImagePath(int pid)
    {
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return null; // The process exited between enumeration and inspection.
            throw new Win32Exception(error);
        }
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length)) return path.ToString();
        int failure = Marshal.GetLastWin32Error();
        if (GetExitCodeProcess(process, out uint code) && code != 259) return null;
        throw new Win32Exception(failure);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
