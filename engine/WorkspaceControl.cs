using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>Who is driving a workspace. Exactly one of these at a time, and the owner always wins.</summary>
public enum Driver { Nobody, Owner, Agent }

/// <summary>
/// A copy of a program running outside a workspace, which is why a launch inside it quit at once.
/// </summary>
/// <param name="Id">Its process id.</param>
/// <param name="File">The executable it was started from, or empty when Windows would not say.</param>
/// <param name="SameFile">It is the very file the workspace tried to start, not another install of it.</param>
public sealed record WorkspaceCopy(int Id, string File, bool SameFile);

/// <summary>
/// Whether the picture of a workspace is the one on its screen right now. A workspace holding an
/// application that has stopped pumping keeps showing its last frame, and everything that shows a
/// frame says so rather than passing an old picture off as the current one.
/// </summary>
/// <param name="Stale">The picture is older than this moment.</param>
/// <param name="Taken">When the picture being shown was actually captured.</param>
/// <param name="Unresponsive">Windows left out of it because their thread is not answering.</param>
/// <param name="PumpBusy">The workspace's own desktop thread did not answer in time.</param>
public sealed record WorkspaceScreenState(bool Stale, DateTimeOffset? Taken, int Unresponsive, bool PumpBusy)
{
    /// <summary>One line for a person or an agent, empty when the picture is simply current.</summary>
    public string Note => !Stale && Unresponsive == 0 ? string.Empty
        : (Stale
            ? "STALE: this is the last picture of the workspace"
                + (Taken is { } when ? ", taken at " + when.ToLocalTime().ToString("HH:mm:ss") : "")
            : "This picture is current")
        + (Unresponsive > 0
            ? $". {Unresponsive} window{(Unresponsive == 1 ? " is" : "s are")} not responding, shown greyed as last seen"
            : PumpBusy ? ". The workspace desktop did not answer in time" : "")
        + ".";

    /// <summary>The short form the owner sees on the panel badge.</summary>
    public string Badge => Stale
        ? "Not responding" + (Taken is { } when ? " · last frame " + when.ToLocalTime().ToString("HH:mm:ss") : "")
        : Unresponsive > 0 ? $"{Unresponsive} not responding" : string.Empty;
}

/// <summary>
/// The control plane. One object per running workspace, holding the three perception layers, the
/// lease that decides who is driving, the evidence written beside the workspace, and the wait that
/// lets a workspace sit idle without a loop and without a model.
///
/// Every action goes through here, which is the only reason the evidence is complete and the only
/// reason the lease cannot be sidestepped.
/// </summary>
public sealed partial class WorkspaceControl : IDisposable
{
    readonly AgentDesktop _desktop;
    readonly WorkspaceTree _tree;
    readonly WorkspaceEvidence _evidence;
    readonly WorkspaceCommands _commands;
    readonly Lock _gate = new();
    WorkspaceBrowser? _browser;
    long _agentLease;
    readonly AsyncLocal<long?> _requiredLease = new();
    CancellationTokenSource _inputStop = new();
    bool _disposed;

    public WorkspaceControl(AgentDesktop desktop)
    {
        _desktop = desktop;
        _tree = new WorkspaceTree(desktop.Name);
        _evidence = new WorkspaceEvidence(desktop.Folder ?? Path.GetTempPath());
        _commands = new WorkspaceCommands(this);
        _evidence.Note("workspace", desktop.Name, "control plane up");
        _desktop.Pulled += Pulled;
    }

    /// <summary>
    /// A window that opened off the workspace screen was brought back. Written down because the
    /// agent sees a window move it did not ask for, and the owner sees one arrive from nowhere.
    /// </summary>
    void Pulled(string what) => _evidence.Note("window", what, "pulled onto the workspace screen");

    public WorkspaceEvidence Evidence => _evidence;

    /// <summary>
    /// Commands started here, tracked as jobs that outlive the tool call that started them. One
    /// store per workspace rather than per connection: a job started by one agent connection is
    /// still findable after that connection drops and a new one acquires the workspace.
    /// </summary>
    public WorkspaceCommands Commands => _commands;

    /// <summary>The workspace's default working folder. Windows determines access to other files.</summary>
    public string Folder => _desktop.Folder ?? string.Empty;

    internal bool OwnsProcess(int processId) => _desktop.OwnsProcess(processId);

    /// <summary>Where the browser transport ended up, for the panel and the log. Empty until it starts.</summary>
    public string BrowserTransport => _browser?.Transport ?? string.Empty;

    // --- the lease ---------------------------------------------------------------------------

    /// <summary>Who is driving. The panel reads this and says so.</summary>
    public Driver Driving { get; private set; } = Driver.Nobody;

    /// <summary>Raised whenever <see cref="Driving"/> changes, so the panel never has to poll it.</summary>
    public event Action<Driver>? DriverChanged;

    /// <summary>
    /// The owner takes the workspace. Instant: it never waits for an agent action to finish, and
    /// input the agent had already queued is dropped on the desktop pump rather than landing under
    /// his hands.
    /// </summary>
    public void OwnerTakes()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            _desktop.Revoke();
            _agentLease = 0;
            CancelInput();
            changed = Became(Driver.Owner);
        }
        if (changed) DriverChanged?.Invoke(Driver.Owner);
        _evidence.Note("control", "owner", "took control");
    }

    /// <summary>The agent asks for the workspace. Refused outright while the owner holds it.</summary>
    public bool AgentTakes()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || Driving == Driver.Owner) { _evidence.Note("control", "agent", "refused, owner is driving or workspace closed"); return false; }
            _agentLease = _desktop.Lease;
            changed = Became(Driver.Agent);
        }
        if (changed) DriverChanged?.Invoke(Driver.Agent);
        _evidence.Note("control", "agent", "took control");
        return true;
    }

    /// <summary>Nobody is driving. A workspace can run without either of them at the wheel.</summary>
    public void Release()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            _desktop.Revoke();
            _agentLease = 0;
            CancelInput();
            changed = Became(Driver.Nobody);
        }
        if (changed) DriverChanged?.Invoke(Driver.Nobody);
        _evidence.Note("control", "-", "released");
    }

    /// <summary>An agent lets go: nobody drives, unless the owner took the wheel in the meantime.
    /// Checked and done under one lock, so letting go can never take the wheel from the owner.</summary>
    public void AgentLetsGo()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || Driving == Driver.Owner) return;
            _desktop.Revoke();
            _agentLease = 0;
            CancelInput();
            changed = Became(Driver.Nobody);
        }
        if (changed) DriverChanged?.Invoke(Driver.Nobody);
        _evidence.Note("control", "-", "released");
    }

    // The event is raised by the caller after the lock is let go: its listeners reach back into the
    // corner and the agent access, which take their own locks, and holding this one across them
    // is a lock-order deadlock with any thread that takes those first.
    bool Became(Driver who)
    {
        if (Driving == who) return false;
        Driving = who;
        return true;
    }

    /// <summary>The number the agent's input must carry, or zero when it is not allowed to act.</summary>
    long Ticket
    {
        get { lock (_gate) return !_disposed && Driving == Driver.Agent
            && (_requiredLease.Value is not { } required || required == _agentLease) ? _agentLease : 0; }
    }

    internal long CurrentLease => Ticket;
    internal IDisposable RequireLease(long lease)
    {
        long? previous = _requiredLease.Value;
        _requiredLease.Value = lease;
        return new LeaseScope(() => _requiredLease.Value = previous);
    }
    sealed class LeaseScope(Action restore) : IDisposable { public void Dispose() => restore(); }

    void CancelInput()
    {
        var old = _inputStop;
        _inputStop = new();
        old.Cancel();
        old.Dispose();
    }
    CancellationTokenSource InputScope(long ticket, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                var closed = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                closed.Cancel();
                return closed;
            }
            var scope = CancellationTokenSource.CreateLinkedTokenSource(cancel, _inputStop.Token);
            if (ticket == 0 || Ticket != ticket) scope.Cancel();
            return scope;
        }
    }

    bool AgentMayAct => Ticket != 0;

    // --- perception --------------------------------------------------------------------------

    public IReadOnlyList<AgentWindow> Windows() => _desktop.Windows();

    /// <summary>
    /// Layer 1. One window's controls. Deliberately not the whole desktop: measured 2026-08-23, all
    /// the trees on a four-application desktop together cost more than a picture of that desktop.
    /// </summary>
    public IReadOnlyList<WorkspaceElement> Elements(nint window, bool everything = false)
    {
        if (!_desktop.Windows().Any(open => open.Handle == window)) return [];
        IReadOnlyList<WorkspaceElement> found = _tree.Read(window, everything);
        _evidence.Note("elements", Describe(window), found.Count + " published");
        return found;
    }

    /// <summary>
    /// Layer 2, always available. Any window at any time, including one being driven through the
    /// browser protocol - which is the only way to answer a captcha or a visual check. Pass zero for
    /// the whole workspace screen.
    /// </summary>
    public BitmapSource? Shot(nint window = 0, bool marks = false)
    {
        BitmapSource? frame = window == 0 ? Screen() : _desktop.CaptureWindow(window);
        if (frame is not null && marks)
        {
            (IReadOnlyList<WorkspaceElement> found, System.Windows.Point origin) = MarksFor(window);
            frame = WorkspaceMarks.Draw(frame, found, origin, 1);
            _evidence.Note("marks", window == 0 ? "the whole screen" : Describe(window),
                found.Count + " controls numbered on the picture");
        }
        _evidence.Note("shot", window == 0 ? "the whole screen" : Describe(window),
            frame is null ? "nothing to photograph" : $"{frame.PixelWidth}x{frame.PixelHeight}");
        return frame;
    }

    /// <summary>
    /// Gives a window that has just stopped answering a moment to finish before it is photographed.
    /// An app is often busy for a beat right after a click while it switches views, and a picture
    /// taken inside that beat shows its last frame instead of what the click did. Costs one ask when
    /// everything answers; a window that is really hung costs this wait and is then drawn greyed.
    /// </summary>
    /// <param name="afterInput">
    /// Right after input, the app may not have started the work the input asked for, and would
    /// answer an ask made that instant only to go quiet a moment later. A short beat first lets the
    /// work start - and lets the click's own repaint reach the picture.
    /// </param>
    internal async Task Settle(nint window, CancellationToken cancel, bool afterInput = false)
    {
        try
        {
            if (afterInput) await Task.Delay(InputBeatMilliseconds, cancel).ConfigureAwait(false);
            long until = Environment.TickCount64 + SettleMilliseconds;
            while (!_desktop.Answers(window) && Environment.TickCount64 < until)
                await Task.Delay(100, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    const int SettleMilliseconds = 2000, InputBeatMilliseconds = 150;

    /// <summary>
    /// The workspace screen, or the last one it had. A capture is bounded now, so it can come back
    /// with nothing when an application has stopped pumping. Handing back null in that case would
    /// blank the panel and tell an agent the workspace was empty, which is false; the last picture
    /// with an honest stale label is the truthful answer and the owner's chosen one.
    /// </summary>
    BitmapSource? Screen(bool record = true)
    {
        DesktopFrame frame = _desktop.Capture(
            Native.GetSystemMetrics(Native.SmCxScreen), Native.GetSystemMetrics(Native.SmCyScreen));
        lock (_frameGate)
        {
            if (frame.Image is not null)
            {
                _lastFrame = frame.Image;
                _lastFrameAt = DateTimeOffset.Now;
            }
            // An empty desktop is not a stall. There is nothing to photograph and nothing is wrong.
            if (record)
                _screen = frame.Image is not null || (frame.Windows == 0 && !frame.TimedOut)
                    ? new WorkspaceScreenState(false, frame.Image is null ? null : _lastFrameAt, frame.Unresponsive, false)
                    : new WorkspaceScreenState(true, _lastFrame is null ? null : _lastFrameAt, frame.Unresponsive, frame.TimedOut);
            return frame.Image ?? _lastFrame;
        }
    }

    /// <summary>
    /// A picture for a second viewer - the corner view - which must not change what the panel and
    /// the agent believe about this workspace. The last one is handed back while it is still this
    /// fresh, so two viewers of one workspace cost one capture rather than two; only when there is
    /// no fresh picture does it take its own, and a capture refused because another was already in
    /// flight leaves the screen state alone rather than recording a stall that did not happen.
    /// </summary>
    public BitmapSource? Glance(TimeSpan fresh)
    {
        lock (_frameGate)
            if (_lastFrame is { } picture && DateTimeOffset.Now - _lastFrameAt < fresh) return picture;
        return Screen(record: false);
    }

    /// <summary>
    /// A picture for the panel. Deliberately the same bounded capture the agent gets, so the owner
    /// and the agent never disagree about whether the workspace is answering, and with no evidence
    /// line: the panel photographs twice a second and an action log is not a frame counter.
    /// </summary>
    public BitmapSource? Frame() => Screen();

    /// <summary>The last picture anything took of the screen, without taking a new one.</summary>
    internal BitmapSource? LastFrame { get { lock (_frameGate) return _lastFrame; } }

    readonly Lock _frameGate = new();
    BitmapSource? _lastFrame;
    DateTimeOffset _lastFrameAt;
    WorkspaceScreenState _screen = new(false, null, 0, false);

    /// <summary>What the last picture of this workspace actually was, for the panel and the agent.</summary>
    public WorkspaceScreenState ScreenState { get { lock (_frameGate) return _screen; } }

    /// <summary>
    /// The controls to number on a picture, and the screen point that picture's top-left corner is.
    /// A whole-screen shot marks the front window - the one the agent is working in - because
    /// reading every window's tree costs more than the picture it is drawn on.
    /// </summary>
    internal (IReadOnlyList<WorkspaceElement>, System.Windows.Point) MarksFor(nint window)
    {
        IReadOnlyList<AgentWindow> open = _desktop.Windows();
        AgentWindow? subject = window == 0
            ? open.FirstOrDefault()
            : open.FirstOrDefault(one => one.Handle == window);
        if (subject is null) return ([], new System.Windows.Point(0, 0));
        IReadOnlyList<WorkspaceElement> found = _tree.Read(subject.Handle, false);
        // A whole-screen picture is already in screen coordinates; one window's picture starts at
        // that window's corner, and the element rectangles are in screen coordinates either way.
        // A window's picture starts where it visibly starts, inside its invisible border.
        AgentWindow shows = AgentDesktop.Drawn(subject);
        return (found, window == 0
            ? new System.Windows.Point(0, 0)
            : new System.Windows.Point(shows.X, shows.Y));
    }

    /// <summary>Whatever text an element holds. The cheap way to read a document or a field.</summary>
    public string TextOf(int elementId) => _tree.Text(elementId);

    // --- action ------------------------------------------------------------------------------

    /// <summary>
    /// Presses a current element. A standard Win32 button or a control without an action pattern
    /// may use its validated point;
    /// a stale control or a provider failure is refused without replaying the action.
    /// </summary>
    public bool Press(int elementId) => Press(elementId, Ticket);

    bool Press(int elementId, long ticket)
    {
        if (ticket == 0 || Ticket != ticket) return false;
        WorkspaceElement? element = _tree.Known(elementId);
        if (element is null) { Note("press", elementId.ToString(), "no such element"); return false; }

        var result = _tree.TryPress(elementId, () => Ticket == ticket);
        if (result == WorkspaceTree.TreeAction.Applied) { Note("press", element.ToString(), "through the tree"); return true; }
        if (result != WorkspaceTree.TreeAction.Unsupported || _tree.Validate(elementId) is not null)
        { Note("press", element.ToString(), "refused; read controls again"); return false; }
        if (!_desktop.Click(element.CentreX, element.CentreY, false, ticket)) return Discarded("press", element.ToString());
        Note("press", element.ToString(), $"by point ({element.CentreX},{element.CentreY})");
        return true;
    }

    /// <summary>
    /// Puts text into an element. Measured 2026-08-23: Notepad's document publishes no way to be
    /// written to, so this focuses it through the tree and types through layer 2. That is the
    /// ordinary case on Windows, not the exception.
    /// </summary>
    public bool Write(int elementId, string text) => Write(elementId, text, Ticket);

    bool Write(int elementId, string text, long ticket)
    {
        if (ticket == 0 || Ticket != ticket) return false;
        WorkspaceElement? element = _tree.Known(elementId);
        if (element is null) { Note("write", elementId.ToString(), "no such element"); return false; }

        var result = _tree.TryWrite(elementId, text, () => Ticket == ticket);
        if (result == WorkspaceTree.TreeAction.Applied) { Note("write", element.ToString(), "through the tree"); return true; }
        if (result != WorkspaceTree.TreeAction.Unsupported || _tree.Validate(elementId) is not null
            || !_tree.Focus(elementId, () => Ticket == ticket))
        { Note("write", element.ToString(), "refused; read controls again"); return false; }
        TypedText typed = _desktop.Type(text, _tree.WindowOf(elementId), ticket);
        if (typed == TypedText.Discarded) return Discarded("write", element.ToString());
        // A failed type used to be filed as if the owner had taken control, which put the wrong
        // reason in the evidence log and told the caller nothing about the control that refused it.
        Note("write", element.ToString(), "focused by the tree, then " + typed);
        return typed.Landed;
    }

    /// <summary>Executes one locally validated sequence without another model turn per action.</summary>
    public WorkspaceBatchResult Batch(IReadOnlyList<WorkspaceBatchAction> actions, CancellationToken cancel = default)
    {
        long ticket = Ticket;
        bool OwnsLease() => ticket != 0 && Ticket == ticket;
        WorkspaceBatchResult result = WorkspaceBatch.Execute(actions, OwnsLease, _tree.Validate, action =>
        {
            if (action.Action == "read") return _tree.ReadCurrent(action.Control, OwnsLease);
            bool applied = action.Action switch
            {
                "press" => Press(action.Control, ticket),
                "write" => Write(action.Control, action.Text!, ticket),
                _ => false,
            };
            return (applied, applied
                ? "executed"
                : "The control did not accept the action.");
        }, cancel);
        _evidence.Note("batch", actions.Count + " steps", $"{result.Status}; {result.Next} executed; {result.Reason}");
        return result;
    }

    /// <summary>Layer 2 by hand, for anything that publishes nothing at all.</summary>
    public bool ClickAt(int x, int y, bool rightButton = false)
    {
        if (!Allowed("click", $"({x},{y})", out long ticket)) return false;
        if (!_desktop.Click(x, y, rightButton, ticket)) return Discarded("click", $"({x},{y})");
        Note("click", $"({x},{y})", rightButton ? "right" : "left");
        return true;
    }

    public bool ScrollAt(int x, int y, int delta)
    {
        if (!Allowed("scroll", $"({x},{y})", out long ticket)) return false;
        if (!_desktop.Scroll(x, y, delta, ticket)) return Discarded("scroll", $"({x},{y})");
        Note("scroll", $"({x},{y})", delta.ToString());
        return true;
    }

    public bool TypeText(string text, nint window = 0) => Type(text, window).Landed;

    /// <summary>
    /// Types, and reports where the text went rather than only that it was sent. With no window
    /// named the keyboard is wherever the desktop last left it, which is not always where the agent
    /// is looking, so the answer carries the control's name and the evidence log records it.
    /// </summary>
    public TypedText Type(string text, nint window = 0)
    {
        if (!Allowed("type", text, out long ticket))
            return new TypedText(false, "", "refused, " + (Driving == Driver.Owner ? "the owner is driving" : "no lease"));
        TypedText typed = _desktop.Type(text, window, ticket);
        if (typed == TypedText.Discarded) { Discarded("type", text); return typed; }
        Note("type", text, typed.ToString());
        return typed;
    }

    public bool Key(int virtualKey, nint window = 0)
    {
        if (!Allowed("key", virtualKey.ToString(), out long ticket)) return false;
        if (!_desktop.SendKey(virtualKey, window, ticket)) return Discarded("key", virtualKey.ToString());
        Note("key", virtualKey.ToString(), "by message");
        return true;
    }

    /// <summary>
    /// Moves, sizes, maximizes, minimizes, restores, raises or closes a window, the way a person
    /// does from its title bar. Needs the lease like any other action: minimizing a window out
    /// from under the owner's hands is exactly what the lease exists to prevent.
    /// </summary>
    public bool Arrange(nint window, WindowArrangement what, int x = 0, int y = 0, int width = 0, int height = 0)
    {
        string detail = what.ToString().ToLowerInvariant() + " " + Describe(window)
            + (what == WindowArrangement.Move ? $" to ({x},{y}){(width > 0 || height > 0 ? $" {width}x{height}" : "")}" : "");
        if (!Allowed("window", detail, out long ticket)) return false;
        if (!_desktop.Arrange(window, what, x, y, width, height, ticket))
        {
            _evidence.Note("window", detail, "refused; the window is gone, not responding, or the owner took control");
            return false;
        }
        Note("window", detail, "done");
        return true;
    }

    // --- files -------------------------------------------------------------------------------

    /// <summary>
    /// Writes a task file with ordinary Windows permissions. Relative paths start in the workspace;
    /// absolute paths and links work as they do for a desktop agent. Access is not task authorization.
    /// </summary>
    public string? Save(string path, string text) => Save(path, text, out _);

    internal string? Save(string path, string text, out string? error)
    {
        error = null;
        if (FilePath(path) is not { } full)
        {
            error = "Invalid file path.";
            _evidence.Note("save", path, error);
            return null;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            _evidence.Note("save", path, "failed: " + ex.Message);
            return null;
        }
        _evidence.Note("save", full, text.Length + " characters");
        return full;
    }

    /// <summary>Reads a task file with the same Windows access as the host process.</summary>
    public string? Load(string path, int limit) => Load(path, limit, out _);

    internal string? Load(string path, int limit, out string? error)
    {
        error = null;
        if (FilePath(path) is not { } full)
        {
            error = "Invalid file path.";
            _evidence.Note("file", path, error);
            return null;
        }
        try
        {
            using var source = new StreamReader(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            char[] buffer = new char[limit + 1];
            int count = source.ReadBlock(buffer, 0, buffer.Length);
            string text = new(buffer, 0, Math.Min(count, limit));
            _evidence.Note("file", full, count > limit ? "read, cut off at " + limit : count + " characters");
            return count > limit ? text + "\n... (cut off)" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            _evidence.Note("file", path, "failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Normalize against the workspace without imposing a second access policy. File I/O follows
    /// links and checks the destination's Windows permissions at the actual operation.
    /// </summary>
    internal string? FilePath(string path)
    {
        string folder = Folder;
        if (folder.Length == 0 || path.Trim().Length == 0) return null;
        try { return Path.GetFullPath(path, Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// Starts an application in the workspace. Owner-side launches need no lease; external agent
    /// launches carry their original lease through process creation. This is the one
    /// place both `open` and `run` go through, so the owner-policy gate is enforced here too.
    /// `run` writes a batch file and starts cmd through this method, so refusing here refuses both.
    ///
    /// The browser is not affected: it starts through <see cref="WorkspaceBrowser"/> on the desktop
    /// directly, so a mission that has read a page can carry on browsing even with blocking on.
    /// </summary>
    /// <param name="quiet">
    /// Set by <see cref="WorkspaceCommands"/> only. A command shell writes one readable "run" line
    /// with the agent's own script instead of an "open cmd.exe /d /s /c ..." line plus a screenshot
    /// nobody reads. The log stays one line per thing that happened rather than two per command.
    /// </param>
    public int Open(string exe, string? arguments = null, bool quiet = false) =>
        Open(exe, arguments, quiet, out _);

    /// <param name="started">
    /// The file Windows was actually asked for, once the plain name the agent used has been looked
    /// up. The caller needs it to say anything true about the process afterwards.
    /// </param>
    /// <inheritdoc cref="Open(string, string?, bool)"/>
    public int Open(string exe, string? arguments, bool quiet, out string started)
    {
        started = exe;
        if (_requiredLease.Value is { } expected && (expected == 0 || Ticket != expected)) return 0;
        // "hivemind" or "chrome" is what a person types into Start; CreateProcess wants the file.
        (exe, arguments) = WorkspacePrograms.Resolve(exe, arguments);
        started = exe;
        if (WorkspacePrograms.ShellOnly(exe))
        {
            _evidence.Note("open", Path.GetFileName(exe), "refused: Windows shell activation requires the owner's desktop");
            return 0;
        }
        int pid = _desktop.Launch(exe, arguments, lease: _requiredLease.Value ?? 0);
        if (!quiet)
            Note("open", Path.GetFileName(exe) + (arguments is null ? "" : " " + arguments),
                pid == 0 ? "REFUSED" : "pid " + pid);
        return pid;
    }

    /// <summary>
    /// The process id of a copy of this program already running outside this workspace, or null.
    ///
    /// Windows applications are routinely one-per-logon-session, and a workspace desktop is inside
    /// the owner's session: a second launch hands its command line to the copy already running and
    /// exits within milliseconds. From in here that is indistinguishable from a program that
    /// started and crashed, and an agent told only "it exited" goes looking for a bug in the
    /// application. Measured 2026-09-07: asked to open HiveMind while the owner's was running, the
    /// agent spent its whole mission on that theory and reported the app as broken.
    /// </summary>
    /// <summary>
    /// The owner's one click when a copy of the program he is testing is already running on his own
    /// desktop: his copy is asked to close, and the program then starts in this workspace, which is
    /// the only way a one-copy-per-session application can be driven in here at all. Nothing closes
    /// until he clicks - this runs from the approval, never from the agent's call. Null when the
    /// workspace has it; one sentence when it does not.
    /// </summary>
    public string? TakeOver(string program, string? arguments)
    {
        (string exe, string? args) = WorkspacePrograms.Resolve(program, arguments);
        if (RunningOutside(exe) is not null && !CloseOutside(exe))
            return "The copy of " + Path.GetFileNameWithoutExtension(exe) + " on the owner's desktop would not close;"
                + " it is probably asking about unsaved work. Close it there and ask again.";
        int pid = Open(exe, args);
        return pid == 0 ? "Windows would not start " + program + " in the workspace." : null;
    }

    /// <summary>Asks every copy outside this workspace to close and waits for it, the way clicking
    /// its X does. Never kills: an application holding unsaved work is the owner's to decide about.</summary>
    bool CloseOutside(string exe)
    {
        Process[] running;
        try { running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return false; }
        try
        {
            foreach (Process other in running)
            {
                if (_desktop.OwnsProcess(other.Id)) continue;
                try { other.CloseMainWindow(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally { foreach (Process other in running) other.Dispose(); }
        // This runs on the owner's click, so it is deliberately short: an application that has
        // nothing to ask about is gone in a few hundred milliseconds, and one that is still up
        // after three seconds is showing him a dialog, which is his to answer, not ours to wait on.
        for (int waited = 0; waited < 3000; waited += 100)
        {
            if (RunningOutside(exe) is null) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    public WorkspaceCopy? RunningOutside(string exe)
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(exe); }
        catch (ArgumentException) { return null; }
        if (name.Length == 0) return null;

        Process[] running;
        try { running = Process.GetProcessesByName(name); }
        catch (InvalidOperationException) { return null; }
        try
        {
            WorkspaceCopy? elsewhere = null;
            foreach (Process other in running)
            {
                if (_desktop.OwnsProcess(other.Id)) continue;
                string file = FileOf(other);
                // The handoff is keyed on a name the program claims for the whole logon session, not
                // on where it was installed, so a copy running from another folder takes the launch
                // just the same. The same file is the certain answer and is taken first; a different
                // one is still reported, with its path, because the owner has to be told which copy
                // he is actually looking at.
                if (file.Length > 0 && file.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    return new WorkspaceCopy(other.Id, file, true);
                elsewhere ??= new WorkspaceCopy(other.Id, file, false);
            }
            return elsewhere;
        }
        finally
        {
            foreach (Process other in running) other.Dispose();
        }
    }

    static string FileOf(Process process)
    {
        try { return process.MainModule?.FileName ?? string.Empty; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// May the agent act, and on which ticket. The ticket is taken once, here, and carried through
    /// the whole action. Re-reading it later is the bug Milestone 4's exit gate found: the owner can
    /// take the workspace between the check and the message, <see cref="Ticket"/> then answers zero,
    /// and zero means "the owner's own input, never discard it" - so the agent's action landed under
    /// his hands, which is the one thing the lease exists to prevent.
    /// </summary>
    bool Allowed(string action, string detail, out long ticket)
    {
        ticket = Ticket;
        if (ticket != 0) return true;
        _evidence.Note(action, detail, "refused, " + (Driving == Driver.Owner ? "the owner is driving" : "no lease"));
        return false;
    }

    /// <summary>
    /// The action reached the pump and the pump threw it away, because the owner took the workspace
    /// between the lease being checked and the message going out. It is written down as discarded,
    /// never as done, and no frame is kept - there is nothing to photograph.
    /// </summary>
    bool Discarded(string action, string detail)
    {
        _evidence.Note(action, detail, "discarded, the owner took control");
        return false;
    }

    void Note(string action, string detail, string outcome) =>
        _evidence.Note(action, detail, outcome, Screen());

    string Describe(nint window)
    {
        foreach (AgentWindow open in _desktop.Windows())
            if (open.Handle == window) return open.Title.Length > 0 ? open.Title : open.ClassName;
        return window.ToString();
    }

    // --- the browser -------------------------------------------------------------------------

    /// <summary>
    /// Layer 3. Starts the workspace's own Chrome on a page and attaches to it. The transport it
    /// ended up with is on <see cref="BrowserTransport"/> and in the log.
    /// </summary>
    public async Task<bool> OpenBrowser(string url, CancellationToken cancel = default) =>
        (await OpenBrowserWithReceipt(url, cancel).ConfigureAwait(false)).Confirmed;

    // Keep the receipt local to this call. A shared "last error" can be replaced by a queued browse
    // between releasing the browser gate and constructing the first agent's response.
    internal async Task<(bool Confirmed, string? Failure)> OpenBrowserWithReceipt(string url,
        CancellationToken cancel = default)
    {
        if (!Allowed("browser", url, out long ticket))
            return (false, "Browser operation refused [control]: this caller does not hold control of the workspace.");
        using var input = InputScope(ticket, cancel);
        // One browser per workspace, started once. Without this gate a browse racing the warm-up
        // starts a second Chrome, which then hands off to the first and cannot be attached to.
        try { await _browserGate.WaitAsync(input.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return RevokedBrowser(); }
        try
        {
        // A browser the owner closed, or whose DevTools connection dropped, is not one to keep
        // navigating. Before this the dead object stayed in place and every later browse failed
        // against it forever, with nothing in the message to say the browser was simply gone.
        if (_browser is { } dead && !dead.Alive)
        {
            Note("browser", url, "the workspace browser had closed; starting a new one");
            dead.Abandon();
            _browser = null;
        }
        if (_browser is { } existing)
        {
            // Disposing only the DevTools connection leaves Chrome holding its profile. Starting
            // a second browser then hands off to the old process and cannot attach reliably.
            // Navigate the existing session instead; never replay a failed/partial navigation.
            bool navigated;
            try { navigated = await existing.Go(url, input.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return RevokedBrowser(); }
            if (cancel.IsCancellationRequested || Ticket != ticket) return RevokedBrowser();
            string? failure = navigated ? null : WorkspaceBrowser.NavigationFailureReceipt(existing.LastProtocolError);
            Note("browser", url, navigated ? "navigated over " + existing.Transport : failure!);
            return (navigated, failure);
        }
        WorkspaceBrowser? started;
        try { started = await WorkspaceBrowser.Start(_desktop, url, true, input.Token, ticket).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return RevokedBrowser(); }
        if (cancel.IsCancellationRequested || Ticket != ticket) { started?.Abandon(); return RevokedBrowser(); }
        _browser = started;
        bool confirmed = _browser?.InitialNavigationConfirmed == true;
        string? receipt = confirmed ? null : _browser is null
            ? WorkspaceBrowser.StartupFailureReceipt(_desktop.Name)
            : WorkspaceBrowser.NavigationFailureReceipt(_browser.LastProtocolError);
        Note("browser", url, confirmed ? "attached over " + _browser!.Transport : receipt!);
        return (confirmed, receipt);
        }
        finally { _browserGate.Release(); }
    }

    static (bool Confirmed, string? Failure) RevokedBrowser() =>
        (false, "Browser operation stopped [control]: control was released or changed. Inspect the workspace before continuing.");

    readonly SemaphoreSlim _browserGate = new(1, 1);

    /// <summary>
    /// Starts the workspace's Chrome on a blank page before anything asks for it, so the first
    /// browse navigates an already-running browser instead of paying the cold start. Owner-side:
    /// it needs no lease, takes none, and opens no page, so it neither reads web content nor marks
    /// the mission as having done so. Failure is silent - the ordinary lazy start still works.
    /// </summary>
    public async Task Prewarm(CancellationToken cancel = default)
    {
        try { await _browserGate.WaitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed || _browser is not null) return;
            // Warm only a browser this workspace actually uses. Measured 2026-09-20: waking a
            // workspace started Chrome every time, even for one that had never browsed, and that
            // was the largest single part of the ~6s wake - paid for a browser nobody opens. The
            // first browse still starts one lazily and leaves the mark, so the next wake warms it.
            if (_desktop.Folder is not { } home
                || !File.Exists(Path.Combine(home, WorkspaceBrowser.UsedMark))) return;
            WorkspaceBrowser? started = await WorkspaceBrowser
                .Start(_desktop, "about:blank", true, cancel, 0).ConfigureAwait(false);
            if (started is null)
            {
                _evidence.Note("browser", "warm-up", WorkspaceBrowser.StartupFailureReceipt(_desktop.Name));
                return;
            }
            if (_disposed || cancel.IsCancellationRequested) { started.Abandon(); return; }
            _browser = started;
            _evidence.Note("browser", "warm-up", "ready over " + started.Transport + "; browse will not pay the cold start");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _evidence.Note("browser", "warm-up", "did not come up: " + ex.GetType().Name);
        }
        finally { _browserGate.Release(); }
    }

    /// <summary>Every page open in the workspace browser, with the one on screen marked.</summary>
    public async Task<IReadOnlyList<BrowserTab>> Tabs(CancellationToken cancel = default)
    {
        if (_browser is not { Alive: true } browser) return [];
        IReadOnlyList<BrowserTab> tabs = await browser.Tabs(cancel).ConfigureAwait(false);
        _evidence.Note("tabs", tabs.Count + " open", tabs.FirstOrDefault(t => t.Active) is { } active
            ? "on screen: " + (active.Title.Length > 0 ? active.Title : active.Url) : "none on screen");
        return tabs;
    }

    /// <summary>
    /// Works in one tab from now on, or goes back to following the tab on screen when the number is
    /// zero. Selecting a tab also brings it to the front, so the picture and the page agree.
    /// </summary>
    public async Task<bool> SelectTab(int number, CancellationToken cancel = default)
    {
        if (_browser is not { Alive: true } browser) return false;
        if (!Allowed("tab", number == 0 ? "follow the tab on screen" : "tab " + number, out long ticket)) return false;
        using var input = InputScope(ticket, cancel);
        bool chosen;
        try { chosen = await browser.SelectTab(number, input.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
        Note("tab", number == 0 ? "follow the tab on screen" : "tab " + number,
            chosen ? "now on " + await Address(cancel).ConfigureAwait(false) : "no such tab");
        return chosen;
    }

    /// <summary>Opens a new tab in the workspace browser and works in it.</summary>
    public async Task<bool> NewTab(string url, CancellationToken cancel = default)
    {
        if (_browser is not { Alive: true } browser) return false;
        if (!Allowed("browser", "new tab " + url, out long ticket)) return false;
        using var input = InputScope(ticket, cancel);
        bool opened;
        try { opened = await browser.NewTab(url, input.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
        Note("browser", "new tab " + url, opened ? "opened, now at "
            + await Address(cancel).ConfigureAwait(false) : "the browser would not open a tab");
        return opened;
    }

    /// <summary>Whether the workspace browser is up and its connection still usable.</summary>
    public bool BrowserAlive => _browser is { Alive: true };

    /// <summary>The workspace browser's own process, whose windows are pages; 0 while there is none.</summary>
    internal int BrowserProcessId => _browser is { Alive: true } browser ? browser.ProcessId : 0;

    // What the owner did that an agent would otherwise find out by surprise - he took one of its
    // windows out onto his own desktop - handed to each connected agent with its next tool reply.
    readonly List<(long Number, string Text)> _notes = [];
    long _lastNote;

    internal void TellAgents(string text)
    {
        lock (_notes)
        {
            _notes.Add((++_lastNote, text));
            if (_notes.Count > 8) _notes.RemoveAt(0);
        }
    }

    /// <summary>Where the notes stand now: a connection starts here and hears only what comes after.</summary>
    internal long NoteMark { get { lock (_notes) return _lastNote; } }

    internal IReadOnlyList<string> NotesSince(ref long seen)
    {
        lock (_notes)
        {
            long after = seen;
            seen = _lastNote;
            return [.. _notes.Where(note => note.Number > after).Select(note => note.Text)];
        }
    }

    /// <summary>The browser itself, for the measurement probes only.</summary>
    internal WorkspaceBrowser? Browser => _browser;

    /// <summary>The page's real text and the things on it that can be acted on.</summary>
    public async Task<string> PageText(CancellationToken cancel = default)
    {
        if (_browser is null) return string.Empty;
        string text = await _browser.Read(20000, cancel).ConfigureAwait(false);
        string at = await Address(cancel).ConfigureAwait(false);
        _evidence.Note("page", at, text.Length + " characters read");
        return text;
    }

    public async Task<bool> PageGo(string url, CancellationToken cancel = default)
    {
        if (_browser is null || !Allowed("page", url, out long ticket)) return false;
        using var input = InputScope(ticket, cancel);
        bool went;
        try { went = await _browser.Go(url, input.Token).ConfigureAwait(false) && !input.IsCancellationRequested; }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
        Note("page", url, (went ? "loaded, now at " : "DID NOT LOAD, still at ")
            + await Address(cancel).ConfigureAwait(false));
        return went;
    }

    public async Task<bool> PageClick(string selector, CancellationToken cancel = default)
    {
        if (_browser is null || !Allowed("page", selector, out long ticket)) return false;
        using var input = InputScope(ticket, cancel);
        bool hit;
        try { hit = await _browser.Click(selector, input.Token).ConfigureAwait(false) && !input.IsCancellationRequested; }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
        Note("page", selector, (hit ? "clicked, now at " : "nothing matched, still at ")
            + await Address(cancel).ConfigureAwait(false));
        return hit;
    }

    public async Task<bool> PageType(string selector, string text, CancellationToken cancel = default)
    {
        if (_browser is null || !Allowed("page", selector, out long ticket)) return false;
        using var input = InputScope(ticket, cancel);
        bool typed;
        try { typed = await _browser.Type(selector, text, input.Token).ConfigureAwait(false) && !input.IsCancellationRequested; }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
        Note("page", selector, (typed ? "typed, on " : "nothing matched, on ")
            + await Address(cancel).ConfigureAwait(false));
        return typed;
    }

    /// <summary>
    /// Where the browser actually is. A click on a link, a redirect or a page navigating itself all
    /// change this without anybody asking for it, so every page line in the log carries the address
    /// the browser ended up at rather than the one it was told to go to.
    /// </summary>
    public async Task<string> Address(CancellationToken cancel = default)
    {
        if (_browser is null) return "no browser";
        string at = await _browser.Evaluate("location.href", cancel).ConfigureAwait(false);
        return at.Length == 0 ? "unknown" : at;
    }

    /// <summary>The browser's own window, so layer 2 can photograph the page it is driving.</summary>
    public nint BrowserWindow => _browser?.Window ?? 0;

    // --- the scheduler -----------------------------------------------------------------------

    /// <summary>
    /// Waits for the first real thing to happen. Nothing spins and no model is asked anything while
    /// this is outstanding: a timer is a timer, a process exit is a kernel wait, a window appearing
    /// is a Windows event hook on the workspace's own desktop, and the owner is a method call.
    /// Returns what woke it.
    /// </summary>
    public async Task<string> Until(TimeSpan? after = null, int processExits = 0,
        string? windowTitled = null, CancellationToken cancel = default)
    {
        var woke = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new CancellationTokenSource();
        using CancellationTokenRegistration byCaller = cancel.Register(() => woke.TrySetResult("cancelled"));
        lock (_gate) _waking = woke;

        Timer? timer = after is null ? null
            : new Timer(_ => woke.TrySetResult("the timer"), null, after.Value, Timeout.InfiniteTimeSpan);

        Process? watched = null;
        if (processExits != 0)
        {
            try
            {
                watched = Process.GetProcessById(processExits);
                watched.EnableRaisingEvents = true;      // RegisterWaitForSingleObject, not a loop
                watched.Exited += (_, _) => woke.TrySetResult("the process exited");
                if (watched.HasExited) woke.TrySetResult("the process exited");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                woke.TrySetResult("the process exited");
            }
        }

        WindowWatch? watching = windowTitled is null ? null
            : new WindowWatch(_desktop, windowTitled, found => woke.TrySetResult("a window appeared: " + found));

        try
        {
            string why = await woke.Task.ConfigureAwait(false);
            _evidence.Note("wait", Describe(after, processExits, windowTitled), "woke on " + why);
            return why;
        }
        finally
        {
            lock (_gate) _waking = null;
            timer?.Dispose();
            watched?.Dispose();
            watching?.Dispose();
            stop.Dispose();
        }
    }

    TaskCompletionSource<string>? _waking;

    /// <summary>The owner, or anything else host-side, ending a wait early.</summary>
    public void Wake(string why)
    {
        TaskCompletionSource<string>? waiting;
        lock (_gate) waiting = _waking;
        waiting?.TrySetResult(why);
    }

    static string Describe(TimeSpan? after, int process, string? window)
    {
        var what = new List<string>();
        if (after is not null) what.Add(after.Value.TotalSeconds + "s");
        if (process != 0) what.Add("pid " + process);
        if (window is not null) what.Add("window \"" + window + "\"");
        return what.Count == 0 ? "the owner" : string.Join(" or ", what);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _agentLease = 0;
            Became(Driver.Nobody);
            _inputStop.Cancel();
            _inputStop.Dispose();
        }
        Wake("the workspace closed");
        _commands.Dispose();
        _desktop.Pulled -= Pulled;
        _browser?.Dispose();
        _tree.Dispose();
        _evidence.Note("workspace", _desktop.Name, "control plane down");
        _evidence.Dispose();
    }
}

/// <summary>
/// A window appearing on the workspace desktop, as a Windows event rather than a scan. The hook has
/// to live on a thread bound to that desktop and that thread has to pump messages, which is why this
/// is a small object with a thread of its own rather than three lines somewhere.
/// </summary>
sealed class WindowWatch : IDisposable
{
    readonly Thread _thread;
    readonly Action<string> _found;
    readonly string _titled;
    readonly string _desktopName;
    // Held for the hook's whole life: a collected delegate is a callback into freed memory.
    Native.WinEventProc? _callback;
    int _threadId;
    bool _stopped;

    public WindowWatch(AgentDesktop desktop, string titled, Action<string> found)
    {
        _desktopName = desktop.Name;
        _titled = titled;
        _found = found;
        _thread = new Thread(Watch) { IsBackground = true, Name = "workspace-watch" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Watch()
    {
        nint desktop = Native.OpenDesktopW(_desktopName, 0, false, Native.GenericAll);
        if (desktop == 0 || !Native.SetThreadDesktop(desktop)) return;
        _threadId = Native.GetCurrentThreadId();

        _callback = (_, _, window, objectId, _, _, _) =>
        {
            if (objectId != Native.ObjIdWindow || _stopped) return;
            var title = new System.Text.StringBuilder(256);
            Native.GetWindowTextW(window, title, title.Capacity);
            string text = title.ToString();
            if (text.Contains(_titled, StringComparison.OrdinalIgnoreCase)) _found(text);
        };
        nint hook = Native.SetWinEventHook(Native.EventObjectCreate, Native.EventObjectShow, 0,
            _callback, 0, 0, Native.WineventOutOfContext);
        Hooked = hook != 0;
        if (hook == 0) { Native.CloseDesktop(desktop); return; }

        while (!_stopped && Native.GetMessageW(out Native.Msg message, 0, 0, 0) > 0)
            Native.DispatchMessageW(ref message);

        Native.UnhookWinEvent(hook);
        Native.CloseDesktop(desktop);
    }

    /// <summary>Whether Windows accepted the hook at all. Measured rather than assumed.</summary>
    public bool Hooked { get; private set; }

    public void Dispose()
    {
        _stopped = true;
        if (_threadId != 0) Native.PostThreadMessageW(_threadId, Native.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
