using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Observe the child's actual desktop instead of inferring it from the launch request.
internal static class SuiteDesktopReceipt
{
    internal static void Record()
    {
        string? path = Environment.GetEnvironmentVariable("ARS_PROBE_DESKTOP_RECEIPT");
        Environment.SetEnvironmentVariable("ARS_PROBE_DESKTOP_RECEIPT", null);
        if (string.IsNullOrEmpty(path)) return;
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The suite receipt path must be absolute.");
        nint handle = GetThreadDesktop(GetCurrentThreadId());
        var name = new StringBuilder(256);
        if (handle == 0 || !GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot observe the suite startup desktop.");
        File.WriteAllText(path, JsonSerializer.Serialize(new { pid = Environment.ProcessId, desktop = name.ToString() }));
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out int needed);
}
