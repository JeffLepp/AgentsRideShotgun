using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

/// <summary>
/// Taking a window out of a workspace onto the owner's desktop, through the real input path a view
/// uses: a title bar dragged off the picture, and Open on my desktop. Nothing appears on the owner's
/// screen. The fixture app shows a window only inside the workspace; started on the owner's desktop
/// it writes down where it landed, with what arguments and in which folder, and exits.
/// </summary>
static class PopOutProbe
{
    const string Title = "Pop-out fixture";
    const string Spaced = "two words";

    /// <summary>The fixture: a window in a workspace, a written receipt on the owner's desktop.</summary>
    internal static int Fixture(string file, string label)
    {
        try { return FixtureCore(file, label); }
        catch (Exception ex)
        {
            try { File.WriteAllText(file + ".error.txt", ex.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return 3;
        }
    }

    static int FixtureCore(string file, string label)
    {
        string desktop = DesktopName();
        var receipt = new
        {
            desktop, pid = Environment.ProcessId, label, folder = Environment.CurrentDirectory,
            commandLine = Environment.CommandLine,
        };
        if (desktop == "Default")
        {
            File.WriteAllText(file + ".owner.json", JsonSerializer.Serialize(receipt));
            return 0;
        }
        File.WriteAllText(file + ".workspace.json", JsonSerializer.Serialize(receipt));
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = Title, Width = 420, Height = 300, Left = 240, Top = 180,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new TextBlock { Text = label, Margin = new Thickness(20) },
        };
        window.Closed += (_, _) => File.WriteAllText(file + ".closed", "closed");
        return app.Run(window);
    }

    internal static int RunStandalone(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "pf-" + Guid.NewGuid().ToString("N")[..8]);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(fixture, "settings.json"));
        using var watchdog = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Pop-out probe exceeded 180 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        string? failure = null;
        try { Run(Check, fixture); }
        catch (Exception ex) { failure = ex.ToString(); }
        finally { WorkspaceRuntime.Rest(); }
        string engine = typeof(WorkspaceRuntime).Assembly.Location;
        File.WriteAllText(Path.Combine(output, "pop-out.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), engine,
            engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine))),
            fixture, claims, failure, globalInputEventsSent = 0, modelCalls = 0, ownerScreenWindows = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException(claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    internal static void Run(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder);
        StoredWorkspace stored = WorkspaceStore.Create("PopOut" + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceAccessStore.Write(stored.Id, new WorkspaceAccessPolicy(true, true) { PrewarmBrowser = false });
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        AgentDesktop computer = runtime.Computer!;

        // The picture a view shows, off every monitor, at the corner window's small size.
        var picture = new Image
        {
            Width = 344, Height = 215, Stretch = Stretch.UniformToFill,
            // A view always has the workspace's frame in it; the size is what the input maps by.
            Source = new System.Windows.Media.Imaging.WriteableBitmap(AgentDesktop.ScreenWidth, AgentDesktop.ScreenHeight,
                96, 96, PixelFormats.Bgr32, null),
        };
        var host = new Window
        {
            Left = -20000, Top = -20000, Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = picture,
        };
        host.Show();
        host.UpdateLayout();
        Until(() => picture.ActualWidth > 0, 5);
        using var input = new WorkspaceScreenInput(picture, () => runtime);
        var said = new List<string>();
        input.PoppedOut += said.Add;

        // --- drag out: the relaunch lands on the owner's desktop, exactly as it was started -------
        // Receipts go in the workspace's own folder: an app in a workspace cannot write elsewhere.
        string receipts = computer.Folder!;
        string first = Path.Combine(receipts, "first");
        (nint window, int pid, AgentWindow at) = Start(computer, first, check);
        using var json = JsonDocument.Parse(File.ReadAllText(first + ".workspace.json"));
        string workspaceFolder = json.RootElement.GetProperty("folder").GetString()!;
        check(json.RootElement.GetProperty("desktop").GetString() == computer.Name,
            "The fixture window is on the workspace's own desktop");

        WorkspacePopOut.Plan? plan = WorkspacePopOut.For(runtime, window);
        check(plan is { Page: false } && string.Equals(plan.Program, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && plan.Arguments is { } args && args.Contains("\"" + Spaced + "\"", StringComparison.Ordinal)
            && args.StartsWith("--popout-fixture", StringComparison.Ordinal)
            && string.Equals(plan.Folder, workspaceFolder, StringComparison.OrdinalIgnoreCase),
            "A workspace app's window reads back as its own program, its exact arguments and its folder");
        check(WorkspacePopOut.For(runtime, 0) is null, "No window, nothing to take out");
        check(WorkspacePopOut.AfterProgram("\"C:\\a b\\x.exe\" --one \"two words\"") == "--one \"two words\""
            && WorkspacePopOut.AfterProgram("x.exe") is null && WorkspacePopOut.AfterProgram("x.exe  -v ") == "-v",
            "Arguments are everything after the program, quoted or not, exactly as written");

        // Hovering finds the window the owner can see at that point: its own console may lie over it
        // until it is brought forward, and then the console is what is there.
        computer.Arrange(window, WindowArrangement.Front);
        Point over = ToPicture(picture, at.X + at.Width / 2.0, at.Y + at.Height / 2.0);
        (nint Window, Rect Area)? hovered = Wait(() => input.PoppableAt(over));
        check(hovered is { } h && h.Window == window && h.Area.Width > 10,
            "Hovering the window on the picture finds it and where it sits there");

        Point grab = ToPicture(picture, at.X + at.Width / 2.0, at.Y + 12);
        check(input.Down(grab, false), "A press on the fixture's title bar is taken");
        check(Until(() => computer.MovingWindow == window), "The press took hold of the window by its title bar");
        input.Moved(new Point(grab.X + 10, grab.Y + 6));
        input.Moved(new Point(-60, grab.Y));
        Pump();
        input.Up(new Point(-60, grab.Y));
        check(Until(() => File.Exists(first + ".owner.json"), 20),
            "Letting go off the picture starts the app again on the owner's desktop");
        using (var owner = JsonDocument.Parse(File.ReadAllText(first + ".owner.json")))
        {
            check(owner.RootElement.GetProperty("desktop").GetString() == "Default"
                && owner.RootElement.GetProperty("label").GetString() == Spaced
                && string.Equals(owner.RootElement.GetProperty("folder").GetString(), workspaceFolder, StringComparison.OrdinalIgnoreCase),
                "The owner's copy runs on his own desktop with the same arguments, spaces intact, in the same folder");
        }
        check(Until(() => said.Count > 0, 20) && said[0].Contains("stays here", StringComparison.Ordinal),
            "A copy that closes straight away on the owner's desktop leaves the workspace's copy where it was, and says so");
        check(!Process.GetProcessById(pid).HasExited && !File.Exists(first + ".closed"),
            "The workspace's copy is still running when the owner's copy did not stay up");
        AgentWindow back = computer.Windows().First(w => w.Handle == window);
        check(Math.Abs(back.X - at.X) <= 2 && Math.Abs(back.Y - at.Y) <= 2,
            "The dragged window went back where it was in the workspace once it left the picture");

        // --- resizing by an edge: Windows' own border is under a pixel on this picture -----------
        computer.Arrange(window, WindowArrangement.Front);
        AgentWindow shows = AgentDesktop.Drawn(computer.Windows().First(w => w.Handle == window));
        // Just outside the visible bottom-right corner, where the owner would reach for it.
        Point corner = ToPicture(picture, shows.X + shows.Width, shows.Y + shows.Height);
        corner.Offset(3, 3);
        check(Until(() => { input.Hover(corner); return input.CursorForTests == System.Windows.Input.Cursors.SizeNWSE; }),
            "Hovering just past a window's corner on the picture shows the resize cursor");
        Point mid = ToPicture(picture, shows.X + shows.Width / 2.0, shows.Y + shows.Height / 2.0);
        input.Hover(mid);
        check(input.CursorForTests != System.Windows.Input.Cursors.SizeNWSE,
            "Inside the window the picture's own cursor comes back");
        check(input.Down(corner, false), "A press on the corner is taken");
        for (int step = 1; step <= 10; step++) input.Moved(new Point(corner.X + step * 3, corner.Y + step * 2));
        Pump();
        input.Up(new Point(corner.X + 30, corner.Y + 20));
        AgentWindow? grown = null;
        check(Until(() => (grown = AgentDesktop.Drawn(computer.Windows().First(w => w.Handle == window))) is { } g
                && g.Width > shows.Width + 40 && g.Height > shows.Height + 30),
            "Dragging the corner makes the window bigger, both ways");
        check(Math.Abs(grown!.X - shows.X) <= 2 && Math.Abs(grown.Y - shows.Y) <= 2,
            "The opposite corner stays put while the dragged one moves");

        // --- the button: the owner's copy stays up, so the workspace's closes and the agent hears --
        using var agent = new Program.Client(stored.Id);
        agent.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "Pop-out probe", version = "1" } });
        agent.Tool("status");
        string second = Path.Combine(receipts, "second");
        (nint window2, int pid2, _) = Start(computer, second, check);
        said.Clear();
        WorkspacePopOut.WindowOfForTests = (_, _) => Task.FromResult((nint)1);
        try
        {
            Task pressed = input.PopOut(window2);
            check(Until(() => pressed.IsCompleted && said.Count > 0, 20) && said[0].Contains("on your desktop now", StringComparison.Ordinal),
                "Open on my desktop reports the app is on the owner's desktop now");
        }
        finally { WorkspacePopOut.WindowOfForTests = null; }
        check(Until(() => File.Exists(second + ".closed"), 15) && Until(() => Exited(pid2), 10),
            "Once the owner's copy is up, the workspace's copy is closed the way its own close button would");
        check(Until(() => File.Exists(second + ".owner.json"), 10), "The button starts the owner's copy too");
        string reply = Program.Client.Text(agent.Tool("status"));
        check(reply.StartsWith("Meanwhile:", StringComparison.Ordinal) && reply.Contains("moved", StringComparison.Ordinal)
            && reply.Contains(Title, StringComparison.Ordinal),
            "The agent's next tool reply tells it the owner moved that app to their own desktop");
        check(!Program.Client.Text(agent.Tool("status")).StartsWith("Meanwhile:", StringComparison.Ordinal),
            "The note is told once, not on every reply");

        StopFixture(pid);
        host.Close();
    }

    static (nint Window, int Pid, AgentWindow At) Start(AgentDesktop computer, string file, Action<bool, string> check)
    {
        int pid = computer.Launch(Environment.ProcessPath!, $"--popout-fixture \"{file}\" \"{Spaced}\"");
        check(pid > 0, "The fixture app starts in the workspace");
        AgentWindow? shown = null;
        check(Until(() => (shown = computer.Windows().FirstOrDefault(w => w.Title == Title && Owner(w.Handle) == pid)) is not null, 30),
            "The fixture's window is up in the workspace");
        return (shown!.Handle, pid, shown);
    }

    static int Owner(nint window) { GetWindowThreadProcessId(window, out int pid); return pid; }

    static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    static void StopFixture(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The same arithmetic a view uses to draw the workspace screen on its picture.</summary>
    static Point ToPicture(Image picture, double x, double y)
    {
        double width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
        double scale = Math.Max(picture.ActualWidth / width, picture.ActualHeight / height);
        double left = (picture.ActualWidth - width * scale) / 2, top = (picture.ActualHeight - height * scale) / 2;
        return new Point(left + x * scale, top + y * scale);
    }

    static bool Until(Func<bool> done, int seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (done()) return true;
            Pump();
            Thread.Sleep(50);
        }
        return done();
    }

    static T? Wait<T>(Func<Task<T>> ask)
    {
        Task<T> task = ask();
        Until(() => task.IsCompleted, 10);
        return task.IsCompletedSuccessfully ? task.Result : default;
    }

    /// <summary>Lets the dispatcher run what the view queued, as its own loop would.</summary>
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    static string DesktopName()
    {
        var name = new StringBuilder(256);
        GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _);
        return name.ToString();
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(nint window, out int processId);
}
