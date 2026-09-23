using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Tool calls that hung or said the wrong thing (review 2026-09-22): a wait on a window title with
/// no seconds never ended, missed a window that was already open or renamed itself, and woke on a
/// control's text; a Start Menu scan cut off by its wait kept growing under the caller and was
/// cached half read; a workspace torn down under a call was reported as bad parameters; the
/// client's notifications/cancelled was dropped; and a cancelled command left behind whatever its
/// exited children had started.
///
/// Every window is made on a workspace desktop of its own, never the owner's, and nothing takes
/// focus or sends global input. No model is called.
///
/// Run: Deskweave.Probe.exe --tool-hangs "C:\absolute\output"
/// </summary>
internal static class ToolHangs
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "th-" + Guid.NewGuid().ToString("N")[..8]);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(fixture, "settings.json"));
        using var watchdog = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Tool-hangs probe exceeded 180 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        string? failure = null;
        try
        {
            ParameterFaults(Check);
            StartMenuSnapshot(Check, Path.Combine(fixture, "menu"));
            QueuedCancel(Check);
            Workspace(Check, fixture);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { WorkspaceRuntime.Rest(); }
            catch (Exception ex) { failure ??= "Cleanup: " + ex; }
        }
        string engine = typeof(WorkspaceRuntime).Assembly.Location;
        File.WriteAllText(Path.Combine(output, "tool-hangs.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), engine,
            engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine))),
            fixture, claims, failure, globalInputEventsSent = 0, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException("Failed: " + claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    static void ParameterFaults(Action<bool, string> check)
    {
        Exception wrongKind;
        try { JsonDocument.Parse("\"text\"").RootElement.GetDouble(); throw new InvalidProgramException(); }
        catch (InvalidOperationException ex) { wrongKind = ex; }
        check(WorkspaceMcp.ArgumentFault(wrongKind) && WorkspaceMcp.ArgumentFault(new ArgumentException("x"))
            && !WorkspaceMcp.ArgumentFault(new ObjectDisposedException("WorkspaceControl"))
            && !WorkspaceMcp.ArgumentFault(new InvalidOperationException("the workspace changed state")),
            "Only malformed arguments read as invalid parameters; a workspace torn down under a call does not");
    }

    static void StartMenuSnapshot(Action<bool, string> check, string menu)
    {
        Directory.CreateDirectory(menu);
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        const int count = 150;
        for (int i = 0; i < count; i++)
            WorkspacePrograms.Write(Path.Combine(menu, $"Tool hangs {i:000}.lnk"), notepad, "");
        TimeSpan patience = WorkspacePrograms.ScanPatience;
        try
        {
            WorkspacePrograms.ScanPatience = TimeSpan.Zero;
            IEnumerable<WorkspacePrograms.Shortcut> cut = WorkspacePrograms.Shortcuts([menu]);
            int first = cut.Count();
            Thread.Sleep(1500);   // the reader goes on past its wait
            check(first < count && cut.Count() == first,
                $"A Start Menu scan cut off by its wait returns a fixed copy ({first} of {count}), not the list still being filled");
            WorkspacePrograms.ScanPatience = patience;
            check(WorkspacePrograms.Shortcuts([menu]).Count() == count,
                "The cut-off scan was not cached: the next lookup reads every shortcut");
        }
        finally
        {
            WorkspacePrograms.ScanPatience = patience;
            WorkspacePrograms.Forget();
        }
    }

    /// <summary>A cancellation for a request still waiting behind another drops it unstarted.</summary>
    static void QueuedCancel(Action<bool, string> check)
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var server = new WorkspacePipeServer(async (request, cancel) =>
        {
            seen.Add(request);
            if (request.Contains("\"slow\"", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, cancel);
            return request.Contains("\"id\"", StringComparison.Ordinal) ? "answered" : null;
        });
        using var pipe = Connect(server);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Send(pipe, """{"jsonrpc":"2.0","id":9,"method":"slow"}""", bound.Token);
        Send(pipe, """{"jsonrpc":"2.0","id":10,"method":"later"}""", bound.Token);
        Send(pipe, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":10}}""", bound.Token);
        Send(pipe, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}""", bound.Token);
        Send(pipe, """{"jsonrpc":"2.0","id":11,"method":"ping"}""", bound.Token);
        string[] replies = [.. Enumerable.Range(0, 5).Select(_ => Receive(pipe, bound.Token))];
        check(replies.SequenceEqual(["", "", "", "", "answered"]) && !seen.Any(r => r.Contains("\"later\"", StringComparison.Ordinal)),
            "Cancelling a running call and a queued one: each frame keeps its one reply in order, and the queued call never starts");
    }

    static void Workspace(Action<bool, string> check, string fixture)
    {
        StoredWorkspace workspace = WorkspaceStore.Create("Tool hangs " + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(workspace.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false });
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
        try
        {
            WorkspaceControl control = runtime.Plane!;
            Windows(check, control, runtime.Computer!.Name);
            Cancellation(check, runtime.Access!);
            Orphans(check, control, fixture);
        }
        finally { runtime.Dispose(); }
    }

    /// <summary>The wait tool's window watch, against windows made on the workspace desktop.</summary>
    static void Windows(Action<bool, string> check, WorkspaceControl control, string desktop)
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        using var owner = new WindowThread(desktop, "Tool hangs already open " + tag);
        var took = Stopwatch.StartNew();
        string why = control.Until(TimeSpan.FromSeconds(10), 0, "already open " + tag).GetAwaiter().GetResult();
        check(why.StartsWith("a window appeared", StringComparison.Ordinal) && took.Elapsed < TimeSpan.FromSeconds(5),
            "A wait for a title that is already on screen ends at once instead of at its limit");

        Task<string> child = control.Until(TimeSpan.FromSeconds(3), 0, "typed " + tag);
        Thread.Sleep(500);
        owner.Retitle(owner.Inner, "typed " + tag);
        check(child.GetAwaiter().GetResult() == "the timer",
            "A control inside a window taking the text does not end a wait for a window title");

        took.Restart();
        Task<string> renamed = control.Until(TimeSpan.FromSeconds(10), 0, "loaded " + tag);
        Thread.Sleep(500);
        owner.Retitle(owner.Top, "Tool hangs loaded " + tag);
        why = renamed.GetAwaiter().GetResult();
        check(why.StartsWith("a window appeared", StringComparison.Ordinal) && took.Elapsed < TimeSpan.FromSeconds(5),
            "A window that takes the title after it opened ends the wait when it is renamed");
    }

    /// <summary>The real wait tool behind a pipe server: its 30-second default, and the client's cancel.</summary>
    static void Cancellation(Action<bool, string> check, WorkspaceExternalAccess access)
    {
        using var server = new WorkspacePipeServer(() => access.Attach());
        using var pipe = Connect(server);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string never = "never " + Guid.NewGuid().ToString("N");

        var took = Stopwatch.StartNew();
        Send(pipe, Call(1, new { window = never }), bound.Token);
        string reply = Receive(pipe, bound.Token);
        check(reply.Contains("woke on the timer", StringComparison.Ordinal)
            && took.Elapsed > TimeSpan.FromSeconds(28) && took.Elapsed < TimeSpan.FromSeconds(40),
            $"wait with a window title and no seconds ends on its 30-second default ({took.Elapsed.TotalSeconds:F1} s)");

        took.Restart();
        Send(pipe, Call(2, new { window = never, seconds = 60 }), bound.Token);
        Thread.Sleep(1000);
        Send(pipe, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2,"reason":"probe"}}""", bound.Token);
        Send(pipe, """{"jsonrpc":"2.0","id":3,"method":"ping"}""", bound.Token);
        string cancelled = Receive(pipe, bound.Token), notice = Receive(pipe, bound.Token), ping = Receive(pipe, bound.Token);
        check(cancelled.Length == 0 && notice.Length == 0 && ping.Contains("\"id\":3", StringComparison.Ordinal)
            && took.Elapsed < TimeSpan.FromSeconds(5),
            $"notifications/cancelled ends the running wait at once with no answer, and the connection goes on ({took.Elapsed.TotalSeconds:F1} s)");
    }

    /// <summary>A command's grandchild whose parent already exited is killed when the command is cancelled.</summary>
    static void Orphans(Action<bool, string> check, WorkspaceControl control, string fixture)
    {
        string report = Path.Combine(fixture, "orphan.json");
        // The inner cmd starts the grandchild and exits at once, so no live process leads to it.
        string command = "cmd /d /c start \"\" /b \"" + Environment.ProcessPath + "\" --grandchild \"" + report + "\""
            + "\r\nping -n 120 127.0.0.1 >nul";
        CommandJob job = control.Commands.Start(command, false, 0, out string? error)
            ?? throw new InvalidOperationException("The orphan command did not start: " + error);
        var waited = Stopwatch.StartNew();
        int orphan = 0;
        while (orphan == 0 && waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(100);
            try { using var json = JsonDocument.Parse(File.ReadAllText(report)); orphan = json.RootElement.GetProperty("pid").GetInt32(); }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        }
        check(orphan != 0 && job.Running, "The command is running and its grandchild has reported in");
        check(control.Commands.Cancel(job.Id, "the probe", out _), "The command accepts cancel");
        waited.Restart();
        while (!Exited(orphan) && waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        bool gone = Exited(orphan);
        if (!gone) try { Process.GetProcessById(orphan).Kill(); } catch (ArgumentException) { }
        check(gone, "Cancelling a command also ends the grandchild its exited parent left running");
    }

    static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    static string Call(int id, object arguments) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "wait", arguments },
    });

    static NamedPipeClientStream Connect(WorkspacePipeServer server)
    {
        var pipe = new NamedPipeClientStream(".", server.Name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        pipe.ConnectAsync(bound.Token).GetAwaiter().GetResult();
        WorkspacePipeProtocol.Write(pipe, server.Capability, 256, bound.Token).GetAwaiter().GetResult();
        if (WorkspacePipeProtocol.Read(pipe, 256, bound.Token).GetAwaiter().GetResult() != "workspace-pipe/1")
            throw new IOException("The tool-hangs pipe did not authenticate.");
        return pipe;
    }

    static void Send(Stream pipe, string frame, CancellationToken cancel) =>
        WorkspacePipeProtocol.Write(pipe, frame, WorkspacePipeProtocol.MaxRequestBytes, cancel).GetAwaiter().GetResult();

    static string Receive(Stream pipe, CancellationToken cancel) =>
        WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxResponseBytes, cancel).GetAwaiter().GetResult()
            ?? throw new IOException("The pipe closed before replying.");

    /// <summary>
    /// A top-level window with a control inside, on the workspace desktop, on a thread that keeps
    /// pumping: the watch reads titles with GetWindowText, which for a window in this process is a
    /// message to this thread.
    /// </summary>
    sealed class WindowThread : IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
        readonly Thread _thread;
        internal nint Top, Inner;

        internal WindowThread(string desktop, string title)
        {
            using var made = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                nint handle = Native.OpenDesktopW(desktop, 0, false, Native.GenericAll);
                if (handle != 0 && Native.SetThreadDesktop(handle))
                {
                    // WS_OVERLAPPEDWINDOW | WS_VISIBLE, and a WS_CHILD | WS_VISIBLE button inside it. Button, not
                    // static or edit: those two do not raise a name change when their text is set.
                    Top = Native.CreateWindowExW(0, "BUTTON", title, 0x10CF0000, 40, 40, 400, 200, 0, 0, 0, 0);
                    if (Top != 0) Inner = Native.CreateWindowExW(0, "BUTTON", "", 0x50000000, 10, 10, 300, 24, Top, 0, 0, 0);
                }
                made.Set();
                while (!_work.IsCompleted)
                {
                    while (PeekMessageW(out Native.Msg message, 0, 0, 0, 1)) Native.DispatchMessageW(ref message);
                    if (_work.TryTake(out Action? work, 10)) work();
                }
                if (Top != 0) Native.DestroyWindow(Top);
                if (handle != 0) Native.CloseDesktop(handle);
            }) { IsBackground = true, Name = "tool-hangs-window" };
            _thread.Start();
            made.Wait(TimeSpan.FromSeconds(5));
            if (Top == 0 || Inner == 0) throw new InvalidOperationException("The probe window could not be made on " + desktop + ".");
        }

        internal void Retitle(nint window, string text)
        {
            using var done = new ManualResetEventSlim();
            _work.Add(() => { SetWindowTextW(window, text); done.Set(); });
            done.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose() { _work.CompleteAdding(); _thread.Join(TimeSpan.FromSeconds(5)); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool PeekMessageW(out Native.Msg message, nint window, uint first, uint last, uint remove);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SetWindowTextW(nint window, string text);
    }
}
