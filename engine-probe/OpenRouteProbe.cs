using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Where a thing opens follows who it is for:
/// the agent's own work stays in the workspace, and something the owner asked for goes to their own
/// desktop, where the shell is - so packaged apps, file associations and one-copy-per-session
/// applications all have a route instead of a refusal. Nothing reaches their desktop, and nothing of
/// theirs closes, until they click.
/// </summary>
internal static class OpenRouteProbe
{
    internal static int RunStandalone(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "of-" + Guid.NewGuid().ToString("N")[..8]);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(fixture, "settings.json"));
        using var watchdog = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Open route probe exceeded 180 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        string? failure = null;
        try { Run(Check); }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "open-route.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), engine = typeof(WorkspaceRuntime).Assembly.Location,
            fixture, claims, failure, globalInputEventsSent = 0, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException(claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        try
        {
            Classifies(check);
            PackagedIsARoute(check);
            OwnerRouteWaitsForTheClick(check);
            OwnerApprovesWhatRuns(check);
            AlreadyRunningOutside(check);
        }
        finally { WorkspaceRuntime.Rest(); }
    }

    // --- fixtures ---------------------------------------------------------------------------

    static (WorkspaceRuntime Runtime, string Id) NewWorkspace(string tag, bool desktopRequests = true)
    {
        StoredWorkspace workspace = WorkspaceStore.Create(tag + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, desktopRequests) { PrewarmBrowser = false });
        return (WorkspaceRuntime.Start(workspace), workspace.Id);
    }

    /// <summary>The probe's own exe, which writes where it ended up and then sits still. An
    /// independent Win32 oracle for which desktop a launch actually landed on, and no window.</summary>
    static string Oracle(string file) => "--grandchild \"" + file + "\"";

    static string? DesktopIn(string file, int seconds = 20)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                return json.RootElement.GetProperty("desktop").GetString();
            }
            catch (Exception ex) when (ex is IOException or JsonException or FileNotFoundException) { }
            Thread.Sleep(100);
        }
        return null;
    }

    static void StopOracle(string file)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            using Process process = Process.GetProcessById(json.RootElement.GetProperty("pid").GetInt32());
            process.Kill();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // --- where a program opens -------------------------------------------------------------

    /// <summary>A Store app's execution alias is a zero-byte reparse point that CreateProcess cannot
    /// start at all; an ordinary program must never be mistaken for one.</summary>
    static void Classifies(Action<bool, string> check)
    {
        check(!WorkspacePrograms.ShellOnly(Environment.ProcessPath!),
            "An ordinary executable is not mistaken for a packaged app, so the workspace route is unchanged for it");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var win10 = new Version(10, 0, 19045);
        var win11 = new Version(10, 0, 22631);
        bool Relay(string relative, Version version) => WorkspacePrograms.SystemShellRelay(Path.Combine(windows, relative), windows, version);
        check(Relay(@"System32\calc.exe", win10) && Relay(@"System32\calc.exe", win11)
            && Relay(@"SysWOW64\calc.exe", win10) && Relay(@"Sysnative\calc.exe", win11),
            "Windows 10 and 11 Calculator activation stubs require the owner route before any process starts");
        check(Relay("explorer.exe", win10) && Relay("explorer.exe", win11),
            "Windows Explorer uses the owner route instead of relaying a folder onto the owner's screen");
        check(!Relay(@"System32\notepad.exe", win10) && !Relay(@"System32\mspaint.exe", win10)
            && !Relay("notepad.exe", win10),
            "Classic Windows 10 Notepad and Paint remain available inside a workspace");
        check(Relay(@"System32\notepad.exe", win11) && Relay(@"System32\mspaint.exe", win11)
            && Relay("notepad.exe", win11) && Relay(@"SysWOW64\notepad.exe", win11),
            "Windows 11 Notepad and Paint system launchers are classified before they can activate packaged apps");
        check(!Relay(@"Tools\calc.exe", win11) && !Relay(@"System32-other\calc.exe", win11)
            && !Relay(@"System32\mycalc.exe", win11)
            && !WorkspacePrograms.SystemShellRelay(Path.Combine(Path.GetTempPath(), "notepad.exe"), windows, win11),
            "Unrelated applications with similar filenames or directories are not classified as Windows shell relays");
        check(Relay(@"System32\..\System32\CALC.EXE", win10)
            && WorkspacePrograms.SystemShellRelay(@"\\?\" + Path.Combine(windows, @"System32\calc.exe"), windows, win11),
            "Relative path components, casing and extended paths retain the known system-launcher classification");
        check(WorkspacePrograms.ShellOnly(Path.Combine(windows, "explorer.exe"))
            && WorkspacePrograms.ShellOnly(Path.Combine(Environment.SystemDirectory, "calc.exe")),
            "Real system Explorer and Calculator paths are classified without opening either application");
        check(WorkspacePrograms.ShellOnly(Path.Combine(Environment.SystemDirectory, "notepad.exe"))
            == (Environment.OSVersion.Version >= new Version(10, 0, 22000)),
            "The real system Notepad follows this PC's Windows generation");
        check(WorkspacePrograms.ShellOnly(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            "An explicit shell:AppsFolder activation is recognised as something only the owner's shell can start");
        if (Alias() is not { } alias)
        {
            check(true, "Packaged-app detection not measured on a real alias: this PC has no app execution aliases installed");
            return;
        }
        check(WorkspacePrograms.ShellOnly(alias), "A real app execution alias on this PC (" + Path.GetFileName(alias)
            + ") is recognised as something only the owner's shell can start");
    }

    /// <summary>An installed Store app's execution alias: zero bytes, and a reparse point Windows
    /// resolves through the shell. Null when this PC has none.</summary>
    static string? Alias()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe")
                .FirstOrDefault(file => new FileInfo(file) is { Length: 0, Exists: true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>`open calc` used to answer "started, pid N. It has already exited", which reads like
    /// a crash and sent an agent hunting for a bug in the application. It must read as a route.</summary>
    static void PackagedIsARoute(Action<bool, string> check)
    {
        if (Alias() is not { } alias)
        {
            check(true, "A packaged-app request not measured through the tools: this PC has no app execution aliases installed");
            return;
        }
        (WorkspaceRuntime runtime, string id) = NewWorkspace("OpenPackaged");
        try
        {
            using var client = new Program.Client(id);
            client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Open route probe", version = "1" } });
            int before = runtime.Computer!.Windows().Count;
            JsonElement reply = client.Tool("open", new { program = alias });
            string said = Program.Client.Text(reply);
            check(Program.Client.Failed(reply)
                && said.Contains("where=owner", StringComparison.Ordinal)
                && !said.Contains("exited", StringComparison.OrdinalIgnoreCase),
                "An unsupported packaged launch reports failure with the supported owner-desktop route");
            check(runtime.Computer.Windows().Count == before,
                "Asking for a packaged app starts nothing at all - no stub process to mistake for a crash, and nothing new on any screen");
        }
        finally { runtime.Dispose(); }
    }

    /// <summary>
    /// The owner's route end to end: the agent asks, nothing opens, the owner clicks, and only then
    /// does Windows start it - on their own desktop, which is the whole point, and which an independent
    /// oracle inside the launched process confirms rather than the tool's own say-so.
    /// </summary>
    static void OwnerRouteWaitsForTheClick(Action<bool, string> check)
    {
        (WorkspaceRuntime runtime, string id) = NewWorkspace("OpenOwner");
        string folder = runtime.Computer!.Folder!;
        string his = Path.Combine(folder, "owner-route.json");
        try
        {
            using var client = new Program.Client(id);
            client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Open route probe", version = "1" } });

            JsonElement asked = client.Tool("open", new
            {
                program = Environment.ProcessPath!, arguments = Oracle(his),
                where = "owner", reason = "the owner asked for this one",
            });
            check(!Program.Client.Failed(asked)
                && Program.Client.Text(asked).Contains("one click", StringComparison.Ordinal),
                "open where=owner comes back as a route the owner can take, not as a refusal");
            check(!File.Exists(his), "Asking for the owner's desktop starts nothing there: an agent cannot put a window on their screen by itself");

            WorkspaceHandoff pending = runtime.Access!.Handoffs.All.Single(r => r.State == "pending" && r.Kind == "program");
            WorkspaceHandoff decided = runtime.Access.Handoffs.Decide(pending.Id, approve: true);
            check(decided.State == "opened", "The owner's one click is enough to open it; the request says what happened: " + decided.Detail);
            string? where = DesktopIn(his);
            check(where is not null && where != runtime.Computer.Name,
                "Once they have clicked, the program really is running on their own desktop (" + (where ?? "not started")
                + "), not on the workspace's");

            // The default route, unchanged, and proved where it counts: a window is on exactly one
            // desktop, and this one is enumerable on the workspace's, which is not the owner's.
            JsonElement inside = client.Tool("open", new { program = Path.Combine(Environment.SystemDirectory, "notepad.exe") });
            check(!Program.Client.Failed(inside) && Pid(Program.Client.Text(inside)) is { } started && Showing(runtime, started),
                "A workspace-bound launch still opens on the workspace screen and never on the owner's");
        }
        finally
        {
            StopOracle(his);
            runtime.Dispose();
        }
    }

    /// <summary>
    /// The owner approves a command line, not a program name. Asking again with other arguments
    /// used to find the waiting request and overwrite what it would run, so an agent could ask with
    /// something harmless and swap in something else before their click. Each command line is now its
    /// own request, fixed when it is made, and approving one runs exactly that one.
    /// </summary>
    static void OwnerApprovesWhatRuns(Action<bool, string> check)
    {
        (WorkspaceRuntime runtime, _) = NewWorkspace("OpenPinned");
        string folder = runtime.Computer!.Folder!;
        string shown = Path.Combine(folder, "pinned-shown.json");
        string swapped = Path.Combine(folder, "pinned-swapped.json");
        try
        {
            WorkspaceExternalAccess access = runtime.Access!;
            Guid agent = access.AcquireForTests();
            WorkspaceHandoff first = access.RequestProgram(agent, Environment.ProcessPath!, Oracle(shown), "the owner asked", false);
            WorkspaceHandoff second = access.RequestProgram(agent, Environment.ProcessPath!, Oracle(swapped), "the owner asked", false);
            WorkspaceHandoff again = access.RequestProgram(agent, Environment.ProcessPath!, Oracle(shown), "the owner asked", false);
            check(second.Id != first.Id && again.Id == first.Id
                && access.Handoffs.All.Single(r => r.Id == first.Id).Arguments == Oracle(shown),
                "The same program with other arguments is a separate request, and the one the owner is looking at keeps its own command line");
            WorkspaceHandoff decided = access.Handoffs.Decide(first.Id, approve: true);
            access.Handoffs.Decide(second.Id, approve: false);
            check(decided.State == "opened" && DesktopIn(shown) is not null && DesktopIn(swapped, 3) is null,
                "Approving a program request runs exactly the command line that request was made with, never one asked for later");

            // Neither side holds its own lock while it waits for the other's. The listener here
            // stands in for the corner window, which reads the workspace from another thread
            // when a request changes; before, Decide raised Changed under the handoffs' lock and
            // a request held the workspace's lock across the whole ask.
            bool requestFree = false, decideFree = false;
            string? phase = "request";
            void Listen()
            {
                bool free = Task.Run(() => { _ = access.HasDriver; _ = access.Handoffs.All; }).Wait(TimeSpan.FromSeconds(3));
                if (phase == "request") requestFree = free; else if (phase == "decide") decideFree = free;
            }
            access.Handoffs.Changed += Listen;
            try
            {
                WorkspaceHandoff asked = access.RequestProgram(agent, Environment.ProcessPath!, "--lock-order", "the owner asked", false);
                phase = "decide";
                access.Handoffs.Decide(asked.Id, approve: false);
                phase = null;
            }
            finally { access.Handoffs.Changed -= Listen; }
            check(requestFree && decideFree,
                "A desktop request and the owner's answer both announce the change with no lock held, so the corner window reading the workspace cannot deadlock against an agent asking");
        }
        finally
        {
            StopOracle(shown);
            StopOracle(swapped);
            runtime.Dispose();
        }
    }

    // --- one copy per session --------------------------------------------------------------

    /// <summary>
    /// A launch that quits at once without a window went somewhere. It is reported as handed to a copy
    /// outside the workspace only when that copy is the very same file, outside every ARS workspace,
    /// and nothing in the workspace answered it. A same-named program from another file, a copy whose
    /// file cannot be read, a copy already in the workspace and a copy in another agent's workspace
    /// never put anything up for closing. A plain desktop of the probe's own, which no workspace
    /// owns, stands in for the owner's: its processes belong to no running workspace.
    /// </summary>
    static void AlreadyRunningOutside(Action<bool, string> check)
    {
        (WorkspaceRuntime testing, string id) = NewWorkspace("OpenTakeover");
        (WorkspaceRuntime elsewhere, _) = NewWorkspace("OpenOtherAgent", desktopRequests: false);
        AgentDesktop owner = AgentDesktop.Create("ARS-probeowner" + Guid.NewGuid().ToString("N")[..6]);
        var made = new List<string>();
        try
        {
            using var client = new Program.Client(id);
            client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Open route probe", version = "1" } });
            NamesakeIsNotTheCopy(check, testing, owner, client);
            nint unrelated = CopyInHereTookIt(check, testing, owner, client, made);
            AnotherWorkspaceIsNotTheOwner(check, testing, elsewhere, client, made);
            SameFileOutside(check, testing, owner, client, made, unrelated);
            TrayOnlyCopy(check, testing, owner, client, made);
        }
        finally
        {
            WorkspaceControl.FileForTests = null;
            owner.Dispose();
            elsewhere.Dispose();
            testing.Dispose();
            foreach (string file in made) Remove(file);
        }
    }

    static int PendingTakeovers(WorkspaceRuntime runtime) =>
        runtime.Access!.Handoffs.All.Count(r => r.State == "pending" && r.Kind == "takeover");

    static string Open(Program.Client client, string program, string? arguments = null) =>
        Program.Client.Text(arguments is null ? client.Tool("open", new { program })
            : client.Tool("open", new { program, arguments }));

    /// <summary>
    /// The field case's other half: the owner has their own install of a program, from another
    /// folder, under the same file name. A launch in the workspace that quits without a window was
    /// reported as handed to it and their copy was offered for closing, though it was never the same
    /// file. A name is not a program: nothing may be filed or closed on the strength of one.
    /// </summary>
    static void NamesakeIsNotTheCopy(Action<bool, string> check, WorkspaceRuntime testing, AgentDesktop owner,
        Program.Client client)
    {
        string name = "one-copy-" + Guid.NewGuid().ToString("N")[..6];
        string his = Path.Combine(testing.Computer!.Folder!, "owner-" + name, name + ".exe");
        string mine = Path.Combine(testing.Computer.Folder!, name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(his)!);
        // The workspace's own launch is rundll32 under that name, which exits at once with no window.
        File.Copy(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), mine);
        int copy = Namesake(owner, his);
        if (copy == 0)
        {
            check(true, "A same-named copy from another file not measured this run: no fixture application would draw a window on this PC");
            return;
        }
        string said = Open(client, mine);
        check(!said.Contains("handed the launch", StringComparison.Ordinal)
            && !said.Contains("one click", StringComparison.Ordinal) && PendingTakeovers(testing) == 0 && Alive(copy),
            "A launch that quits while only a same-named program from another file runs outside is not reported as handed to it, files no request, and leaves that program running");
        check(said.Contains("not this file", StringComparison.Ordinal),
            "The agent is told the same-named program outside is a different file, as a fact and not as a route: " + said.Split('\n')[0]);

        // The same program, when Windows will not say which file it runs - as for an elevated copy,
        // whose memory cannot be read: never called another file, never a target.
        WorkspaceControl.FileForTests = pid => pid == copy ? "" : null;
        try
        {
            string unread = Open(client, mine);
            check(!unread.Contains("not this file", StringComparison.Ordinal)
                && unread.Contains("would not say which file", StringComparison.Ordinal)
                && !unread.Contains("one click", StringComparison.Ordinal) && PendingTakeovers(testing) == 0 && Alive(copy),
                "A same-named program whose file cannot be read is neither called a different file nor offered for closing");
        }
        finally { WorkspaceControl.FileForTests = null; }
    }

    /// <summary>
    /// The field case: a one-copy application already running in the workspace, a second launch
    /// that signals it - it restores its own window in here - and quits, and the owner's own copy of
    /// the same name running on their desktop. The launch went nowhere outside, so nothing is asked of
    /// the owner and nothing of theirs is put up for closing. Returns that window, for later use as
    /// an unrelated window in the workspace, or zero.
    /// </summary>
    static nint CopyInHereTookIt(Action<bool, string> check, WorkspaceRuntime testing, AgentDesktop owner,
        Program.Client client, List<string> made)
    {
        string key = Guid.NewGuid().ToString("N")[..8];
        string name = "one-copy-" + key;
        if (OwnCopy(name) is not { } app)
        {
            check(true, "A one-copy app inside the workspace not measured this run: the probe's folder is not writable");
            return 0;
        }
        made.Add(app);
        string receipt = Path.Combine(testing.Computer!.Folder!, name + ".txt");
        string fixture = $"--one-copy-fixture {key} \"{receipt}\"";
        int inHere = Pid(Open(client, app, fixture)) ?? 0;
        check(inHere != 0 && Until(() => Showing(testing.Computer, inHere), 20),
            "The one-copy fixture's first copy opens its window in the workspace");
        nint window = WindowOf(testing.Computer, inHere);
        check(testing.Computer.Arrange(window, WindowArrangement.Minimize) && Until(() => !Showing(testing.Computer, inHere), 5),
            "The workspace's copy is minimized, so the second launch has to restore it, as the field app did");
        string elsewhere = Path.Combine(testing.Computer.Folder!, "owner-" + name, name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        int theirs = Namesake(owner, elsewhere);

        string said = Open(client, app, fixture);
        check(Until(() => Raised(receipt) > 0, 5) && Showing(testing.Computer, inHere),
            "The second launch really was taken by the copy in the workspace, which restored its window there");
        check(said.Contains("already running in this workspace", StringComparison.Ordinal)
            && !said.Contains("handed the launch", StringComparison.Ordinal)
            && !said.Contains("one click", StringComparison.Ordinal) && PendingTakeovers(testing) == 0,
            "A launch taken by the copy already in the workspace is reported as handled in there and files no request"
            + (theirs == 0 ? " (no same-named copy outside could be started this run)" : ", though a same-named program runs outside"));
        check(Alive(inHere) && (theirs == 0 || Alive(theirs)),
            "Nothing was closed: the workspace's copy and the same-named program outside are both still running");
        return window;
    }

    /// <summary>
    /// Another agent's workspace is not the owner's desktop. A copy of the same file running in
    /// another ARS workspace is that agent's program; a launch it takes is never put to the owner as
    /// "close your copy", which would close another agent's work.
    /// </summary>
    static void AnotherWorkspaceIsNotTheOwner(Action<bool, string> check, WorkspaceRuntime testing,
        WorkspaceRuntime elsewhere, Program.Client client, List<string> made)
    {
        string key = Guid.NewGuid().ToString("N")[..8];
        string name = "one-copy-" + key;
        if (OwnCopy(name) is not { } app)
        {
            check(true, "A copy in another workspace not measured this run: the probe's folder is not writable");
            return;
        }
        made.Add(app);
        string receipt = Path.Combine(elsewhere.Computer!.Folder!, name + ".txt");
        string fixture = $"--one-copy-fixture {key} \"{receipt}\"";
        int theirs = elsewhere.Computer.Launch(app, fixture);
        check(theirs != 0 && Until(() => Showing(elsewhere.Computer, theirs), 20),
            "Another workspace has a copy of the same one-copy fixture file up on its own desktop");
        string said = Open(client, app, fixture);
        check(Until(() => Raised(receipt) > 0, 5), "The launch really was taken by the copy in the other workspace");
        check(!said.Contains("handed the launch", StringComparison.Ordinal)
            && !said.Contains("one click", StringComparison.Ordinal) && PendingTakeovers(testing) == 0 && Alive(theirs),
            "A same-file copy in another ARS workspace is never reported as the owner's copy, files no request and keeps running");
    }

    /// <summary>
    /// Most Windows applications allow one copy per logon session, so a second launch hands its
    /// command line to the copy already running and quits. Without a route, the owner has to close
    /// their own copy to let an agent test it; the answer must be a button they can press, not a
    /// negotiation. The copy outside is the very same file here, the only case that is reported as a
    /// handoff, and approving closes that one process: a same-named program from another file stays.
    /// </summary>
    static void SameFileOutside(Action<bool, string> check, WorkspaceRuntime testing, AgentDesktop owner,
        Program.Client client, List<string> made, nint unrelated)
    {
        string key = Guid.NewGuid().ToString("N")[..8];
        string name = "one-copy-" + key;
        if (OwnCopy(name) is not { } app)
        {
            check(true, "The already-running-outside route not measured this run: the probe's folder is not writable");
            return;
        }
        made.Add(app);
        string receipt = Path.Combine(testing.Computer!.Folder!, "owner-" + name + ".txt");
        string fixture = $"--one-copy-fixture {key} \"{receipt}\"";
        int theirs = owner.Launch(app, fixture);
        check(theirs != 0 && Until(() => Showing(owner, theirs), 20),
            "The owner's copy of the one-copy fixture is up with its window on a desktop no workspace owns");
        string decoyAt = Path.Combine(testing.Computer.Folder!, "owner-" + name, name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(decoyAt)!);
        int namesake = Namesake(owner, decoyAt);

        // Something unrelated in the workspace comes forward while the launch is being handed out.
        // That is all the agent may be told: no copy of this program runs in here.
        if (unrelated != 0 && testing.Computer.Arrange(unrelated, WindowArrangement.Minimize))
        {
            int raisedBefore = Raised(receipt);
            using var restoring = Task.Run(() =>
            {
                if (Until(() => Raised(receipt) > raisedBefore, 10))
                    testing.Computer.Arrange(unrelated, WindowArrangement.Restore);
            });
            string moved = Open(client, app, fixture);
            restoring.Wait(TimeSpan.FromSeconds(15));
            check(!moved.Contains("already running in this workspace", StringComparison.Ordinal)
                && moved.Contains("no copy of this program was found running here", StringComparison.Ordinal)
                && !moved.Contains("one click", StringComparison.Ordinal) && PendingTakeovers(testing) == 0 && Alive(theirs),
                "When an unrelated window in the workspace comes forward during the handoff, the agent is told what was seen - no copy of the program runs here - and nothing is asked of the owner: "
                + moved.Split('\n')[0]);
        }
        else check(true, "An unrelated window coming forward during a handoff not measured this run: no workspace window to move");

        int raisedAtOpen = Raised(receipt);
        string said = Open(client, app, fixture);
        check(said.Contains("already running outside this workspace", StringComparison.Ordinal)
            && said.Contains("one click", StringComparison.Ordinal) && said.Contains("pid " + theirs, StringComparison.Ordinal),
            "A launch handed to the same program file already running outside every workspace answers with the owner's one click, naming that copy");
        check(Raised(receipt) > raisedAtOpen, "The launch really did go to the copy outside, which raised its own window there");

        WorkspaceHandoff pending = testing.Access!.Handoffs.All.Single(r => r.State == "pending" && r.Kind == "takeover");
        long started;
        using (Process process = Process.GetProcessById(theirs)) started = process.StartTime.ToFileTimeUtc();
        check(pending.CopyId == theirs && string.Equals(pending.CopyFile, app, StringComparison.OrdinalIgnoreCase)
            && pending.CopyCreated == started,
            "The request carries exactly the process id, file and start time of the copy it would close");

        // An id that has since gone to another process, and a copy whose file Windows will not name,
        // are refused with what was found - never closed, never reported as closed.
        string? reused = testing.Plane!.TakeOver(app, fixture, theirs, app, started + 1);
        check(reused is not null && reused.Contains("no longer running", StringComparison.Ordinal) && Alive(theirs),
            "A takeover whose start time no longer matches that process id closes nothing and says so: " + reused);
        WorkspaceControl.FileForTests = pid => pid == theirs ? "" : null;
        string? unread;
        try { unread = OnDesktop(owner.Name, () => testing.Plane.TakeOver(app, fixture, theirs, app, started)); }
        finally { WorkspaceControl.FileForTests = null; }
        check(unread is not null && unread.Contains("would not say which program", StringComparison.Ordinal) && Alive(theirs),
            "A takeover of a copy whose file cannot be read closes nothing and does not report success: " + unread);

        // The owner's click comes from a window on the desktop their copy is on. Standing there is
        // what lets a close reach that window at all - EnumWindows only sees its own desktop -
        // and in the product that desktop is their own, where both the app and ARS's window are.
        WorkspaceHandoff decided = OnDesktop(owner.Name, () => testing.Access.Handoffs.Decide(pending.Id, approve: true));
        check(decided.State == "opened" && Until(() => !Alive(theirs), 5),
            "One click closes the copy they had open and starts the program in the workspace instead: " + decided.Detail);
        check(namesake == 0 ? true : Alive(namesake), namesake == 0
            ? "Closing only the named process not measured against a same-named program this run: none would start"
            : "Approving closes only the process the request named: a same-named program from another file is still running");
        check(Until(() => testing.Computer.Windows().Any(w =>
            {
                Native.GetWindowThreadProcessId(w.Handle, out int pid);
                try { using Process process = Process.GetProcessById(pid); return process.ProcessName == name; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
            }), 20),
            "The program then runs in the workspace, so the next launch in here is no longer handed outside");
    }

    /// <summary>
    /// A tray application - Slack, Teams, Discord - keeps only hidden windows while it runs. Its copy
    /// still takes the launch, so it still has to be found, by its file, as the owner's.
    /// </summary>
    static void TrayOnlyCopy(Action<bool, string> check, WorkspaceRuntime testing, AgentDesktop owner,
        Program.Client client, List<string> made)
    {
        string key = Guid.NewGuid().ToString("N")[..8];
        string name = "one-copy-" + key;
        if (OwnCopy(name) is not { } app)
        {
            check(true, "A tray-only copy outside not measured this run: the probe's folder is not writable");
            return;
        }
        made.Add(app);
        string receipt = Path.Combine(testing.Computer!.Folder!, "tray-" + name + ".txt");
        string fixture = $"--one-copy-fixture {key} \"{receipt}\" tray";
        int theirs = owner.Launch(app, fixture);
        check(theirs != 0 && Until(() => Read(receipt).Contains("up ", StringComparison.Ordinal), 20) && !Showing(owner, theirs),
            "The owner's tray-only copy is running with no visible window");
        string said = Open(client, app, fixture);
        check(Until(() => Raised(receipt) > 0, 5)
            && said.Contains("already running outside this workspace", StringComparison.Ordinal)
            && said.Contains("pid " + theirs, StringComparison.Ordinal),
            "A launch handed to a tray-only copy of the same file outside is still reported as handed to it, naming that copy");
        WorkspaceHandoff pending = testing.Access!.Handoffs.All.Single(r => r.State == "pending" && r.Kind == "takeover");
        check(pending.CopyId == theirs, "The request names that tray-only process");
        testing.Access.Handoffs.Decide(pending.Id, approve: false);
    }

    /// <summary>
    /// A one-copy-per-session application in miniature, run from the probe's own exe copied under a
    /// name of its own. The first copy holds a session-wide name and shows a window - or, as "tray",
    /// keeps only a hidden one; a later launch signals it and quits at once with no window, and the
    /// first then restores and raises its window wherever it is - which is what the field app (a
    /// Tauri one) does.
    /// </summary>
    internal static int OneCopy(string key, string receipt, bool tray = false)
    {
        string name = @"Local\ARS.Probe.OneCopy." + key;
        using var only = new Mutex(true, name, out bool first);
        if (!first)
        {
            try { using var wake = EventWaitHandle.OpenExisting(name + ".raise"); wake.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        var raise = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".raise");
        var app = new System.Windows.Application
        {
            ShutdownMode = tray ? System.Windows.ShutdownMode.OnExplicitShutdown : System.Windows.ShutdownMode.OnMainWindowClose,
        };
        var window = new System.Windows.Window
        {
            Title = "One-copy fixture " + key, Width = 420, Height = 300, Left = 200, Top = 160,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, ShowActivated = false,
        };
        window.Loaded += (_, _) => Note(receipt, "up " + Environment.ProcessId);
        new Thread(() =>
        {
            try
            {
                while (raise.WaitOne())
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        if (!tray)
                        {
                            nint handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                            Native.ShowWindow(handle, Native.SwRestore);
                            Native.SetWindowPos(handle, 0, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
                        }
                        Note(receipt, "raised");
                    });
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true }.Start();
        if (!tray) return app.Run(window);
        // A tray application's only windows are hidden ones.
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        app.Startup += (_, _) => Note(receipt, "up " + Environment.ProcessId);
        return app.Run();
    }

    static int Raised(string receipt) =>
        Read(receipt).Split('\n').Count(line => line.Trim() == "raised");

    static void Note(string file, string line)
    {
        try { File.AppendAllText(file, line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Read(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The probe's own exe under a new name beside it, so its process name and file are its
    /// own: the apphost finds the probe's assembly next to itself whatever it is called.</summary>
    static string? OwnCopy(string name)
    {
        string at = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, name + ".exe");
        try { File.Copy(Environment.ProcessPath!, at, true); return at; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static void Remove(string file)
    {
        for (int tries = 0; tries < 20 && File.Exists(file); tries++)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    /// <summary>A program of that file name, from another file, up with a window on that desktop.
    /// Two ordinary Windows programs stand in, because a copied and renamed one does not always find
    /// its own resources on every machine. Zero when neither drew a window.</summary>
    static int Namesake(AgentDesktop desktop, string at)
    {
        foreach ((string source, string? arguments) in new (string, string?)[]
        {
            ("notepad.exe", null),
            ("rundll32.exe", "shell32.dll,Control_RunDLL main.cpl"),
        })
        {
            int copy = Standing(desktop, at, Path.Combine(Environment.SystemDirectory, source), arguments);
            if (copy != 0) return copy;
        }
        return 0;
    }

    static bool Alive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    static bool Until(Func<bool> done, int seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (done()) return true;
            Thread.Sleep(100);
        }
        return done();
    }

    /// <summary>
    /// Puts a copy of <paramref name="source"/> at <paramref name="at"/> and starts it on that
    /// desktop, standing in for a program the owner already has open. Its process id once it has a
    /// window of its own, or zero when it never drew one. The language resources go with it: a
    /// Windows program renamed away from its own .mui may not come up at all.
    /// </summary>
    static int Standing(AgentDesktop desktop, string at, string source, string? arguments)
    {
        try
        {
            if (File.Exists(at)) File.Delete(at);
            File.Copy(source, at);
            foreach (string folder in Directory.EnumerateDirectories(Path.GetDirectoryName(source)!))
            {
                string mui = Path.Combine(folder, Path.GetFileName(source) + ".mui");
                if (!File.Exists(mui)) continue;
                string into = Path.Combine(Path.GetDirectoryName(at)!, Path.GetFileName(folder));
                Directory.CreateDirectory(into);
                File.Copy(mui, Path.Combine(into, Path.GetFileName(at) + ".mui"), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        int pid = desktop.Launch(at, arguments);
        var waited = Stopwatch.StartNew();
        while (pid != 0 && !Showing(desktop, pid) && waited.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
        if (pid != 0 && Showing(desktop, pid)) return pid;
        try { if (pid != 0) using (Process started = Process.GetProcessById(pid)) started.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { }
        Thread.Sleep(200);
        return 0;
    }

    /// <summary>The process id out of `open`'s own answer, which is all the agent gets too.</summary>
    static int? Pid(string said) =>
        said.Split("pid ") is [_, string rest, ..] && int.TryParse(new string([.. rest.TakeWhile(char.IsDigit)]), out int pid)
            ? pid : null;

    /// <summary>That process has a window of its own up on that workspace's screen.</summary>
    static bool Showing(WorkspaceRuntime runtime, int pid) => Showing(runtime.Computer!, pid);

    /// <summary>That process has a window of its own up on that desktop.</summary>
    static bool Showing(AgentDesktop desktop, int pid) => WindowOf(desktop, pid) != 0;

    static nint WindowOf(AgentDesktop desktop, int pid) => desktop.Windows().FirstOrDefault(window =>
        { Native.GetWindowThreadProcessId(window.Handle, out int owner); return owner == pid; })?.Handle ?? 0;

    /// <summary>Runs one piece of work on a thread attached to the named desktop.</summary>
    static T OnDesktop<T>(string desktop, Func<T> work)
    {
        T answer = default!;
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            nint handle = Native.OpenDesktopW(desktop, 0, false, Native.GenericAll);
            try
            {
                if (handle == 0 || !Native.SetThreadDesktop(handle))
                    throw new InvalidOperationException("Could not stand on " + desktop + ".");
                answer = work();
            }
            catch (Exception ex) { failed = ex; }
            finally { if (handle != 0) Native.CloseDesktop(handle); }
        });
        thread.Start();
        thread.Join();
        return failed is null ? answer : throw failed;
    }
}
