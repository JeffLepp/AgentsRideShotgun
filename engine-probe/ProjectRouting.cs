using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

internal static class ProjectRouting
{
    internal static void Run(Action<bool, string> check, string output)
    {
        using (var desktop = AgentDesktop.Create(AgentDesktop.NameFor("empty" + Guid.NewGuid().ToString("N")[..8])))
        {
            DesktopFrame frame = desktop.Capture(640, 480);
            check(frame is { Complete: true, Windows: 0, Image.PixelWidth: 640, Image.PixelHeight: 480 },
                "A new empty Windows desktop returns a real opaque background screenshot before any app opens");
            // Settings > General > Agent screens: a desktop behind the windows, or the plain fill.
            AgentScreenLook lookBefore = AppSettingsStore.Current.AgentScreen;
            var looks = new Dictionary<AgentScreenLook, uint>();
            foreach (AgentScreenLook look in new[] { AgentScreenLook.Full, AgentScreenLook.Simple })
            {
                AppSettingsStore.Update(s => s with { AgentScreen = look });
                desktop.Capture(1440, 900);   // a first sight builds the wall off the capture path
                Thread.Sleep(1500);
                BitmapSource screen = desktop.Capture(1440, 900).Image!;
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(screen));
                using (FileStream file = File.Create(Path.Combine(output, "agent-screen-" + look.ToString().ToLowerInvariant() + ".png"))) png.Save(file);
                var pixel = new uint[1];
                screen.CopyPixels(new System.Windows.Int32Rect(230, 160, 1, 1), pixel, 4, 0);   // inside the first glow
                looks[look] = pixel[0] & 0xFFFFFF;
            }
            AppSettingsStore.Update(s => s with { AgentScreen = lookBefore });
            check(looks[AgentScreenLook.Simple] == 0x141A20 && looks[AgentScreenLook.Full] != looks[AgentScreenLook.Simple],
                "Full desktop paints the agent screen's background; Simple keeps the plain fill");
        }
        CaptureGateChecks(check);
        CapturePumpChecks(check);
        string evidenceHome = Directory.CreateTempSubdirectory("Deskweave-evidence-").FullName;
        string frames = Path.Combine(evidenceHome, "evidence", "frames");
        Directory.CreateDirectory(frames);
        string old = Path.Combine(frames, "000001.png"), recent = Path.Combine(frames, "000002.png");
        File.WriteAllBytes(old, [1]);
        File.WriteAllBytes(recent, [2]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        using (new WorkspaceEvidence(evidenceHome)) { }
        check(!File.Exists(old) && File.Exists(recent),
            "Screenshots older than 7 days are dropped when a workspace next starts, newer ones stay");
        File.WriteAllBytes(Path.Combine(frames, "250000.png"), [3]);
        using (var evidence = new WorkspaceEvidence(evidenceHome))
        {
            var pixel = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, new byte[4], 4);
            evidence.Note("probe", "frame", "kept", pixel);
        }
        string[] names = [.. Directory.GetFiles(frames).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        check(names.Length == 3 && names[^1]!.StartsWith('t'),
            "A new screenshot sorts after the numbered ones older versions wrote, so trimming keeps the newest");
        // Outside the source checkout: its .git must not turn non-project fixtures into projects.
        string root = Directory.CreateTempSubdirectory("Deskweave-routing-").FullName;
        string alpha = Path.Combine(root, "Alpha"), beta = Path.Combine(root, "Beta");
        string child = Path.Combine(alpha, "packages", "web");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "package.json"), "{}");
        Directory.CreateDirectory(beta);
        File.WriteAllText(Path.Combine(beta, ".git"), "gitdir: ../worktrees/beta");
        check(WorkspaceHome.ProjectFolder(child) == alpha,
            "A monorepo package and its parent resolve to one repository root");
        check(WorkspaceHome.ProjectFolder(beta) == beta,
            "A Git worktree's .git file identifies a separate project");
        string nested = Path.Combine(child, "vendor");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));
        check(WorkspaceHome.ProjectFolder(nested) == nested, "A nested repository remains its own project");
        string standalone = Path.Combine(root, "Standalone");
        Directory.CreateDirectory(Path.Combine(standalone, "src"));
        File.WriteAllText(Path.Combine(standalone, "App.csproj"), "<Project />");
        check(WorkspaceHome.ProjectFolder(Path.Combine(standalone, "src")) == standalone,
            "A project manifest identifies an unversioned project from a subfolder");
        check(WorkspaceHome.ProjectFolder(root) == "", "An ordinary folder without project markers belongs in Scratch");
        check(new[] { "", "Z:\\missing-deskweave-project", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetPathRoot(root)! }
            .All(path => WorkspaceHome.ProjectFolder(path) == ""),
            "Home, Desktop, a drive, unknown folders and missing context never become projects");
        StoredWorkspace[] records = [
            new() { Id = "private", Name = "Scratch", Agents = "" },
            new() { Id = "shared", Agents = WorkspaceHome.Anyone },
            new() { Id = "codex", Agents = WorkspaceHome.Agent("codex") },
            new() { Id = "alpha", Agents = WorkspaceHome.Folder(alpha) },
            new() { Id = "scratch", Agents = WorkspaceHome.Scratch }];
        check(WorkspaceHome.Decide(records, child, "claude-code", _ => true).Existing == "alpha"
            && WorkspaceHome.Decide(records.Reverse(), alpha.ToUpperInvariant(), "codex-mcp-client", _ => false).Existing == "alpha",
            "Different agents, folder casing and record order all reuse the same project workspace");
        check(new[] { "claude-code", "claude-code account 2", "codex-mcp-client", "codex account 2", "gemini-cli", "custom MCP client" }
                .All(client => WorkspaceHome.Decide(records, child, client, _ => true).Existing == "alpha"),
            "Project routing is independent of provider/profile labels, including generic MCP clients, while account authentication remains with the provider");
        check(WorkspaceHome.Decide(records, beta, "codex-mcp-client", _ => false) is { Existing: null, Name: "Beta" },
            "A new project creates its own workspace despite legacy shared or per-agent workspaces");
        check(WorkspaceHome.Decide(records, root, "claude-code", _ => true).Existing == "scratch"
            && WorkspaceHome.Decide(records, "", "codex-mcp-client", _ => false).Existing == "scratch",
            "Every agent outside a project shares one Scratch regardless of whether it is busy");
        check(WorkspaceHome.Decide(records.Where(w => w.Id != "scratch"), root, "codex", _ => false).Existing is null,
            "A private workspace named Scratch is never appropriated for outside agents");
        int before = WorkspaceStore.All().Count;
        StoredWorkspace scratch = WorkspaceHome.EnsureScratch();
        check(WorkspaceHome.EnsureScratch().Id == scratch.Id && WorkspaceStore.All().Count == before + 1
            && WorkspaceRuntime.Of(scratch.Id) is null,
            "Repeated Scratch initialization creates exactly one saved record and no running desktop");
    }

    static void CaptureGateChecks(Action<bool, string> check)
    {
        var gate = new WorkspaceCaptureGate();
        using var entered = new ManualResetEventSlim();
        var finish = new TaskCompletionSource<DesktopFrame>();
        var picture = new DesktopFrame(null, 3, 0, false);
        int composites = 0;
        Task<DesktopFrame> first = Task.Run(() => gate.Take(640, 480, TimeSpan.FromSeconds(2), () =>
        {
            Interlocked.Increment(ref composites);
            entered.Set();
            return finish.Task;
        }));
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The controlled capture begins within its bound");
        Task<DesktopFrame> second = Task.Run(() => gate.Take(640, 480, TimeSpan.FromSeconds(2), () =>
        {
            Interlocked.Increment(ref composites);
            return Task.FromResult(picture);
        }));
        try
        {
            check(!second.Wait(100), "A concurrent agent screenshot waits for the preview's in-flight frame");
            check(gate.Take(320, 240, TimeSpan.FromMilliseconds(20), () => Task.FromResult(picture)).TimedOut,
                "Concurrent requests never receive an image in the wrong coordinate space");
            check(gate.Take(640, 480, TimeSpan.FromMilliseconds(20), () => Task.FromResult(picture)).TimedOut,
                "Waiting for a busy capture remains bounded");
        }
        finally { finish.TrySetResult(picture); }
        Task.WaitAll(first, second);
        check(composites == 1 && ReferenceEquals(first.Result, second.Result),
            "The agent and preview receive the same frame from one composite, with no duplicate capture queue");
        var outstanding = new TaskCompletionSource<DesktopFrame>();
        int submissions = 0;
        Task<DesktopFrame> Begin()
        {
            submissions++;
            return outstanding.Task;
        }
        TimeSpan shortWait = TimeSpan.FromMilliseconds(20);
        check(gate.Take(640, 480, shortWait, Begin).TimedOut,
            "The caller that submits a capture may time out while the native operation remains pending");
        bool followersTimedOut = Enumerable.Range(0, 3).All(_ => gate.Take(640, 480, shortWait, Begin).TimedOut);
        check(followersTimedOut && submissions == 1,
            "Later preview ticks share the same capture after the first caller times out, without resubmitting it");
        check(gate.Take(320, 240, shortWait, Begin).TimedOut && submissions == 1,
            "A timed-out capture still protects its coordinate space until the operation finishes");
        outstanding.SetResult(picture);
        var nextPicture = new DesktopFrame(null, 7, 0, false);
        DesktopFrame next = gate.Take(320, 240, shortWait, () =>
        {
            submissions++;
            return Task.FromResult(nextPicture);
        });
        check(submissions == 2 && ReferenceEquals(next, nextPicture),
            "Completion releases the capture flight so the next request can obtain a new frame and size");

        var failed = new TaskCompletionSource<DesktopFrame>();
        check(gate.Take(640, 480, shortWait, () => failed.Task).TimedOut,
            "A caller can leave before a later native capture failure");
        failed.SetException(new IOException("controlled late capture failure"));
        check(ReferenceEquals(gate.Take(640, 480, shortWait, () => Task.FromResult(picture)), picture),
            "A capture fault after all callers timed out releases the flight and allows recovery");
        bool submissionFailed = false;
        try { gate.Take(640, 480, shortWait, () => throw new IOException("controlled enqueue failure")); }
        catch (IOException) { submissionFailed = true; }
        check(submissionFailed && ReferenceEquals(gate.Take(640, 480, shortWait, () => Task.FromResult(picture)), picture),
            "Submission failure reaches its caller and does not strand the capture gate");
    }

    static void CapturePumpChecks(Action<bool, string> check)
    {
        using var desktop = AgentDesktop.Create(AgentDesktop.NameFor("capture" + Guid.NewGuid().ToString("N")[..8]));
        // Control the real serial pump without launching a window or relying on a hung third-party app.
        var work = (BlockingCollection<Action>)typeof(AgentDesktop).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(desktop)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var actionRan = new ManualResetEventSlim();
        int actions = 0;
        work.Add(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); });
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(2)), "The real desktop pump is held by a controlled fixture operation");
            DesktopFrame first = desktop.Capture(640, 480);
            work.Add(() => { Interlocked.Increment(ref actions); actionRan.Set(); });
            DesktopFrame later = desktop.Capture(640, 480);
            check(first.TimedOut && later.TimedOut && work.Count == 2,
                "Repeated real capture timeouts leave one queued capture and the independently queued action");
        }
        finally { release.Set(); }
        check(actionRan.Wait(TimeSpan.FromSeconds(5)) && actions == 1,
            "The non-capture pump action runs exactly once after the pending capture, without being skipped or replayed");
        check(desktop.Capture(640, 480) is { Complete: true, Image.PixelWidth: 640, Image.PixelHeight: 480 },
            "Real screenshot capture recovers when the held desktop pump resumes");

        entered.Reset();
        release.Reset();
        work.Add(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); });
        Task? disposing = null;
        Task<DesktopFrame>? pending = null;
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(2)), "The disposal fixture holds the desktop pump before a capture is queued");
            pending = Task.Run(() => desktop.Capture(640, 480));
            check(SpinWait.SpinUntil(() => work.Count == 1, TimeSpan.FromSeconds(2)),
                "A real capture is submitted before desktop disposal starts");
            disposing = Task.Run(desktop.Dispose);
            check(SpinWait.SpinUntil(() => work.IsAddingCompleted, TimeSpan.FromSeconds(2)),
                "Disposal closes capture submission before draining the held pump");
            release.Set();
            check(Task.WaitAll([pending, disposing], TimeSpan.FromSeconds(8))
                && pending.Result is { Image: null, TimedOut: false },
                "An accepted capture completes as unavailable during teardown instead of stranding or faulting its caller");
            check(desktop.Capture(640, 480) is { Image: null, TimedOut: false },
                "Capture after desktop disposal returns unavailable without submitting to the closed queue");
        }
        finally
        {
            release.Set();
            if (disposing is not null) disposing.Wait(TimeSpan.FromSeconds(8));
            if (pending is not null) pending.Wait(TimeSpan.FromSeconds(3));
        }
    }
}
