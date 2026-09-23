using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Three review findings on the workspace browser, each checked on a hidden workspace desktop:
/// a url that looks like a Chrome switch stays a url on a cold start, sound from the workspace
/// browser stays silent (it runs in its own job, which the workspace's audio sweep never reaches),
/// and the browser's window is the browser's own even when another Chromium window from a
/// different process sits above it.
///
/// Run: Deskweave.Probe.exe --browser-hardening "C:\absolute\output"
/// </summary>
internal static class BrowserHardeningProbe
{
    const int InjectedPort = 9338;

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Browser hardening probe exceeded its 180-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        var checks = new List<object>();
        void Check(bool passed, string name, object? detail = null) => checks.Add(new { passed, name, detail });
        string? failure = null;
        try
        {
            // 1. A switch-shaped url must not open a DevTools port (or change anything else).
            using (AgentDesktop desktop = AgentDesktop.Create("harden-" + Guid.NewGuid().ToString("N")[..8]))
            {
                bool before = Listening(InjectedPort);
                using WorkspaceBrowser? browser = WorkspaceBrowser.Start(desktop,
                    $"--remote-debugging-port={InjectedPort}").GetAwaiter().GetResult();
                Thread.Sleep(1500);
                bool after = Listening(InjectedPort);
                Check(!before && !after, "A url shaped like a Chrome switch does not become one on a cold start",
                    new { before, after, started = browser is not null });
            }

            // 2. Normal urls and about:blank still open, and a page's sound stays silent.
            using (AgentDesktop desktop = AgentDesktop.Create("harden-" + Guid.NewGuid().ToString("N")[..8]))
            {
                using (WorkspaceBrowser? blank = WorkspaceBrowser.Start(desktop, "about:blank").GetAwaiter().GetResult())
                {
                    string where = blank is null ? "" : blank.Evaluate("location.href").GetAwaiter().GetResult();
                    Check(where == "about:blank", "about:blank still opens", where);
                }
                string page = Path.Combine(desktop.Folder!, "sound.html");
                // Nearly silent, so a leak before the fix is not loud; it still opens an audio session.
                File.WriteAllText(page, """
                    <!doctype html><title>sound</title>
                    <button id="play" style="width:300px;height:200px" onclick="
                      const c = new AudioContext(), o = c.createOscillator(), g = c.createGain();
                      g.gain.value = 0.002; o.connect(g); g.connect(c.destination); o.start();
                      document.title = 'playing';">play</button>
                    """);
                using WorkspaceBrowser? browser = WorkspaceBrowser.Start(desktop, new Uri(page).AbsoluteUri).GetAwaiter().GetResult();
                string title = browser is null ? "" : browser.Evaluate("document.title").GetAwaiter().GetResult();
                Check(title == "sound", "A normal file url still opens", title);
                bool clicked = browser?.Click("#play").GetAwaiter().GetResult() == true;
                string state = browser is null ? "" : browser.Evaluate("document.title").GetAwaiter().GetResult();
                // Louder than silence means it reached the speakers. Sessions are the browser's own,
                // measured by the Windows meter; none at all is also silence.
                float peak = 0;
                int sessions = 0;
                for (int wait = 0; wait < 20; wait++)
                {
                    Thread.Sleep(250);
                    // Core Audio from the pool: the probe's main thread is WPF's single-threaded apartment.
                    (int count, float loudest) = Task.Run(() => Meter(desktop.OwnsProcess)).GetAwaiter().GetResult();
                    sessions = Math.Max(sessions, count);
                    peak = Math.Max(peak, loudest);
                }
                // The workspace sweep must leave chrome.exe's session alone: Windows remembers that
                // mute for the owner's own Chrome too.
                Check(clicked && state == "playing" && peak == 0 && desktop.AudioSessionsMuted == 0,
                    "Sound from the workspace browser stays silent without muting chrome.exe in Windows",
                    new { clicked, state, sessions, peak, desktop.AudioSessionsMuted });
            }

            // 3. Another Chromium window from a different process, above the browser, is not picked.
            using (AgentDesktop desktop = AgentDesktop.Create("harden-" + Guid.NewGuid().ToString("N")[..8]))
            {
                using WorkspaceBrowser? browser = WorkspaceBrowser.Start(desktop, "about:blank").GetAwaiter().GetResult();
                string decoyProfile = Path.Combine(desktop.Folder!, "decoy-profile");
                int decoy = desktop.Launch(WorkspaceBrowser.ChromePath,
                    $"--no-first-run --no-default-browser-check --user-data-dir=\"{decoyProfile}\" about:blank");
                string top = "";
                for (int wait = 0; wait < 40; wait++)
                {
                    Thread.Sleep(250);
                    AgentWindow? first = desktop.Windows().FirstOrDefault(w => w.ClassName.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal));
                    if (first is null) continue;
                    Native.GetWindowThreadProcessId(first.Handle, out int owner);
                    top = owner == decoy ? "decoy" : "browser";
                    if (top == "decoy") break;
                }
                nint window = browser?.Window ?? 0;
                int windowOwner = 0;
                if (window != 0) Native.GetWindowThreadProcessId(window, out windowOwner);
                Check(browser is not null && window != 0 && windowOwner == browser.ProcessId,
                    "The browser window belongs to the browser, not another Chromium process",
                    new { decoy, browserPid = browser?.ProcessId, windowOwner, top });
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        bool passed = failure is null && checks.All(c => (bool)c.GetType().GetProperty("passed")!.GetValue(c)!);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed, checks, failure },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    /// <summary>How many render sessions belong to the workspace, and the loudest of them right now.</summary>
    static (int Count, float Peak) Meter(Predicate<int> owned)
    {
        int count = 0;
        float peak = 0;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0) return (0, 0);
        Guid id = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref id, 23, 0, out object raw) != 0) return (0, 0);
        if (((IAudioSessionManager2)raw).GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return (0, 0);
        sessions.GetCount(out int total);
        for (int i = 0; i < total; i++)
        {
            if (sessions.GetSession(i, out IAudioSessionControl2 session) != 0) continue;
            if (session.GetProcessId(out int pid) != 0 || !owned(pid)) continue;
            count++;
            if (session is IAudioMeterInformation meter && meter.GetPeakValue(out float value) == 0)
                peak = Math.Max(peak, value);
        }
        return (count, peak);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotImplementedEnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid id, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int NotImplementedGetAudioSessionControl();
        int NotImplementedGetSimpleAudioVolume();
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int NotImplemented1(); int NotImplemented2(); int NotImplemented3(); int NotImplemented4(); int NotImplemented5();
        int NotImplemented6(); int NotImplemented7(); int NotImplemented8(); int NotImplemented9();
        int NotImplementedGetSessionIdentifier(); int NotImplementedGetSessionInstanceIdentifier();
        int GetProcessId(out int processId);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected;
        }
        catch (Exception) { return false; }
    }
}
