using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

static class StorageMvpChecks
{
    internal static async Task Run()
    {
        string root = Path.Combine(Program.Output, "storage-mvp-fixture");
        string data = Path.Combine(root, "data"), install = Path.Combine(root, "install"), workspaces = Path.Combine(data, "agent-workspaces");
        var running = new HashSet<string>();
        bool race = false;
        var store = new WorkspaceStorage(data, install, workspaces, running.Contains, (id, action) =>
        { if (race || running.Contains(id)) return false; return action(); });
        void Put(string path, int bytes, int days = 0)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[bytes]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days)); }
        string Folder(string id) => Path.Combine(workspaces, "ARS-" + id);
        void Workspace(string id, int age)
        {
            Directory.CreateDirectory(Folder(id));
            File.WriteAllText(Path.Combine(Folder(id), "workspace.json"), JsonSerializer.Serialize(new StoredWorkspace
            { Id = id, Name = id, LastUsed = DateTimeOffset.UtcNow.AddDays(-age) }));
        }
        Workspace("idle", 60); Workspace("live", 60); Workspace("fresh", 1);
        Directory.CreateDirectory(Folder("empty-recovered"));
        string cache = Path.Combine(Folder("idle"), "chrome-profile", "Default", "Cache", "Cache_Data", "entry");
        string cookie = Path.Combine(Folder("idle"), "chrome-profile", "Default", "Network", "Cookies");
        string indexed = Path.Combine(Folder("idle"), "chrome-profile", "Default", "IndexedDB", "important");
        string nestedCache = Path.Combine(Folder("idle"), "chrome-profile", "Default", "Local Storage", "Cache", "keep");
        string serviceWorker = Path.Combine(Folder("idle"), "chrome-profile", "Default", "Service Worker", "CacheStorage", "keep");
        Put(cache, 4096); Put(cookie, 100); Put(indexed, 101); Put(nestedCache, 102); Put(serviceWorker, 103);
        Put(Path.Combine(Folder("idle"), "project.txt"), 104);
        string liveCache = Path.Combine(Folder("live"), "chrome-profile", "Default", "Cache", "entry"); Put(liveCache, 2048);
        string liveLog = Path.Combine(Folder("live"), "evidence", "actions.log"); Put(liveLog, 99, 90);
        running.Add("live");
        Put(Path.Combine(install, "current", "ARS.dll"), 700);
        Directory.CreateDirectory(Path.Combine(install, "current"));
        File.WriteAllText(Path.Combine(install, "current", "sq.version"), "<package><metadata><version>0.3.5</version></metadata></package>");
        foreach (string version in new[] { "0.3.1", "0.3.2", "0.3.4", "0.3.5", "0.3.6" })
            Put(Path.Combine(install, "packages", "ARSApp-" + version + "-full.nupkg"), 1000);
        Put(Path.Combine(install, ".private-retained-recovery", "keep"), 1000);
        foreach (string version in new[] { "0.3.1", "0.3.2", "0.3.4", "0.3.5" }) Put(Path.Combine(install, "app-" + version, "app.dll"), 800);
        Put(Path.Combine(data, "bridge-hosts", "old-but-referenced", "host.exe"), 500);
        Put(Path.Combine(data, "logs", "old.log"), 120, 90);
        StorageReport report = store.Report();
        long expected = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Program.Check(report.TotalBytes == expected && report.Items.SelectMany(i => i.Files).Select(f => f.Path).Distinct().Count()
            == Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length, "Storage counts every fixture byte once, including current install, metadata and profiles");
        Program.Check(report.Items.Any(i => i.WorkspaceId == "empty-recovered" && i.Bytes == 0 && i.LastUsed is null),
            "Empty recovered workspaces are visible with zero bytes and unknown age");
        Program.Check(report.Items.Single(i => i.WorkspaceId == "idle" && i.Category == "caches").Bytes == 4096
            && report.Items.Single(i => i.WorkspaceId == "idle" && i.Category == "browser-profiles").Bytes == 406,
            "Only allowlisted Chromium caches are disposable; cookies, IndexedDB, Local Storage and service workers are user data");
        StorageCleanup dry = await store.Clean("caches");
        Program.Check(dry.PreviewBytes == 4096 && dry.FreedBytes == 0 && File.Exists(cache) && dry.Skipped.Any(s => s.Contains("live")), "Dry run reports exact eligible bytes and protects a running workspace");
        race = true;
        StorageCleanup raced = await store.Clean("caches", false);
        Program.Check(raced.FreedBytes == 0 && File.Exists(cache), "Workspace starting between scan and cleanup is protected by final admission check");
        race = false;
        StorageCleanup cleaned = await store.Clean("caches", false);
        Program.Check(cleaned.FreedBytes == 4096 && !File.Exists(cache) && File.Exists(liveCache) && File.Exists(cookie)
            && File.Exists(indexed) && File.Exists(nestedCache) && File.Exists(serviceWorker), "Real cache cleanup frees measured bytes and preserves running caches and signed-in profile data");
        string oldLog = Path.Combine(Folder("idle"), "evidence", "actions.log"); Put(oldLog, 4096, 90);
        StorageCleanup preview = await store.Clean("logs");
        Put(oldLog, 8192, 90); Put(Path.Combine(Folder("idle"), "evidence", "new.log"), 333);
        StorageCleanup reviewed = await store.Clean("logs", false, reviewed: preview.ReviewedFiles);
        Program.Check(File.Exists(oldLog) && File.Exists(Path.Combine(Folder("idle"), "evidence", "new.log"))
            && reviewed.Skipped.Length >= 2, "Confirmation cannot delete files added or changed after its preview");
        StorageCleanup retention = await store.Clean("retention", false);
        Program.Check(!File.Exists(oldLog) && File.Exists(liveLog) && File.Exists(cookie) && Directory.Exists(Folder("idle")),
            "Automatic retention expires old idle logs, preserves running logs, profiles and workspaces");
        StorageCleanup packages = await store.Clean("packages", false);
        Program.Check(packages.FreedBytes == 2000 && File.Exists(Path.Combine(install, "packages", "ARSApp-0.3.4-full.nupkg"))
            && File.Exists(Path.Combine(install, "packages", "ARSApp-0.3.5-full.nupkg")) && File.Exists(Path.Combine(install, "packages", "ARSApp-0.3.6-full.nupkg")),
            "Package retention keeps current plus one rollback and all newer pending releases");
        StorageCleanup versions = await store.Clean("versions", false);
        Program.Check(versions.FreedBytes == 1600 && File.Exists(Path.Combine(install, "current", "ARS.dll"))
            && File.Exists(Path.Combine(install, "app-0.3.4", "app.dll")) && File.Exists(Path.Combine(install, ".private-retained-recovery", "keep")),
            "Old version cleanup protects current install, latest rollback and unlabelled private recovery archives");
        string outside = Path.Combine(root, "outside"); Put(Path.Combine(outside, "precious"), 500);
        string linked = Path.Combine(Folder("idle"), "temp", "link"); Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        // Directory junctions do not require Windows Developer Mode or elevation.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(linked); start.ArgumentList.Add(outside);
        using (var process = System.Diagnostics.Process.Start(start)!) { process.WaitForExit(); if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd()); }
        StorageReport links = store.Report(); await store.Clean("temp", false);
        Program.Check(links.Warnings.Any(w => w.Contains("link")) && File.Exists(Path.Combine(outside, "precious")), "Accounting and cleanup refuse directory junctions and preserve their external targets");
        Directory.Delete(linked);
        StorageCleanup unused = await store.Clean("workspaces");
        Program.Check(unused.Paths.Any(p => p == cookie) && unused.Paths.All(p => !p.StartsWith(Folder("live")) && !p.StartsWith(Folder("fresh")))
            && File.Exists(cookie), "Unused workspace removal is an explicit preview, includes user data, and excludes running or recently used workspaces");
        await store.Clean("workspaces", false, reviewed: unused.ReviewedFiles);
        Program.Check(!Directory.Exists(Folder("idle")) && Directory.Exists(Folder("live")) && Directory.Exists(Folder("fresh")),
            "Explicit reviewed workspace deletion removes only eligible stopped workspaces");
        store.Policy = new StoragePolicy { CacheCapMb = -1, LogCapMb = -2, UnusedDays = 0 };
        Program.Check(store.Policy.CacheCapMb == 16 && store.Policy.LogCapMb == 1 && store.Policy.UnusedDays == 1,
            "Retention policy persists and bounds malformed settings");
        Program.Check(File.Exists(Path.Combine(data, "bridge-hosts", "old-but-referenced", "host.exe")), "Referenced bridge hosts remain report-only");
        Workspace("cap", 1);
        store.Policy = new StoragePolicy { CacheCapMb = 16, LogCapMb = 1 };
        Put(Path.Combine(Folder("cap"), "chrome-profile", "Default", "Cache", "old"), 10 * 1048576, 5);
        string newCache = Path.Combine(Folder("cap"), "chrome-profile", "Default", "Cache", "new"); Put(newCache, 10 * 1048576, 1);
        Put(Path.Combine(Folder("cap"), "evidence", "old.log"), 700000, 3);
        string newLog = Path.Combine(Folder("cap"), "evidence", "new.log"); Put(newLog, 700000, 1);
        StorageCleanup capped = await store.Clean("retention", false);
        Program.Check(capped.FreedBytes == 10 * 1048576L + 700000 && File.Exists(newCache) && File.Exists(newLog),
            "Cache and log caps evict oldest files while preserving newer files below the configured limit");
        string run = Path.Combine(Folder("fresh"), "recorder-runs", "old", "payload");
        Put(run, 1, 20);
        using (var large = File.OpenWrite(run)) large.SetLength(1L << 30);
        File.SetLastWriteTimeUtc(run, DateTime.UtcNow.AddDays(-20));
        string companion = Path.Combine(Path.GetDirectoryName(run)!, "run.json");
        string keepRun = Path.Combine(Folder("cap"), "recorder-runs", "new", "payload");
        string liveRun = Path.Combine(Folder("live"), "recorder-runs", "active", "payload");
        Put(companion, 15, 20); Put(keepRun, 40); Put(liveRun, 90);
        var runs = await store.Clean("recorder-runs");
        Program.Check(runs.PreviewBytes == (1L << 30) + 55 && !runs.Paths.Contains(liveRun)
            && (await store.Clean("retention")).PreviewBytes == 0, "Recorder runs can be reviewed by size and are retained by default");
        store.Policy = store.Policy with { RecorderLimitGb = 1 };
        await store.Clean("retention", false);
        Program.Check(!Directory.Exists(Path.GetDirectoryName(run)) && File.Exists(keepRun) && File.Exists(liveRun),
            "The aggregate recorder limit removes the oldest stopped run as a group and preserves active runs");
        string profile = Path.Combine(Folder("fresh"), "chrome-profile", "Default", "Network", "Cookies");
        Put(profile, 89);
        var profiles = await store.Clean("browser-profiles");
        Program.Check(profiles.Paths.Contains(profile) && !profiles.Paths.Contains(newCache) && !profiles.Paths.Contains(keepRun),
            "Profile cleanup reviews saved browser data separately from caches and recorder runs");
        await store.Clean("browser-profiles", false, reviewed: profiles.ReviewedFiles);
        Program.Check(!File.Exists(profile) && File.Exists(newCache) && File.Exists(keepRun), "Confirmed profile cleanup preserves other categories");
        Put(profile, 89);
        Workspace("recent-manual", 0);
        string chosen = Path.Combine(Folder("recent-manual"), "document"); Put(chosen, 123);
        var ids = new HashSet<string> { "recent-manual", "empty-recovered", "live" };
        var manual = await store.CleanSelected(["workspaces"], true, workspaceIds: ids);
        Program.Check(manual.Paths.Contains(chosen) && manual.WorkspaceFolders.Contains(Folder("empty-recovered"))
            && !manual.WorkspaceFolders.Contains(Folder("live")) && !manual.Paths.Contains(profile), "Manual selection includes recent and recovered workspaces and excludes running or unselected workspaces");
        Put(chosen, 456);
        await store.CleanSelected(["workspaces"], false, manual.ReviewedFiles, workspaceIds: ids, reviewedFolders: manual.WorkspaceFolders);
        Program.Check(File.Exists(chosen) && File.Exists(Path.Combine(Folder("recent-manual"), "workspace.json"))
            && !Directory.Exists(Folder("empty-recovered")), "Changed workspace keeps its record and files, and a reviewed empty folder is removed");
        Workspace("ui-selection", 0);
        using (var large = File.Create(Path.Combine(Folder("ui-selection"), "owner-file"))) large.SetLength(1L << 30);
        StoredWorkspace actual = WorkspaceStore.Create("Storage runtime guard");
        WorkspaceAccessStore.Write(actual.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        using (var runtime = WorkspaceRuntime.Start(actual))
        {
            bool called = false;
            Program.Check(!WorkspaceRuntime.WithIdleStorage(actual.Id, () => called = true) && !called,
                "Production admission guard refuses storage mutations while a real runtime is running");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Task<bool> cleanup = Task.Run(() => WorkspaceRuntime.WithIdleStorage("other-idle", () => { entered.Set(); release.Wait(3000); return true; }));
            try
            {
                Program.Check(entered.Wait(2000), "Idle cleanup enters its admission guard");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                bool readable = ReferenceEquals(WorkspaceRuntime.Of(actual.Id), runtime);
                Program.Check(readable && clock.ElapsedMilliseconds < 500 && !WorkspaceRuntime.BeginMaintenance(),
                    "Idle disk cleanup keeps running-workspace state responsive and defers restart admission");
                bool refused = false;
                try { WorkspaceRuntime.Start(actual with { Id = "storage-racing-start" }); }
                catch (InvalidOperationException) { refused = true; }
                Program.Check(refused, "A new workspace cannot start during an admitted storage mutation");
            }
            finally { release.Set(); await cleanup; }
        }
        Program.Check(WorkspaceRuntime.WithIdleStorage(actual.Id, () => true), "Production admission guard allows a stopped runtime");
        WorkspaceStore.Delete(actual.Id);
        WorkspaceRouter.Start();
        try
        {
            Program.Check(await Task.Run(() => StorageController.Command(null, true)) == 0,
                "Storage CLI authenticates to the existing router and returns a JSON report without creating a workspace");
            using var ticket = JsonDocument.Parse(File.ReadAllText(WorkspaceAccessStore.RouterTicket));
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", ticket.RootElement.GetProperty("pipe").GetString()!, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(timeout.Token);
            await WorkspacePipeProtocol.Write(client, Convert.ToBase64String(new byte[32]), 256, timeout.Token);
            string? rejected = await WorkspacePipeProtocol.Read(client, 256, timeout.Token);
            Program.Check(rejected is null, "Storage transport rejects an invalid capability before processing commands");
        }
        finally
        {
            string ticket = File.ReadAllText(WorkspaceAccessStore.RouterTicket);
            WorkspaceRouter.Stop();
            File.WriteAllText(WorkspaceAccessStore.RouterTicket, ticket);
            try
            {
                Program.Check(await Task.Run(() => StorageController.Command(null, true)) == 1 && !WorkspaceRuntime.AnyRunning,
                    "A stale router ticket returns a controlled CLI failure without starting a controller or workspace");
            }
            finally { File.Delete(WorkspaceAccessStore.RouterTicket); }
        }
        StorageUsage saved = store.Usage();
        string changed = Path.Combine(data, "after-measurement");
        Put(changed, 2048);
        var reopened = new WorkspaceStorage(data, install, workspaces, _ => throw new InvalidOperationException("Unexpected rescan"), (_, _) => false);
        Program.Check(reopened.Usage().MeasuredAt == saved.MeasuredAt && reopened.Usage().TotalBytes == saved.TotalBytes,
            "A new storage instance reopens the saved summary without enumerating workspaces");
        Program.Check(store.Usage(true).TotalBytes >= saved.TotalBytes + 2048, "Refresh measures changes since the saved report");
        File.WriteAllText(Path.Combine(data, "storage-usage-v1.json"), "{");
        Program.Check(store.Usage().MeasuredAt > saved.MeasuredAt, "An incomplete cache recovers with a fresh measurement");
        StorageController.SourceForTests = store;
        try
        {
            using (var hub = new MainWindow())
            {
                ProbeWindow.OffScreen(hub);
                hub.OpenStorage();
                try
                {
                    Button? HubMeasure() => Desc<Button>(hub).SingleOrDefault(b => AutomationProperties.GetAutomationId(b) == "MeasureStorage");
                    for (int i = 0; i < 100 && (!hub.IsLoaded || HubMeasure() == null); i++) await Task.Delay(50);
                    Program.Check(hub.IsLoaded && hub.DisplayMode == "settings" && HubMeasure() != null,
                        "The first Storage command on a never-shown hub survives saved-layout restoration");
                    hub.WindowState = WindowState.Minimized;
                    hub.OpenStorage();
                    Program.Check(hub.WindowState == WindowState.Normal && hub.DisplayMode == "settings",
                        "Storage command restores a minimized hub without requiring owner input");
                }
                finally { hub.Close(); }
            }
            foreach (var (theme, card) in new[] { (ThemeChoice.Light, false), (ThemeChoice.Dark, false), (ThemeChoice.Light, true), (ThemeChoice.Dark, true) })
            {
                AppearanceManager.Apply(theme);
                var view = new SettingsView(card);
                var window = ProbeWindow.OffScreen(new Window { Content = view, Width = card ? 320 : 1200,
                    Height = card ? 480 : 800, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None });
                window.Show(); view.Show("storage");
                string prefix = "storage-" + theme.ToString().ToLowerInvariant() + (card ? "-card" : "-wide");
                try
                {
                    view.UpdateLayout();
                    await Task.Delay(300); // Capture after the existing page-transition animation.
                    var scroller = (ScrollViewer)view.FindName("Scroller");
                    bool Total() => Desc<TextBlock>(view).Any(t => t.IsVisible && t.Text.EndsWith("used by ARS", StringComparison.Ordinal));
                    Button Id(string id) => Desc<Button>(view).Single(b => AutomationProperties.GetAutomationId(b) == id);
                    bool Hint(string start) => Desc<TextBlock>(view).Any(t => t.IsVisible && t.Text.StartsWith(start, StringComparison.Ordinal));
                    async Task Settle() { for (int i = 0; i < 200 && !Id("StorageRemove1").IsEnabled && Id("StorageRemove1").IsVisible; i++) await Task.Delay(50); view.UpdateLayout(); }
                    var measure = Id("MeasureStorage");
                    Program.Check(measure.IsVisible && !Total() && Desc<Expander>(view).All(e => !e.IsExpanded) && !Desc<ListBox>(view).Any(),
                        "Storage opens to one Measure button: no totals, previews or path lists before the owner asks in " + prefix);
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + "-start.png"));
                    measure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int i = 0; i < 200 && !Total(); i++) await Task.Delay(50);
                    view.UpdateLayout();
                    var removes = Enumerable.Range(0, SettingsView.StorageGroups.Length).Select(i => Id("StorageRemove" + i)).ToArray();
                    Program.Check(Total() && !measure.IsVisible, "One click measures and shows the total in " + prefix);
                    string[] missing = SettingsView.StorageGroups.Select(g => g.Label)
                        .Where(label => !Desc<TextBlock>(view).Any(t => t.IsVisible
                            && (t.Text == label || t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == label)))).ToArray();
                    Program.Check(missing.Length == 0, "The measurement shows all four sections in " + prefix + (missing.Length > 0 ? "; missing " + string.Join(", ", missing) : ""));
                    var last = removes.Last(b => b.IsVisible);
                    Program.Check(last.TranslatePoint(new Point(0, last.ActualHeight), scroller).Y <= scroller.ViewportHeight,
                        "Every section's Remove fits the first viewport in " + prefix);
                    await Task.Delay(100);
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + ".png"));
                    if (card && theme == ThemeChoice.Light)
                    {
                        removes[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        view.UpdateLayout();
                        Program.Check(Equals(removes[0].Content, "Confirm") && File.Exists(profile) && Hint("Signs stopped workspaces out"),
                            "Browser sign-ins ask once inline and delete nothing on the first click");
                        Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, "storage-signins-confirm.png"));
                    }
                    if (!card && theme == ThemeChoice.Dark)
                    {
                        removes[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (int i = 0; i < 200 && !Hint("Nothing to remove"); i++) await Task.Delay(50);
                        Program.Check(Hint("Nothing to remove") && File.Exists(Path.Combine(install, "current", "ARS.dll"))
                            && File.Exists(Path.Combine(install, "packages", "ARSApp-0.3.4-full.nupkg")),
                            "Removing app updates keeps the current version and rollback and says when nothing was removed");
                        await Settle();
                    }
                    if (card && theme == ThemeChoice.Dark)
                    {
                        removes[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (int i = 0; i < 200 && !Hint("Removed "); i++) await Task.Delay(50);
                        Program.Check(!File.Exists(newCache) && !File.Exists(newLog) && File.Exists(liveCache) && File.Exists(liveLog)
                            && File.Exists(changed) && Hint("Removed ") && Desc<TextBlock>(view).Any(t => t.Text.Contains("stop running workspaces")),
                            "One click removes caches, logs and temp files, keeps a running workspace's files, and says why some stayed");
                        await Settle();
                        removes[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        view.UpdateLayout();
                        Program.Check(Equals(removes[2].Content, "Confirm") && File.Exists(keepRun), "Recordings ask once inline before deleting");
                        removes[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (int i = 0; i < 200 && File.Exists(keepRun); i++) await Task.Delay(50);
                        await Settle();
                        Program.Check(!File.Exists(keepRun) && File.Exists(liveRun) && File.Exists(chosen) && Hint("Removed 40 B"),
                            "The second click removes stopped recordings, keeps workspace files and running recordings, and reports the freed bytes");
                        Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, "storage-removed.png"));
                    }
                    var fold = Desc<Expander>(view).Single(e => Equals(e.Header, "Automatic cleanup"));
                    fold.IsExpanded = true;
                    scroller.ScrollToBottom(); await Task.Delay(100);
                    Program.Check(Desc<CheckBox>(fold).Count() == 1 && Desc<ComboBox>(fold).Count() == 1,
                        "Automatic cleanup is one submenu with one switch and one recording limit in " + prefix);
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + "-automatic.png"));
                }
                finally { window.Close(); }
            }
        }
        finally { StorageController.SourceForTests = null; }
        File.WriteAllText(Path.Combine(Program.Output, "storage-fixture-report.json"), JsonSerializer.Serialize(report, WorkspaceStorage.Json));
    }

    static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (T nested in Desc<T>(child)) yield return nested;
        }
    }
}
