using System.IO;
using HiveMind.AgentWorkspaces;

internal static class ProjectRouting
{
    internal static void Run(Action<bool, string> check)
    {
        using (var desktop = AgentDesktop.Create(AgentDesktop.NameFor("empty" + Guid.NewGuid().ToString("N")[..8])))
        {
            DesktopFrame frame = desktop.Capture(640, 480);
            check(frame is { Complete: true, Windows: 0, Image.PixelWidth: 640, Image.PixelHeight: 480 },
                "A new empty Windows desktop returns a real opaque background screenshot before any app opens");
        }
        var gate = new WorkspaceCaptureGate();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var picture = new DesktopFrame(null, 3, 0, false);
        int composites = 0;
        Task<DesktopFrame> first = Task.Run(() => gate.Take(640, 480, TimeSpan.FromSeconds(2), () =>
        {
            Interlocked.Increment(ref composites);
            entered.Set();
            finish.Wait(TimeSpan.FromSeconds(2));
            return picture;
        }));
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The controlled capture begins within its bound");
        Task<DesktopFrame> second = Task.Run(() => gate.Take(640, 480, TimeSpan.FromSeconds(2), () =>
        {
            Interlocked.Increment(ref composites);
            return picture;
        }));
        try
        {
            check(!second.Wait(100), "A concurrent agent screenshot waits for the preview's in-flight frame");
            check(gate.Take(320, 240, TimeSpan.FromMilliseconds(20), () => picture).TimedOut,
                "Concurrent requests never receive an image in the wrong coordinate space");
            check(gate.Take(640, 480, TimeSpan.FromMilliseconds(20), () => picture).TimedOut,
                "Waiting for a busy capture remains bounded");
        }
        finally { finish.Set(); }
        Task.WaitAll(first, second);
        check(composites == 1 && ReferenceEquals(first.Result, second.Result),
            "The agent and preview receive the same frame from one composite, with no duplicate capture queue");
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
}
