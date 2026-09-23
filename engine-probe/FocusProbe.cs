using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Which engine step, if any, takes the foreground from the owner. Found 2026-09-22: during the UI
/// gate the foreground went to a window titled DeskweaveClipboardBroker for six seconds as the
/// first workspace started. Logs the foreground window around each step.
///
/// Run: Deskweave.Probe.exe --focus "C:\absolute\output"
/// </summary>
internal static class FocusProbe
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(nint h, out int pid);

    static string Foreground()
    {
        nint h = GetForegroundWindow();
        var t = new StringBuilder(200); GetWindowText(h, t, 200);
        GetWindowThreadProcessId(h, out int pid);
        string name = "?"; try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
        return $"{name} '{t}' {h:X}";
    }

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var log = new List<string>();
        void Note(string step) { log.Add($"{DateTime.Now:HH:mm:ss.fff} {step}: {Foreground()}"); }
        Note("start");
        _ = ClipboardBroker.Shared;
        Thread.Sleep(700); Note("after broker created");
        using (AgentDesktop desktop = AgentDesktop.Create("focus-" + Guid.NewGuid().ToString("N")[..8]))
        {
            Thread.Sleep(700); Note("after desktop created");
            using var control = new WorkspaceControl(desktop);
            Thread.Sleep(700); Note("after control plane");
            desktop.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"), null);
            Thread.Sleep(2500); Note("after an app launched in the workspace");
        }
        Thread.Sleep(700); Note("after desktop disposed");
        File.WriteAllLines(Path.Combine(output, "focus.txt"), log);
        return 0;
    }
}
