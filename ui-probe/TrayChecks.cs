using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Deskweave.UiProbe;

internal static class TrayChecks
{
    internal static async Task Run()
    {
        string executable = Environment.ProcessPath!;
        Guid identity = TrayIcon.IdentityFor(executable);
        Program.Check(identity == TrayIcon.IdentityFor(executable.ToUpperInvariant())
            && identity != TrayIcon.IdentityFor(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(executable)!, "another.exe")),
            "Tray identity survives relaunches and path casing, without colliding with another unsigned install");

        var calls = new List<(uint Operation, TrayIcon.IconData Data)>();
        var entries = new HashSet<Guid> { identity }; // a ghost from an abruptly stopped process
        bool available = true;
        int successfulAdds = 0;
        bool Notify(uint operation, ref TrayIcon.IconData data)
        {
            calls.Add((operation, data));
            if (!available) return false;
            if (operation == 0)
            {
                bool added = entries.Add(data.Identity);
                if (added) successfulAdds++;
                return added;
            }
            return operation switch
            {
                1 => entries.Contains(data.Identity),
                2 => entries.Remove(data.Identity),
                _ => true
            };
        }
        using var icon = (Icon)SystemIcons.Application.Clone();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Deskweave");
        using var tray = new TrayIcon(icon, menu, identity, Notify);
        Program.Check(calls[0].Operation == 2 && entries.SetEquals([identity]),
            "Startup removes only its own stale tray identity before installing one live entry");
        for (int i = 0; i < 40; i++) tray.Register();
        Program.Check(entries.Count == 1 && successfulAdds == 1
            && calls.All(c => c.Data.Identity == identity && (c.Data.Flags & 0x20) != 0),
            "Forty tray refreshes use the same GUID and add no extra icons");
        int opens = 0, balloons = 0;
        tray.OpenRequested += () => opens++;
        tray.BalloonClicked += () => balloons++;
        SendMessage(tray.Handle, TrayIcon.CallbackMessage, 0, 0x203);
        SendMessage(tray.Handle, TrayIcon.CallbackMessage, 0, (1 << 16) | 0x401);
        SendMessage(tray.Handle, TrayIcon.CallbackMessage, 0, 0x405);
        Program.Check(opens == 2 && balloons == 1, "Tray double-click, keyboard activation and notification clicks retain their actions");
        // Show the existing native menu on the test monitor, not at the owner's mouse position.
        var screen = Screen.AllScreens.FirstOrDefault(s => !s.Primary);
        bool interactive = Environment.GetEnvironmentVariable("DESKWEAVE_UI_ALLOW_FOREGROUND") == "1";
        if (screen is not null && !TestScreen.OwnerFullscreen && interactive)
        {
            long anchor = (ushort)(screen.WorkingArea.Left + 30) | ((long)(ushort)(screen.WorkingArea.Top + 30) << 16);
            SendMessage(tray.Handle, TrayIcon.CallbackMessage, (IntPtr)anchor, 0x7b);
            Program.Check(menu.Visible, "The native tray callback opens the existing context menu");
            menu.Close();
            Program.Check(calls[^1].Operation == 3, "Closing the tray menu restores notification-area keyboard navigation");
        }
        else
            System.IO.File.WriteAllText(System.IO.Path.Combine(Program.Output, "tray-menu-skipped.txt"),
                "Native tray popup activation and its close callback were not tested: this explicitly foreground-taking check requires "
                + "DESKWEAVE_UI_ALLOW_FOREGROUND=1, a spare monitor, and no owner fullscreen app. Native icon registration checks still run.");

        tray.ShowBalloonTip(new string('x', 70), new string('y', 300));
        Program.Check(calls[^1] is { Operation: 1, Data.Title.Length: 63, Data.Info.Length: 255 }
            && entries.Count == 1, "Long notifications are bounded and modify the existing tray icon");
        entries.Clear(); // Explorer restarted; deliver the actual registered window message
        SendMessage(tray.Handle, TrayIcon.TaskbarCreated, 0, 0);
        SendMessage(tray.Handle, TrayIcon.TaskbarCreated, 0, 0);
        Program.Check(entries.Count == 1 && successfulAdds == 2,
            "Explorer-restart messages restore one tray icon, even when repeated");

        available = false;
        entries.Clear();
        tray.Register();
        available = true;
        await Task.Delay(2300);
        Program.Check(entries.Count == 1, "A temporarily unavailable notification area recovers without restarting Deskweave");
        int healthyCalls = calls.Count;
        await Task.Delay(2300);
        Program.Check(calls.Count == healthyCalls, "A healthy tray has no background retry polling");
        available = false;
        tray.Register(); // dispose while a retry is pending
        available = true;
        tray.Dispose();
        int disposedCalls = calls.Count;
        tray.Dispose();
        tray.Register();
        tray.ShowBalloonTip("late", "queued after exit");
        await Task.Delay(2300);
        Program.Check(entries.Count == 0 && menu.IsDisposed && calls.Count == disposedCalls,
            "Shutdown removes the tray entry once and cancels pending recovery and notifications");

        // The probe has its own executable/path GUID. Never claims or deletes the owner's icon.
        var nativeCalls = new List<string>();
        bool NativeNotify(uint operation, ref TrayIcon.IconData data)
        {
            bool ok = Shell_NotifyIcon(operation, ref data);
            nativeCalls.Add($"{operation}:{ok} hwnd={data.Window} size={data.Size}");
            return ok;
        }
        using (var native = new TrayIcon(icon, new ContextMenuStrip(), identity, NativeNotify))
        {
            if (!await WaitForIcon(identity)) throw new InvalidOperationException("Native tray: " + string.Join(", ", nativeCalls));
            Program.Check(true, "The real Windows notification area accepts the stable tray identity");
            for (int i = 0; i < 40; i++) native.Register();
            Program.Check(Exists(identity), "Forty native refreshes retain the same real tray identity");
        }
        Program.Check(!Exists(identity), "Normal disposal removes the real Windows tray entry");
        for (int i = 0; i < 40; i++)
        {
            using var native = new TrayIcon(icon, new ContextMenuStrip(), identity);
            // Explorer lays out a newly added icon asynchronously, after Shell_NotifyIcon returns.
            if (!await WaitForIcon(identity)) throw new InvalidOperationException("Native tray relaunch failed at " + i);
        }
        Program.Check(!Exists(identity), "Forty native create/dispose cycles leave no tray entry behind");
    }

    static async Task<bool> WaitForIcon(Guid identity)
    {
        for (int i = 0; i < 60; i++)
        {
            if (Exists(identity)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    internal static bool Exists(Guid identity)
    {
        var id = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Identity = identity };
        // S_FALSE is also success: the icon is tucked into the collapsed overflow, not missing.
        return Shell_NotifyIconGetRect(ref id, out _) >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Identity; }
    [StructLayout(LayoutKind.Sequential)]
    struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref Identifier identifier, out Rectangle rectangle);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint operation, ref TrayIcon.IconData data);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
