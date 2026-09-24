using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// One control an application published, as an agent sees it. The rectangle is always here, so
/// every element is also a point: nothing the tree can name is unreachable by the click and type
/// path underneath it.
/// </summary>
public sealed record WorkspaceElement(int Id, string Type, string Name, string AutomationId,
    int X, int Y, int Width, int Height, bool Enabled, string Does, int Depth)
{
    public int CentreX => X + Width / 2;
    public int CentreY => Y + Height / 2;

    public override string ToString() =>
        $"{Id} {Type} \"{Name}\" ({X},{Y} {Width}x{Height}){(Enabled ? "" : " disabled")}" +
        (Does.Length == 0 ? "" : " " + Does);
}

/// <summary>
/// Layer 1 of the control plane: the controls an application publishes on the separate workspace
/// desktop. ARS stays on the owner's desktop while the launched app uses the owner's normal
/// token and file permissions.
///
/// This class is only ever layer 1. It never falls back to pixels: composing layer 1 with the frame
/// and message path is <see cref="WorkspaceControl"/>'s job, so that the fallback is visible in one
/// place instead of hidden in six.
/// </summary>
public sealed class WorkspaceTree : IDisposable
{
    // UI Automation is COM and every user32 call under it is desktop-affine, so one MTA thread owns
    // the whole layer. It is deliberately not AgentDesktop's pump: a tree read of a busy window took
    // 1.3 seconds when measured, and the live view is 2 frames a second on that pump.
    readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new(1);
    internal const int OperationMilliseconds = 3000;

    /// <summary>Shrinks the read budget below <see cref="OperationMilliseconds"/> so the partial-read
    /// path can be reached on purpose. Zero means unset. Only the engine probe ever sets this.</summary>
    internal static int BudgetMillisecondsForTests;

    /// <summary>An artificial pause before each node in <see cref="ReadOnPump"/>, so a small fixture
    /// tree can still blow a short test budget deterministically instead of racing a real slow window.
    /// Only the engine probe ever sets this.</summary>
    internal static TimeSpan SlowNodeForTests = TimeSpan.Zero;

    long _operationDeadline;
    readonly Thread _thread;
    readonly Dictionary<int, Entry> _known = [];
    readonly string _desktopName;
    nint _desktop;
    int _nextId = 1;
    int _reading;
    bool _disposed;

    sealed record Entry(AutomationElement Element, WorkspaceElement Info, int Reading, int Process,
        int[] RuntimeId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenDesktopW(string desktop, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desktop);

    public WorkspaceTree(string desktopName)
    {
        _desktopName = desktopName;
        _thread = new Thread(Pump) { IsBackground = true, Name = $"workspace-tree-{desktopName}" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!Run(Bind))
            throw new InvalidOperationException(
                $"Could not bind a UI Automation client to {desktopName} " +
                $"(error {Marshal.GetLastWin32Error()}).");
    }

    bool Bind()
    {
        _desktop = OpenDesktopW(_desktopName, 0, false, 0x10000000);
        // Measured: an unbound client still reads most of a workspace window, and silently drops
        // elements while doing it - 27 with 5 failures against 27 clean. So binding is not optional.
        return _desktop != 0 && SetThreadDesktop(_desktop);
    }

    static readonly AutomationProperty[] Properties =
    [
        AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
        AutomationElement.AutomationIdProperty, AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty
    ];

    static readonly (AutomationPattern Pattern, string Verb)[] Verbs =
    [
        (InvokePattern.Pattern, "press"), (TogglePattern.Pattern, "toggle"),
        (ExpandCollapsePattern.Pattern, "expand"), (SelectionItemPattern.Pattern, "select"),
        (ValuePattern.Pattern, "write"), (TextPattern.Pattern, "read")
    ];

    /// <summary>
    /// What one window publishes. One window, never the whole desktop: measured, every
    /// tree on a four-application desktop together costs more than a picture of that desktop, and a
    /// single window's tree costs a fifth of one.
    /// </summary>
    /// <param name="everything">
    /// Include the unnamed structural nodes an agent cannot act on. Off by default.
    /// </param>
    /// <param name="nameContains">
    /// Keep only elements whose name contains this text, case-insensitive. The walk still visits
    /// every node - a plain parent can still have a matching child - but the caller gets back only
    /// what it asked for, which scopes a large window down to the one control it already knows the
    /// name of.
    /// </param>
    public IReadOnlyList<WorkspaceElement> Read(nint window, bool everything = false, string? nameContains = null)
        => Read(window, everything, nameContains, out _);

    /// <summary>
    /// Like <see cref="Read(nint, bool, string?)"/>, but also says whether the read stopped at its
    /// time budget with more of the tree left unread. A busy window used to mean CheckDeadline threw
    /// and the agent got nothing at all. What was collected before the clock ran
    /// out is worth more than an exception, every time.
    /// </summary>
    public IReadOnlyList<WorkspaceElement> Read(nint window, bool everything, string? nameContains, out bool partial)
    {
        (IReadOnlyList<WorkspaceElement> found, bool stopped) = Run(() => ReadOnPump(window, everything, nameContains));
        partial = stopped;
        return found;
    }

    (IReadOnlyList<WorkspaceElement>, bool) ReadOnPump(nint window, bool everything, string? nameContains)
    {
        var found = new List<WorkspaceElement>();
        int reading = ++_reading;
        Forget(reading);

        var request = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (AutomationProperty property in Properties) request.Add(property);
        foreach ((AutomationPattern pattern, string _) in Verbs) request.Add(pattern);

        var queue = new Queue<(AutomationElement Element, int Depth)>();
        try { queue.Enqueue((AutomationElement.FromHandle(window), 0)); }
        catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException) { return (found, false); }

        TreeWalker walker = TreeWalker.ControlViewWalker;
        bool partial = false;
        // 4000 is a ceiling against a runaway tree, not a measured limit: the busiest window seen so
        // far published 111 elements.
        while (queue.Count > 0 && found.Count < 4000)
        {
            if (DeadlineReached()) { partial = true; break; }
            // Only the engine probe ever sets this, to reach this path on a small fixture tree
            // deterministically rather than waiting on a real slow one and hoping the timing lines up.
            if (SlowNodeForTests > TimeSpan.Zero) Thread.Sleep(SlowNodeForTests);
            (AutomationElement element, int depth) = queue.Dequeue();
            try
            {
                WorkspaceElement? info = Describe(element, depth, reading, everything);
                if (info is not null
                    && (nameContains is null || info.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
                    found.Add(info);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { }

            try
            {
                for (AutomationElement? child = walker.GetFirstChild(element, request); child is not null;
                     child = walker.GetNextSibling(child, request))
                {
                    if (DeadlineReached()) { partial = true; break; }
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { }
        }
        return ((IReadOnlyList<WorkspaceElement>)found, partial);
    }

    WorkspaceElement? Describe(AutomationElement element, int depth, int reading, bool everything)
    {
        // Cached where the walker filled the cache, live for the one root element that has no cache.
        AutomationElement.AutomationElementInformation now =
            depth == 0 ? element.Current : element.Cached;
        System.Windows.Rect box = now.BoundingRectangle;
        bool placed = !box.IsEmpty && box.Width > 0 && box.Height > 0 && !now.IsOffscreen;

        var does = new List<string>();
        foreach ((AutomationPattern pattern, string verb) in Verbs)
            if (element.TryGetCachedPattern(pattern, out object? _) ||
                (depth == 0 && element.TryGetCurrentPattern(pattern, out object? _)))
                does.Add(verb);

        string name = now.Name ?? string.Empty;
        if (!everything && (!placed || (name.Length == 0 && does.Count == 0))) return null;

        var info = new WorkspaceElement(_nextId++, now.ControlType?.ProgrammaticName?.Split('.')[^1] ?? "Custom",
            name, now.AutomationId ?? string.Empty, (int)box.X, (int)box.Y, (int)box.Width, (int)box.Height,
            now.IsEnabled, string.Join('/', does), depth);
        _known[info.Id] = new Entry(element, info, reading, element.Current.ProcessId, element.GetRuntimeId());
        return info;
    }

    /// <summary>A cached coordinate is never permission to act on a control that has changed.</summary>
    public string? Validate(int id) => Run(() =>
        _known.TryGetValue(id, out Entry? entry) ? Changed(entry) : "Unknown control. Read controls again.");

    static string? Changed(Entry entry)
    {
        try
        {
            var now = entry.Element.Current;
            WorkspaceElement was = entry.Info;
            var box = now.BoundingRectangle;
            if (now.ProcessId != entry.Process || !entry.Element.GetRuntimeId().SequenceEqual(entry.RuntimeId)
                || (now.Name ?? string.Empty) != was.Name || (now.AutomationId ?? string.Empty) != was.AutomationId
                || now.ControlType?.ProgrammaticName?.Split('.')[^1] != was.Type)
                return "The control's identity changed. Read controls again.";
            if (!now.IsEnabled || now.IsOffscreen || box.IsEmpty || box.Width <= 0 || box.Height <= 0)
                return "The control is disabled or no longer visible. Read controls again.";
            if ((int)box.X != was.X || (int)box.Y != was.Y
                || (int)box.Width != was.Width || (int)box.Height != was.Height)
                return "The control moved or resized. Read controls again.";
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return "The control is no longer available. Read controls again.";
        }
    }

    /// <summary>Where an element is on the workspace's screen, so it can be clicked instead.</summary>
    public WorkspaceElement? Known(int id) => Run(() => _known.TryGetValue(id, out Entry? e) ? e.Info : null);

    /// <summary>
    /// Where a numbered control is right now, for a pointer action aimed at it: its live centre,
    /// whether WPF draws it, or why it cannot be aimed at. Unlike <see cref="Validate"/>, a control
    /// that has moved is fine - the number names the control, not the place it was - but one that
    /// became something else, went off screen, or sits under another program's window is refused,
    /// because a click at its centre would land on whatever is there instead.
    /// </summary>
    internal (int X, int Y, bool Wpf, string? Why) Where(int id) => Run<(int, int, bool, string?)>(() =>
    {
        if (!_known.TryGetValue(id, out Entry? entry))
            return (0, 0, false, $"Control {id} is not known. Number the controls again with controls or marks.");
        try
        {
            var now = entry.Element.Current;
            WorkspaceElement was = entry.Info;
            if (now.ProcessId != entry.Process || !entry.Element.GetRuntimeId().SequenceEqual(entry.RuntimeId)
                || (now.Name ?? string.Empty) != was.Name
                || now.ControlType?.ProgrammaticName?.Split('.')[^1] != was.Type)
                return (0, 0, false, $"Control {id} changed. Number the controls again.");
            System.Windows.Rect box = now.BoundingRectangle;
            if (now.IsOffscreen || box.IsEmpty || box.Width <= 0 || box.Height <= 0)
                return (0, 0, false, $"Control {id} is not on the screen now; it may be scrolled away or hidden.");
            int x = (int)(box.X + box.Width / 2), y = (int)(box.Y + box.Height / 2);
            // This thread is bound to the workspace desktop, so this asks that desktop what is on top.
            nint top = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
            Native.GetWindowThreadProcessId(top, out int owner);
            if (top == 0 || owner != entry.Process)
                return (0, 0, false, $"Control {id} is covered by another window. Bring its window to the front first.");
            return (x, y, now.FrameworkId == "WPF", null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return (0, 0, false, $"Control {id} is gone. Number the controls again.");
        }
    });

    /// <summary>
    /// Presses an element through its supported provider. TryPress distinguishes an unsupported
    /// route (eligible for a validated point press) from a refused or possibly dispatched action.
    /// </summary>
    public bool Press(int id) => TryPress(id, () => true) == TreeAction.Applied;

    internal TreeAction TryPress(int id, Func<bool> mayAct) => Run(() => Act(id, mayAct, element =>
    {
        // The Win32 UIA proxy for a standard dialog Button can publish Invoke while its
        // implementation fails on a non-input desktop (measured: Win32Exception, "Hot key is
        // already registered"). Choose the existing validated point route BEFORE invoking.
        // Never reinterpret a provider exception as permission to replay a possible action.
        // The same holds for a task dialog's buttons - Notepad's "Save changes?" prompt, every
        // TaskDialog - which are comctl32's CCPushButton under a DirectUI provider (measured:
        // Invoke refused, the prompt stayed up, and an agent could not answer it).
        var now = element.Current;
        if (now.ControlType == ControlType.Button && now.NativeWindowHandle != 0
            && (now.FrameworkId == "Win32" && now.ClassName == "Button"
                || now.FrameworkId == "DirectUI" && now.ClassName == "CCPushButton"))
            return false;
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
        { CheckAction(mayAct); ((InvokePattern)invoke).Invoke(); return true; }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
        { CheckAction(mayAct); ((TogglePattern)toggle).Toggle(); return true; }
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
        {
            var it = (ExpandCollapsePattern)expand;
            bool expanded = it.Current.ExpandCollapseState == ExpandCollapseState.Expanded;
            CheckAction(mayAct);
            if (expanded) it.Collapse(); else it.Expand();
            return true;
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? select))
        { CheckAction(mayAct); ((SelectionItemPattern)select).Select(); return true; }
        return false;
    }));

    /// <summary>
    /// Puts text into an element. Measured: Notepad's document publishes Text and Scroll
    /// and no ValuePattern, so this returns false for the commonest editor on Windows and the caller
    /// types the text through layer 2 instead. That is the normal case, not the failure case.
    /// </summary>
    public bool Write(int id, string text) => TryWrite(id, text, () => true) == TreeAction.Applied;

    internal TreeAction TryWrite(int id, string text, Func<bool> mayAct) => Run(() => Act(id, mayAct, element =>
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value)) return false;
        var writable = (ValuePattern)value;
        if (writable.Current.IsReadOnly) throw new InvalidOperationException("The control is read-only.");
        CheckAction(mayAct);
        writable.SetValue(text);
        return true;
    }));

    /// <summary>Whatever text an element holds, or its name when it holds none.</summary>
    public string Text(int id) => Run(() =>
    {
        if (!_known.TryGetValue(id, out Entry? entry)) return string.Empty;
        try
        {
            if (entry.Element.TryGetCurrentPattern(TextPattern.Pattern, out object? text))
                return ((TextPattern)text).DocumentRange.GetText(-1);
            if (entry.Element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
                return ((ValuePattern)value).Current.Value ?? string.Empty;
            return entry.Element.Current.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    });

    internal (bool Applied, string Result) ReadCurrent(int id, Func<bool> mayRead) => Run(() =>
    {
        if (!_known.TryGetValue(id, out Entry? entry) || Changed(entry) is not null || !mayRead())
            return (false, "The control changed before it could be read.");
        try
        {
            if (entry.Element.TryGetCurrentPattern(TextPattern.Pattern, out object? text))
                return (true, ((TextPattern)text).DocumentRange.GetText(WorkspaceBatch.MaxText));
            if (entry.Element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value))
                return (true, ((ValuePattern)value).Current.Value ?? string.Empty);
            return (true, entry.Element.Current.Name ?? string.Empty);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        { return (false, "The control could not be read."); }
    });

    /// <summary>Puts the caret in an element, so typing through layer 2 lands in the right place.</summary>
    public bool Focus(int id) => Focus(id, () => true);

    internal bool Focus(int id, Func<bool> mayAct) => Run(() =>
        Act(id, mayAct, element => { element.SetFocus(); return true; }) == TreeAction.Applied);

    /// <summary>The window handle an element belongs to, for a screenshot of exactly that thing.</summary>
    public nint WindowOf(int id) => Run(() =>
    {
        if (!_known.TryGetValue(id, out Entry? entry)) return (nint)0;
        try { return new nint(entry.Element.Current.NativeWindowHandle); }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException) { return (nint)0; }
    });

    internal enum TreeAction { Applied, Unsupported, Refused }

    TreeAction Act(int id, Func<bool> mayAct, Func<AutomationElement, bool> what)
    {
        if (!_known.TryGetValue(id, out Entry? entry) || Changed(entry) is not null || !mayAct())
            return TreeAction.Refused;
        CheckDeadline();
        try { return what(entry.Element) ? TreeAction.Applied : TreeAction.Unsupported; }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // The provider may have acted before throwing. Never retry as a coordinate click.
            return TreeAction.Refused;
        }
    }

    bool DeadlineReached() => Environment.TickCount64 >= _operationDeadline;

    /// <summary>
    /// Used by a single action - press, write, focus - where there is no partial result to fall back
    /// to, so a blown budget is still a real failure. Read has its own, cooperative check: see
    /// <see cref="ReadOnPump"/>.
    /// </summary>
    void CheckDeadline()
    {
        if (DeadlineReached())
            throw new TimeoutException("UI Automation exceeded its 3-second budget. Use computer/look to inspect; an in-flight provider action may still finish. Do not replay automatically.");
    }

    void CheckAction(Func<bool> mayAct)
    {
        CheckDeadline();
        if (!mayAct()) throw new InvalidOperationException("The original input lease was revoked.");
    }

    /// <summary>
    /// Ids are never reused, so an id from an old read fails rather than acting on something else.
    /// Four readings back is kept because an agent reads, thinks, then acts.
    /// </summary>
    void Forget(int reading)
    {
        if (_known.Count < 4000) return;
        foreach (int id in _known.Where(e => e.Value.Reading < reading - 4).Select(e => e.Key).ToList())
            _known.Remove(id);
    }

    void Pump()
    {
        foreach (Action job in _work.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception) { /* the caller's TaskCompletionSource carries it */ }
        }
    }

    T Run<T>(Func<T> job)
    {
        if (Thread.CurrentThread == _thread) return job();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int budgetMs = BudgetMillisecondsForTests > 0 ? BudgetMillisecondsForTests : OperationMilliseconds;
        // The walk stops at its own deadline and hands back what it collected, but it can only do
        // that if it is given less time than the caller waits: with one budget for both, the walk
        // returned its partial list at the same instant this thread gave up on it, and the caller
        // lost that race and threw the list away. Measured: a read of a busy window
        // failed with the exception below while the elements it had were sitting in a list nobody
        // read. The remaining wait is what it always was - protection against a provider call that
        // has wedged the COM thread, where there is nothing to hand back.
        long handback = Math.Min(250, budgetMs / 4);
        long deadline = Environment.TickCount64 + budgetMs - handback;
        try
        {
            if (!_work.TryAdd(() =>
            {
                try { _operationDeadline = deadline; CheckDeadline(); done.SetResult(job()); }
                catch (Exception ex) { done.SetException(ex); }
            })) throw new TimeoutException("UI Automation is busy. Use computer/look; no additional UIA work was queued.");
        }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(WorkspaceTree)); }
        try { return done.Task.WaitAsync(TimeSpan.FromMilliseconds(budgetMs)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            // Never replace a stuck COM thread with an unlimited stream of new threads. Queued
            // operations expire before starting, and in-flight provider calls are not retried.
            throw new TimeoutException("UI Automation exceeded its 3-second budget. Use computer/look to inspect; an in-flight provider action may still finish. Do not replay automatically.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(2))) Cleanup();
        else _ = Task.Run(() => { _thread.Join(); Cleanup(); });
        // A provider cannot be forcibly cancelled. Keep its desktop alive until its thread exits.
        void Cleanup() { if (_desktop != 0) CloseDesktop(_desktop); _work.Dispose(); }
    }
}
