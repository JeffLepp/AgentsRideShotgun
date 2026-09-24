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
    /// Most Windows applications allow one copy per logon session, so a second launch hands its
    /// command line to the copy already running and quits. Without a route, the owner has to close
    /// their own copy to let an agent test it; the answer must be a button they can press, not a
    /// negotiation.
    ///
    /// The fixture is the real shape of it: one copy already running somewhere the workspace does not
    /// own, with a window that closing actually closes, and a second launch that exits at once.
    /// </summary>
    static void AlreadyRunningOutside(Action<bool, string> check)
    {
        (WorkspaceRuntime testing, string id) = NewWorkspace("OpenTakeover");
        (WorkspaceRuntime elsewhere, _) = NewWorkspace("OpenOwnersCopy", desktopRequests: false);
        string name = "one-copy-" + Guid.NewGuid().ToString("N")[..6];
        string his = Path.Combine(elsewhere.Computer!.Folder!, name + ".exe");
        string mine = Path.Combine(testing.Computer!.Folder!, name + ".exe");
        try
        {
            // Their copy keeps a window open, the way an application they are using does. The workspace's
            // own launch is a program of the same name from another folder that exits at once with no
            // window, which is what Windows leaves behind when the launch was handed to the copy
            // already up. Two ordinary Windows programs stand in for theirs, because a copied and
            // renamed one does not always find its own resources on every machine.
            File.Copy(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), mine);
            int copy = 0;
            foreach ((string source, string? arguments) in new (string, string?)[]
            {
                ("notepad.exe", null),
                ("rundll32.exe", "shell32.dll,Control_RunDLL main.cpl"),
            })
            {
                copy = Standing(elsewhere, his, Path.Combine(Environment.SystemDirectory, source), arguments);
                if (copy != 0) break;
            }
            if (copy == 0)
            {
                check(true, "The already-running-outside route not measured this run: no fixture application would draw a window on this PC");
                return;
            }

            using var client = new Program.Client(id);
            client.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Open route probe", version = "1" } });
            string said = Program.Client.Text(client.Tool("open", new { program = mine }));
            check(said.Contains("already running outside this workspace", StringComparison.Ordinal)
                && said.Contains("one click", StringComparison.Ordinal),
                "A launch handed to a copy already running outside answers with the owner's one click, not with a dead end for the agent to argue about");

            WorkspaceHandoff pending = testing.Access!.Handoffs.All.Single(r => r.State == "pending" && r.Kind == "takeover");
            // The owner's click comes from a window on the desktop their copy is on. Standing there is
            // what lets a close reach that window at all - EnumWindows only sees its own desktop -
            // and in the product that desktop is their own, where both the app and ARS's window are.
            WorkspaceHandoff decided = OnDesktop(elsewhere.Computer.Name,
                () => testing.Access.Handoffs.Decide(pending.Id, approve: true));
            check(decided.State == "opened",
                "One click closes the copy they had open and starts the program in the workspace instead: " + decided.Detail);
            check(Process.GetProcessesByName(name).Length == 0,
                "Their copy really is gone afterwards, so the next launch in the workspace is no longer handed to it");
        }
        finally
        {
            elsewhere.Dispose();
            testing.Dispose();
        }
    }

    /// <summary>
    /// Puts a copy of <paramref name="source"/> at <paramref name="at"/> and starts it on that
    /// workspace, standing in for the copy the owner already has open. Its process id once it has a
    /// window of its own, or zero when it never drew one. The language resources go with it: a
    /// Windows program renamed away from its own .mui may not come up at all.
    /// </summary>
    static int Standing(WorkspaceRuntime runtime, string at, string source, string? arguments)
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

        int pid = runtime.Computer!.Launch(at, arguments);
        var waited = Stopwatch.StartNew();
        while (pid != 0 && !Showing(runtime, pid) && waited.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
        if (pid != 0 && Showing(runtime, pid)) return pid;
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
    static bool Showing(WorkspaceRuntime runtime, int pid) => runtime.Computer!.Windows().Any(window =>
        { Native.GetWindowThreadProcessId(window.Handle, out int owner); return owner == pid; });

    /// <summary>Runs one piece of work on a thread attached to the named workspace desktop.</summary>
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
