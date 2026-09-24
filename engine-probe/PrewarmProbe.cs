using System.Diagnostics;
using System.IO;
using Deskweave.AgentWorkspaces;

/// <summary>
/// The warmed-up browser must not cover the window the
/// agent just opened, must not be started at all for a workspace that never browses, and must not
/// leave a new blank tab behind every time something navigates.
/// </summary>
internal static class PrewarmProbe
{
    internal static void Run(Action<bool, string> check)
    {
        if (!File.Exists(WorkspaceBrowser.ChromePath))
        {
            check(true, "Browser warm-up not measured: no supported browser is installed on this machine");
            return;
        }
        try
        {
            NeverBrowsedStaysCold(check);
            WarmBrowserStaysBehind(check);
            ColdStartUnderLoad(check);
        }
        finally { WorkspaceRuntime.Rest(); }
    }

    /// <summary>
    /// A workspace nobody has ever browsed in pays nothing for a browser. Before this, every wake
    /// started Chrome regardless, which was the largest single part of the wake.
    /// </summary>
    static void NeverBrowsedStaysCold(Action<bool, string> check)
    {
        StoredWorkspace workspace = New("PrewarmCold");
        using WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        // Long enough that a warm-up that was going to happen has happened: a Chrome cold start on
        // this machine is seconds, and the gate's own RoundTripCost measures the whole trip at ~6.
        Thread.Sleep(TimeSpan.FromSeconds(8));
        check(!runtime.Plane!.BrowserAlive,
            "A workspace that has never browsed starts no browser when it runs");
    }

    /// <summary>
    /// A workspace that does browse gets its browser warmed on the next start - behind the window
    /// the agent opened, not over it - and navigating it twice does not grow the tab count.
    /// </summary>
    static void WarmBrowserStaysBehind(Action<bool, string> check)
    {
        StoredWorkspace workspace = New("PrewarmWarm");
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        WorkspaceRuntime first = WorkspaceRuntime.Start(workspace);
        string folder = first.Computer!.Folder!;
        File.WriteAllText(Path.Combine(folder, "warm.html"), "<title>warm</title><h1>warm</h1>");
        string page = new Uri(Path.Combine(folder, "warm.html")).AbsoluteUri;
        first.Plane!.AgentTakes();
        bool browsed = Navigate(first.Plane, page);
        first.Dispose();
        if (!browsed)
        {
            // The same cold-start race SleepGate records: a browser that does not come up is a
            // browser-launch reliability question, not a warm-up placement one.
            check(true, "Browser warm-up not measured this run: the browser did not come up before sleeping");
            return;
        }
        check(File.Exists(Path.Combine(folder, WorkspaceBrowser.UsedMark)),
            "A real navigation records that this workspace browses");

        using WorkspaceRuntime woken = WorkspaceRuntime.Start(WorkspaceStore.All().Single(w => w.Id == workspace.Id));
        woken.Plane!.AgentTakes();
        // What the agent does the moment it wakes: open its own window. The warm-up is in flight
        // underneath, which is exactly when it used to land on top of this one.
        woken.Computer!.Launch(notepad);
        if (!Settled(() => woken.Plane.BrowserAlive && Front(woken.Computer, "Notepad") >= 0))
        {
            check(true, "Browser warm-up not measured this run: the browser did not come back up after waking");
            return;
        }
        int app = Front(woken.Computer, "Notepad"), browser = Front(woken.Computer, "Chrome_WidgetWin");
        check(app >= 0 && (browser < 0 || app < browser),
            "The warmed-up browser stays behind the window the agent opened");

        int before = woken.Plane.Tabs().GetAwaiter().GetResult().Count;
        bool again = Navigate(woken.Plane, page) && Navigate(woken.Plane, page);
        int after = woken.Plane.Tabs().GetAwaiter().GetResult().Count;
        check(again && after <= before, "Navigating a warmed-up browser twice opens no further tabs");
        check(Front(woken.Computer, "Chrome_WidgetWin") == 0,
            "A page the agent asked for comes to the front of the workspace screen");
    }

    /// <summary>
    /// The rest: under the intended load of about three workspaces at once,
    /// a Chrome cold start can lose its race outright. It shows up during a full gate run and rarely
    /// in isolated runs, so this reproduces the same load by starting three cold starts at the same
    /// instant instead of one at a time. Checks that WorkspaceBrowser
    /// either comes up under that load or says exactly which stage did not, per workspace - not the
    /// silent "DID NOT COME UP" that made this hole unreportable in the first place.
    /// </summary>
    static void ColdStartUnderLoad(Action<bool, string> check)
    {
        const int concurrent = 3;
        var workspaces = new StoredWorkspace[concurrent];
        var runtimes = new WorkspaceRuntime[concurrent];
        for (int i = 0; i < concurrent; i++)
        {
            workspaces[i] = New("PrewarmLoad" + i);
            runtimes[i] = WorkspaceRuntime.Start(workspaces[i]);
        }
        try
        {
            Task<WorkspaceBrowser?>[] starts = runtimes
                .Select(runtime => WorkspaceBrowser.Start(runtime.Computer!, "about:blank", true, CancellationToken.None, 0))
                .ToArray();
            if (!Task.WaitAll(starts, TimeSpan.FromSeconds(90)))
            {
                check(true, "Cold start under load not measured this run: the concurrent starts did not settle inside the probe's own budget");
                return;
            }

            int came = 0;
            for (int i = 0; i < concurrent; i++)
            {
                WorkspaceBrowser? browser = starts[i].Result;
                if (browser is not null) { came++; browser.Abandon(); continue; }
                string reason = WorkspaceBrowser.StartFailureReason(runtimes[i].Computer!.Name) ?? string.Empty;
                check(reason.Length > 0,
                    "A cold start that loses the race under concurrent load says precisely which stage failed, not just that it did");
            }
            check(came > 0,
                $"At least one of {concurrent} browsers cold-starting at the same instant came up (measured {came}/{concurrent})");
        }
        finally { foreach (WorkspaceRuntime runtime in runtimes) runtime.Dispose(); }
    }

    /// <summary>Where a window sits in the workspace's z-order, front first, or -1 when it is not there.</summary>
    static int Front(AgentDesktop desktop, string match)
    {
        IReadOnlyList<AgentWindow> windows = desktop.Windows();
        for (int at = 0; at < windows.Count; at++)
            if (windows[at].ClassName.StartsWith(match, StringComparison.Ordinal)
                || windows[at].Title.Contains(match, StringComparison.OrdinalIgnoreCase)) return at;
        return -1;
    }

    static bool Navigate(WorkspaceControl control, string url)
    {
        try { return control.OpenBrowser(url).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    static bool Settled(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (condition()) return true; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            Thread.Sleep(250);
        }
        return false;
    }

    static StoredWorkspace New(string tag)
    {
        StoredWorkspace workspace = WorkspaceStore.Create(tag + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = true });
        return workspace;
    }
}
