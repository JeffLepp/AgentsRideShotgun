using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// Shutdown and first run: quitting over setup threw past OnExit, the lock was let go before Exit
/// deleted the data folders, a launch during a slow quit vanished, and a cut-off last picture took
/// down the workspace page; a downloaded update applied under an agent's background start, and the
/// version Settings shows lagged the release. Each runs on the real code with the probe's own lock names and store;
/// no window is shown.
/// </summary>
static class LifecycleChecks
{
    internal static async Task Run()
    {
        await OwnerIntegrationChecks.Run();
        Program.Check(string.Equals(ControllerProcess.ImagePath(Environment.ProcessId), Environment.ProcessPath,
                StringComparison.OrdinalIgnoreCase) && ControllerProcess.ImagePath(int.MaxValue) is null,
            "Limited-rights process inspection identifies this executable and tolerates a departed process");
        string feed = Path.Combine(Program.Output, "local-feed");
        Directory.CreateDirectory(feed);
        Program.Check(Updates.LocalSource(feed) == Path.GetFullPath(feed), "Lifecycle updates accept an explicit existing local feed folder");
        foreach (string invalid in new[] { "relative-feed", "https://example.invalid/feed", @"\\server\share\feed",
            "file:///C:/feed", @"\??\UNC\server\share\feed", "//server/share/feed", @"/\server\share\feed" })
        {
            bool refused = false;
            try { _ = Updates.LocalSource(invalid); }
            catch (ArgumentException) { refused = true; }
            Program.Check(refused, "Lifecycle update source refuses " + invalid);
        }
        // Quitting closes the hub before setup; setup's Closed handler must not show it again.
        var hub = new Window();
        hub.Close();
        bool threw = false;
        try { hub.Show(); }
        catch (InvalidOperationException) { threw = true; }
        bool quiet = true;
        try { App.ShowHubAfterFirstRun(hub, quitting: true); }
        catch (InvalidOperationException) { quiet = false; }
        Program.Check(threw && quiet, "Closing setup while ARS quits leaves the closed hub alone");

        string name = @"Local\Deskweave.Probe.Instance." + Guid.NewGuid().ToString("N");
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");

        // Exit ("Delete all data", Uninstall) runs while this instance still holds the lock.
        var owned = new Mutex(false, name);
        owned.WaitOne();
        bool heldDuringExit = false;
        App.ExitHoldingInstance(() => heldDuringExit = !Task.Run(() => Free(name)).Result, owned, owns: true);
        Program.Check(heldDuringExit && await Task.Run(() => Free(name)),
            "The single-instance lock is held through Exit's folder delete and released after it");

        // A running ARS takes the request and the new launch ends.
        using (var holder = Holder(name, listen: true, releaseAfter: TimeSpan.FromSeconds(3)))
        using (var mine = new Mutex(false, name))
        {
            holder.Ready.Wait();
            bool claimed = App.ClaimInstance(mine, activate, background: false, TimeSpan.FromMilliseconds(500));
            Program.Check(!claimed && holder.Shown.Wait(2000), "A second launch shows the running ARS and ends");
        }
        // A background start asks for nothing.
        using (var holder = Holder(name, listen: false, releaseAfter: TimeSpan.FromSeconds(3)))
        using (var mine = new Mutex(false, name))
        {
            holder.Ready.Wait();
            bool claimed = App.ClaimInstance(mine, activate, background: true, TimeSpan.FromMilliseconds(500));
            Program.Check(!claimed && !activate.WaitOne(0), "A background start beside a running ARS asks for no window");
        }
        // One that is quitting stopped listening; the launch waits for its lock and starts normally.
        using (var holder = Holder(name, listen: false, releaseAfter: TimeSpan.FromMilliseconds(600)))
        using (var mine = new Mutex(false, name))
        {
            holder.Ready.Wait();
            bool claimed = App.ClaimInstance(mine, activate, background: false, TimeSpan.FromSeconds(5));
            Program.Check(claimed && !activate.WaitOne(0), "A launch during a slow quit waits for the lock and starts, with no stale request left");
            if (claimed) mine.ReleaseMutex();
        }

        // A downloaded update is applied only by the owner's own start with nothing running: never
        // under an agent's bridge (--background) or beside a running copy.
        bool ownerStart = Deskweave.Program.ApplyUpdateOnStart([], name);
        bool bridgeStart = Deskweave.Program.ApplyUpdateOnStart([StartWithWindows.Background], name);
        bool besideRunning;
        using (var running = new Mutex(false, name)) besideRunning = Deskweave.Program.ApplyUpdateOnStart([], name);
        Program.Check(ownerStart && !bridgeStart && !besideRunning,
            "Updates apply on the owner's start only, not on a background start or beside a running ARS");
        var legacyStart = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Bridge", "ARS.WorkspaceBridge.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        legacyStart.ArgumentList.Add("--workspace");
        legacyStart.ArgumentList.Add(Path.Combine(Program.Output, "offline-legacy-bridge.json"));
        using (Process legacy = Process.Start(legacyStart) ?? throw new IOException("Could not start the legacy bridge fixture."))
        {
            try
            {
                Program.Check(!Deskweave.Program.ApplyUpdateOnStart([], name) && Updates.DeferredReason().Contains("legacy bridge"),
                    "Startup and the tray both defer updates while a bridge runs from the installed payload");
            }
            finally
            {
                legacy.StandardInput.Close();
                if (!legacy.WaitForExit(10000)) legacy.Kill();
            }
        }

        // No windows or real restart: exercise the production request/defer/retry transaction.
        int scheduled = 0, shutdown = 0;
        Updates.ScheduleForTests = operation => { if (operation == "restart") scheduled++; };
        Updates.Start((_, _) => { }, () => shutdown++);
        try
        {
            StoredWorkspace busy = WorkspaceStore.Create("Restart safety fixture");
            WorkspaceAccessStore.Write(busy.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
            using (var runtime = WorkspaceRuntime.Start(busy))
            {
                string deferred = await Updates.Request("restart");
                Program.Check(deferred.StartsWith("Deferred:", StringComparison.Ordinal) && scheduled == 0
                    && !WorkspaceRuntime.Maintenance && ReferenceEquals(WorkspaceRuntime.Of(busy.Id), runtime),
                    "A restart defers even for an idle running workspace and never closes its apps");
                Program.Check(await Updates.Request("status") == deferred, "Controller status explains the deferred restart");
                WorkspaceRouter.Start();
                Program.Check(await Task.Run(() => ControllerLifecycle.Command("status", null)) == 0,
                    "The CLI lifecycle transport authenticates to the fixture router and reads deferred status");
            }
            Updates.TryRestart();
            Program.Check(scheduled == 1 && WorkspaceRuntime.Maintenance && Updates.Status.StartsWith("Accepted:", StringComparison.Ordinal),
                "Stopping the last workspace lets the queued restart pass the atomic gate exactly once");
            Updates.TryRestart();
            await Task.Delay(650);
            Program.Check(scheduled == 1 && shutdown == 1 && !File.Exists(WorkspaceAccessStore.QuitMark),
                "Restart acknowledges before graceful shutdown and leaves no owner-quit mark");
            WorkspaceStore.Delete(busy.Id);
        }
        finally { Updates.Stop(); Updates.ScheduleForTests = null; WorkspaceRuntime.CancelMaintenance(); WorkspaceRouter.Stop(); }

        // Settings shows the assembly version; it and the file version follow Version.
        Assembly app = typeof(App).Assembly;
        string product = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0].Split('-')[0];
        string fileVersion = app.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        Program.Check(app.GetName().Version?.ToString() == product + ".0" && fileVersion == product + ".0",
            "The version Settings shows and the file version match the release version " + product);

        // A truncated last picture reads as no picture, on the hub and on the workspace page.
        StoredWorkspace cut = WorkspaceStore.Create("Cut-off picture");
        try
        {
            string frame = WorkspaceStore.LastFrameOf(cut.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, new byte[64 * 64 * 4], 64 * 4)));
            using (var whole = new MemoryStream())
            {
                encoder.Save(whole);
                File.WriteAllBytes(frame, whole.ToArray()[..(int)(whole.Length / 2)]);
            }
            // The workspace page loads its last picture through this same call.
            BitmapSource? hubPicture = await HubLastLook.FullAsync(cut.Id);
            Program.Check(hubPicture is null, "A cut-off last picture shows nothing instead of crashing the workspace page");
        }
        finally { WorkspaceStore.Delete(cut.Id); }
    }

    /// <summary>Whether a new process could take the lock now.</summary>
    static bool Free(string name)
    {
        using var probe = new Mutex(false, name);
        try
        {
            if (!probe.WaitOne(0)) return false;
            probe.ReleaseMutex();
            return true;
        }
        catch (AbandonedMutexException) { probe.ReleaseMutex(); return true; }
    }

    /// <summary>Another ARS holding the lock on its own thread, listening for a show request or not.</summary>
    static Held Holder(string name, bool listen, TimeSpan releaseAfter)
    {
        var held = new Held(name, new ManualResetEventSlim(), new ManualResetEventSlim());
        new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
            mutex.WaitOne();
            held.Ready.Set();
            long until = Environment.TickCount64 + (long)releaseAfter.TotalMilliseconds;
            if (listen && activate.WaitOne(releaseAfter)) held.Shown.Set();
            Thread.Sleep((int)Math.Max(0, until - Environment.TickCount64));
            mutex.ReleaseMutex();
        }) { IsBackground = true }.Start();
        return held;
    }

    readonly record struct Held(string Name, ManualResetEventSlim Ready, ManualResetEventSlim Shown) : IDisposable
    {
        // Wait for the holder to let go so the next case starts from a free lock.
        public void Dispose() { for (int i = 0; i < 100 && !Free(Name); i++) Thread.Sleep(50); }
    }
}
