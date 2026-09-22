using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskweave.AgentWorkspaces;

public sealed partial class AgentDesktop
{
    const int ConsoleStartfUseShowWindow = 0x1;
    const short ConsoleShowMinNoActive = 7;
    const int ConsoleShowNoActivate = 4;
    const uint ConsoleCreateNoWindow = 0x08000000;

    // Resolve once and use the same absolute image for CreateProcess. No extension/name heuristic:
    // any native console executable needs this treatment, while GUI apps keep their normal startup.
    internal static bool TryConsoleImage(string exe, out string image, out bool console)
    {
        image = string.Empty;
        console = false;
        try
        {
            var path = new StringBuilder(32768);
            uint length = ConsoleSearchPath(null, exe, ".exe", path.Capacity, path, 0);
            if (length == 0 || length >= path.Capacity) return false;
            image = path.ToString();
            using var file = new FileStream(image, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(file);
            Subsystem? subsystem = pe.PEHeaders.PEHeader?.Subsystem;
            console = subsystem == Subsystem.WindowsCui;
            return console || subsystem == Subsystem.WindowsGui;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or BadImageFormatException or ArgumentException or NotSupportedException)
        {
            return false; // An unreadable/unknown image must not take an unchecked console route.
        }
    }

    // Keep the child's real PID, command line, token, job, cwd and exit code. A conhost command-line
    // wrapper would reparse arguments and replace that PID/exit code with its host's. Only the
    // uniquely titled console on this exact desktop may be restored, never a Default window.
    // Source: microsoft/terminal src/server/IoDispatchers.cpp _shouldAttemptHandoff.
    // A descendant deliberately asking for a *new* console is not covered by inherited hosting.
    void RestoreConsoleWhenReady(string marker, string displayTitle, long lease)
    {
        long until = Environment.TickCount64 + 5000;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && !Revoked(lease) && Environment.TickCount64 < until)
                {
                    bool restored = Run(() =>
                    {
                        if (_disposed || Revoked(lease) || Environment.TickCount64 >= until) return false;
                        bool found = false;
                        Native.EnumDesktopWindows(_desktop, (window, _) =>
                        {
                            if (Text(window, Native.GetClassNameW) != "ConsoleWindowClass"
                                // Titled adds a diagnostic suffix for hung windows. Matching must
                                // use the unmodified, nonblocking cached title instead.
                                || Text(window, Native.InternalGetWindowText) != marker) return true;
                            // EnumDesktopWindows plus the launch nonce establishes the exact window;
                            // no PID reuse, process-name match, owner-desktop enumeration or focus call.
                            if (!Native.IsIconic(window)) return false;
                            found = ConsoleShowWindowAsync(window, ConsoleShowNoActivate);
                            if (found)
                            {
                                nint title = Marshal.StringToHGlobalUni(displayTitle);
                                try { Native.SendMessageTimeoutW(window, 0x000C, 0, title,
                                    Native.SmtoAbortIfHung | Native.SmtoBlock, 100, out _); }
                                finally { Marshal.FreeHGlobal(title); }
                            }
                            return false;
                        }, 0);
                        return found;
                    }, TimeSpan.FromMilliseconds(250));
                    if (restored) return;
                    await Task.Delay(25).ConfigureAwait(false);
                }
                // A fast exit, explicit title/window change, revocation or a slow launch may defeat
                // restoration. Leave that console minimized; never retry through Terminal/Default.
                Trace.WriteLine("Workspace console was not restored within its bound: " + Name);
            }
            catch (ObjectDisposedException) { }
        });
    }

    [DllImport("kernel32.dll", EntryPoint = "SearchPathW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint ConsoleSearchPath(string? path, string file, string extension,
        int capacity, StringBuilder result, nint filePart);

    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConsoleShowWindowAsync(nint window, int command);
}
