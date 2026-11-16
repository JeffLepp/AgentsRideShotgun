using System.IO;
using System.Text.Json;
using System.Windows;
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
        StorageController.SourceForTests = store;
        try
        {
            using (var hub = new MainWindow())
            {
                ProbeWindow.OffScreen(hub);
                hub.OpenStorage();
                try
                {
                    for (int i = 0; i < 100 && (!hub.IsLoaded || !Desc<TextBlock>(hub).Any(t => t.Text.EndsWith("used by ARS", StringComparison.Ordinal))); i++) await Task.Delay(50);
                    Program.Check(hub.IsLoaded && hub.DisplayMode == "settings"
                        && Desc<TextBlock>(hub).Any(t => t.Text.EndsWith("used by ARS", StringComparison.Ordinal)),
                        "The first Storage command on a never-shown hub survives saved-layout restoration");
                    hub.WindowState = WindowState.Minimized;
                    hub.OpenStorage();
                    Program.Check(hub.WindowState == WindowState.Normal && hub.DisplayMode == "settings",
                        "Storage command restores a minimized hub without requiring owner input");
                }
                finally { hub.Close(); }
            }
            foreach (var (theme, card) in new[] { (ThemeChoice.Light, false), (ThemeChoice.Dark, false), (ThemeChoice.Light, true) })
            {
                AppearanceManager.Apply(theme);
                var view = new SettingsView(card);
                var window = ProbeWindow.OffScreen(new Window { Content = view, Width = card ? 340 : 1200,
                    Height = 800, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None });
                window.Show(); view.Show("storage");
                try
                {
                    view.UpdateLayout();
                    for (int i = 0; i < 100 && !Desc<TextBlock>(view).Any(t => t.Text.EndsWith("used by ARS", StringComparison.Ordinal)); i++) await Task.Delay(50);
                    view.UpdateLayout();
                    Program.Check(Desc<TextBlock>(view).Any(t => t.Text.EndsWith("used by ARS", StringComparison.Ordinal)),
                        "Storage renders measured totals in " + theme + (card ? " card" : " wide"));
                    await Task.Delay(300); // Capture after the existing page-transition animation.
                    string prefix = "storage-" + theme.ToString().ToLowerInvariant() + (card ? "-card" : "-wide");
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + ".png"));
                    var scroller = (ScrollViewer)view.FindName("Scroller");
                    scroller.ScrollToBottom(); await Task.Delay(100);
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + "-retention.png"));
                    Desc<Button>(view).First(b => Equals(b.Content, "Preview")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int i = 0; i < 100 && !Desc<Button>(view).Any(b => Equals(b.Content, "Clean up these files")); i++) await Task.Delay(50);
                    Program.Check(Desc<TextBox>(view).Any(t => t.IsReadOnly && t.Text.Contains(newCache, StringComparison.Ordinal)),
                        "Storage preview lists eligible file paths before confirmation in " + theme + (card ? " card" : " wide"));
                    Desc<TextBox>(view).First(t => t.IsReadOnly && t.Text.Contains(newCache, StringComparison.Ordinal)).BringIntoView();
                    await Task.Delay(100);
                    Mvp.Save(Mvp.Photograph(window), Path.Combine(Program.Output, prefix + "-preview.png"));
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
