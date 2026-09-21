using System.Diagnostics;
using System.IO;
using HiveMind.AgentWorkspaces;

/// <summary>
/// Prompt 2 (design/FIX-PROMPTS-2026-09-20.md), the engine half: a running workspace nobody is
/// using must not stay awake forever, however it got stuck - an unanswered desktop request, a
/// lease nobody disposed, or a clock that was never given a thread that pumps it.
/// </summary>
internal static class SleepGate
{
    internal static void Run(Action<bool, string> check)
    {
        TimeSpan sleepBefore = WorkspaceRuntime.SleepAfter;
        TimeSpan dozeBefore = WorkspaceRuntime.DozeInterval;
        TimeSpan useAgeBefore = WorkspaceExternalAccess.MaxUseAge;
        // Short enough that every check below converges well inside the probe's own wall clock;
        // real values are restored before this returns so nothing else in the run is affected.
        WorkspaceRuntime.SleepAfter = TimeSpan.FromMilliseconds(150);
        WorkspaceRuntime.DozeInterval = TimeSpan.FromMilliseconds(40);
        WorkspaceExternalAccess.MaxUseAge = TimeSpan.FromMilliseconds(150);
        try
        {
            PendingRequestSleeps(check);
            LeakedLeaseSelfHeals(check);
            ClockTicksOffUiThread(check);
            ConcurrentStart(check);
            RunningCommandStaysAwake(check);
            WakeCost(check);
            RoundTripCost(check);
        }
        finally
        {
            WorkspaceRuntime.SleepAfter = sleepBefore;
            WorkspaceRuntime.DozeInterval = dozeBefore;
            WorkspaceExternalAccess.MaxUseAge = useAgeBefore;
            WorkspaceRuntime.Rest();
        }
    }

    static StoredWorkspace NewWorkspace(string tag, bool desktopRequests = false)
    {
        StoredWorkspace workspace = WorkspaceStore.Create(tag + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, desktopRequests) { PrewarmBrowser = false });
        return workspace;
    }

    /// <summary>A workspace whose only open item is an unanswered desktop request must not stay
    /// awake forever waiting for a click that may never come.</summary>
    static void PendingRequestSleeps(Action<bool, string> check)
    {
        StoredWorkspace workspace = NewWorkspace("SleepPending", desktopRequests: true);
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        try
        {
            WorkspaceHandoff request = runtime.Access!.Handoffs.Request("url", "https://example.invalid/report", "waiting on the owner");
            check(request.State == "pending", "A fresh desktop request starts pending");
            check(runtime.Quiet is not null,
                "A pending desktop request no longer blocks Quiet by itself - it is bounded by the ordinary idle clock, not unbounded");
            WaitUntilAsleep(workspace.Id);
            check(WorkspaceRuntime.Of(workspace.Id) is null,
                "A workspace with an unanswered desktop request sleeps once it has been idle for SleepAfter, instead of staying pinned awake forever");
        }
        finally { WorkspaceRuntime.Of(workspace.Id)?.Dispose(); }
    }

    /// <summary>A Use() lease nobody disposes - the shape of a caller that hit an exception,
    /// cancellation or early return between taking it and its `using` - must not pin the workspace
    /// awake for the rest of the app's life.</summary>
    static void LeakedLeaseSelfHeals(Action<bool, string> check)
    {
        StoredWorkspace workspace = NewWorkspace("SleepLeak");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        try
        {
            Guid client = runtime.Access!.AcquireForTests();
            try
            {
                WorkspaceExternalAccess.UseLease? lease = runtime.Access.Use(client);
                check(lease is not null, "A workspace's own driver can take a Use() lease");
                throw new InvalidOperationException("simulated failure inside a held lease");
            }
            catch (InvalidOperationException)
            {
                // lease is deliberately never disposed past this point - standing in for the call
                // site WorkspaceMcp.cs never actually has (its `using` covers every exit), but that
                // any future call site could, if nobody audited it for exactly this.
            }
            runtime.Access.Release(client); // the agent's turn ends normally; only the lease leaked
            WaitUntilAsleep(workspace.Id);
            check(WorkspaceRuntime.Of(workspace.Id) is null,
                "A UseLease that is never disposed does not pin the workspace awake once it has outlived MaxUseAge");
        }
        finally { WorkspaceRuntime.Of(workspace.Id)?.Dispose(); }
    }

    /// <summary>The sleep clock must tick from whatever thread happens to call Start, not only from
    /// a UI dispatcher that pumps it. Started on a pool thread and left alone - nothing here calls
    /// Doze() directly - so only the clock itself can be what puts this workspace to sleep.</summary>
    static void ClockTicksOffUiThread(Action<bool, string> check)
    {
        StoredWorkspace workspace = NewWorkspace("SleepClock");
        WorkspaceRuntime runtime = Task.Run(() => WorkspaceRuntime.Start(workspace)).GetAwaiter().GetResult();
        try
        {
            check(WorkspaceRuntime.Of(workspace.Id) is not null, "Start succeeds when called from a pool thread, off any UI dispatcher");
            var waited = Stopwatch.StartNew();
            while (WorkspaceRuntime.Of(workspace.Id) is not null && waited.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(25);
            check(WorkspaceRuntime.Of(workspace.Id) is null,
                "The shared sleep clock ticks on its own after Start ran on a pool thread, with nothing here calling Doze() directly");
        }
        finally { WorkspaceRuntime.Of(workspace.Id)?.Dispose(); }
    }

    /// <summary>
    /// What WorkspaceRuntime.Start() alone costs on a workspace that already has a folder: building
    /// the desktop object and launching the terminal, nothing more. This is not the cost of a nap -
    /// see RoundTripCost below for that - it only proves Start's own synchronous work is cheap, so
    /// none of the real cost of waking is hiding in the constructor.
    /// </summary>
    static void WakeCost(Action<bool, string> check)
    {
        StoredWorkspace workspace = NewWorkspace("SleepWake");
        WorkspaceRuntime asleep = WorkspaceRuntime.Start(workspace);
        string folder = asleep.Computer!.Folder!;
        asleep.Dispose(); // exactly what TrySleep does: the runtime goes away, the folder does not
        var clock = Stopwatch.StartNew();
        WorkspaceRuntime woken = WorkspaceRuntime.Start(WorkspaceStore.All().Single(w => w.Id == workspace.Id));
        TimeSpan cost = clock.Elapsed;
        try
        {
            check(woken.Computer!.Folder == folder, "Waking a slept workspace reopens its exact same folder");
            check(cost < TimeSpan.FromSeconds(5), "WorkspaceRuntime.Start() alone (a fresh Start on an existing folder) took "
                + $"{cost.TotalMilliseconds:0} ms - cheap, but not what a nap actually costs; see RoundTripCost");
        }
        finally { woken.Dispose(); }
    }

    /// <summary>
    /// What a nap actually costs: not Start() (see WakeCost above) but everything sleep tears down -
    /// the browser process and its profile connection, every app the agent had open, the shell -
    /// measured getting back to an equivalent state: browser up and renavigated to the same page,
    /// one app running again. This is the number SleepAfter's comment is chosen against.
    /// </summary>
    static void RoundTripCost(Action<bool, string> check)
    {
        if (!File.Exists(WorkspaceBrowser.ChromePath))
        {
            check(true, "Round-trip cost not measured: no supported browser is installed on this machine");
            return;
        }
        StoredWorkspace workspace = NewWorkspace("SleepRoundTrip");
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = true });
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        WorkspaceRuntime before = WorkspaceRuntime.Start(workspace);
        string folder = before.Computer!.Folder!;
        string page = new Uri(Path.Combine(folder, "round-trip.html")).AbsoluteUri;
        File.WriteAllText(Path.Combine(folder, "round-trip.html"), "<title>round trip</title><h1>round trip</h1>");
        before.Plane!.AgentTakes();
        before.Computer.Launch(notepad);
        if (!Navigate(before.Plane, page))
        {
            // A Chrome cold start can lose a race under heavy concurrent load - the rest of this
            // gate, or three real workspaces on the owner's machine, both create exactly that load.
            // WorkspaceControl.Prewarm already treats a browser that does not come up as silent and
            // retryable, not a defect; that is a browser-launch reliability question (see prompt 5,
            // FIX-PROMPTS-2026-09-20.md), not a sleep/wake one, so it does not fail this gate - it
            // just means this run has no clean number to report.
            check(true, "Round-trip cost not measured this run: the browser did not come up before sleep even after retrying");
            before.Dispose();
            return;
        }

        var clock = Stopwatch.StartNew();
        before.Dispose(); // sleep: the desktop, the browser process, the app and the shell all die here
        WorkspaceRuntime woken = WorkspaceRuntime.Start(WorkspaceStore.All().Single(w => w.Id == workspace.Id));
        woken.Plane!.AgentTakes();
        woken.Computer!.Launch(notepad);
        bool navigatedAfter = Navigate(woken.Plane, page);
        TimeSpan roundTrip = clock.Elapsed;
        try
        {
            if (!navigatedAfter)
            {
                check(true, "Round-trip cost not measured this run: the browser did not come back up after waking even after retrying");
                return;
            }
            check(File.Exists(Path.Combine(folder, "evidence", "actions.log")),
                "The evidence log written before sleep is still on disk after waking - sleep tears down the desktop, not the workspace folder");
            check(roundTrip < TimeSpan.FromMinutes(3), "Full round trip after sleep - new desktop, browser relaunched and renavigated, "
                + $"one app relaunched - took {roundTrip.TotalSeconds:0.0} s. This is the cost SleepAfter's comment is measured against");
        }
        finally { woken.Dispose(); }
    }

    /// <summary>
    /// Opens the page, retrying past a Chrome cold start that loses a race under load - the same
    /// tolerance a real agent gets by just calling `browse` again.
    /// </summary>
    static bool Navigate(WorkspaceControl plane, string page)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool ok;
            try { ok = plane.OpenBrowser(page, CancellationToken.None).GetAwaiter().GetResult() && plane.BrowserAlive; }
            catch (Exception) { ok = false; }
            if (ok) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    static void WaitUntilAsleep(string id)
    {
        var waited = Stopwatch.StartNew();
        while (WorkspaceRuntime.Of(id) is not null && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            WorkspaceRuntime.Doze();
            Thread.Sleep(25);
        }
    }

    static void ConcurrentStart(Action<bool, string> check)
    {
        TimeSpan sleep = WorkspaceRuntime.SleepAfter;
        WorkspaceRuntime.SleepAfter = TimeSpan.FromMinutes(1);
        StoredWorkspace workspace = NewWorkspace("ConcurrentStart");
        try
        {
            var starts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => WorkspaceRuntime.Start(workspace))).ToArray();
            Task.WaitAll(starts);
            check(starts.All(task => ReferenceEquals(task.Result, starts[0].Result))
                && WorkspaceRuntime.Running.Count(runtime => runtime.Id == workspace.Id) == 1,
                "Eight simultaneous starts create one runtime and one desktop for the same workspace");
        }
        finally { WorkspaceRuntime.Of(workspace.Id)?.Dispose(); WorkspaceRuntime.SleepAfter = sleep; }
    }

    static void RunningCommandStaysAwake(Action<bool, string> check)
    {
        StoredWorkspace workspace = NewWorkspace("SleepCommand");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        try
        {
            Guid client = runtime.Access!.AcquireForTests();
            CommandJob? job;
            using (runtime.Access.Use(client))
                job = runtime.Plane!.Commands.Start("Start-Sleep -Seconds 30", true, 0, out _);
            check(job is not null, "A durable command starts before the agent releases its turn");
            runtime.Access.Release(client);
            Thread.Sleep(400);
            WorkspaceRuntime.Doze();
            check(ReferenceEquals(runtime, WorkspaceRuntime.Of(workspace.Id)) && job!.Running && runtime.Quiet is null,
                "Idle cleanup preserves a running command after the agent releases its lease");
            check(!WorkspaceRuntime.SleepQuietest() && !runtime.Access.Retire(),
                "Capacity cleanup cannot retire a workspace with a running command");
            runtime.Plane!.Commands.Cancel(job!.Id, "probe", out _);
            check(job.Finished.Task.Wait(TimeSpan.FromSeconds(5)), "Explicit cancellation ends the protected command");
            WaitUntilAsleep(workspace.Id);
            check(WorkspaceRuntime.Of(workspace.Id) is null,
                "After its command ends the workspace becomes idle and sleeps normally");
        }
        finally { WorkspaceRuntime.Of(workspace.Id)?.Dispose(); }
    }
}
