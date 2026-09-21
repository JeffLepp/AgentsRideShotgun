using System.IO;

namespace HiveMind.AgentWorkspaces;

/// <summary>Provider-neutral access. Connection identity is host-issued; client labels are unverified.</summary>
internal sealed class WorkspaceExternalAccess : IDisposable
{
    readonly Lock _gate = new();
    readonly WorkspaceControl _control;
    readonly string _id;
    readonly Dictionary<Guid, string> _clients = [];
    WorkspacePipeServer? _server;
    Guid? _driver;
    readonly List<ActiveUse> _uses = [];
    long _lastActive;
    bool _disposed;
    readonly Timer _letGo;

    /// <summary>How long a controller can sit idle before it lets go: another agent sharing the
    /// workspace can take over, and the corner window fades. Its next tool call takes it back.</summary>
    internal static long IdleHandoverMs { get; set; } = 30_000;

    /// <summary>How long a tool waits for its turn. The engine probe shortens it; nothing else changes it.</summary>
    internal static TimeSpan AcquireWait { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long an outstanding Use() is trusted before it is treated as abandoned rather than slow.
    /// The longest a single legitimate tool call can run is `run`'s own wait, capped at 1500 seconds
    /// (WorkspaceMcp.MaxWaitSeconds); comfortably past that, a lease still open is not a slow
    /// command, it is one whose Dispose never ran - an exception that unwound past a caller who
    /// skipped `using`, a cancelled task, a future call site nobody audited for it. Reclaiming it is
    /// what keeps the counter from pinning a workspace awake for the rest of the app's life. The
    /// engine probe shortens this to prove the reclaim without a 40-minute wait.
    /// </summary>
    internal static TimeSpan MaxUseAge { get; set; } = TimeSpan.FromMinutes(40);
    internal WorkspaceAccessPolicy Policy { get; private set; }
    internal WorkspaceHandoffs Handoffs { get; }
    internal bool HasDriver { get { lock (_gate) return _driver is not null; } }
    internal string Controller { get { lock (_gate) return _driver is { } id ? _clients.GetValueOrDefault(id, "Connected agent") : ""; } }
    /// <summary>Who is at the wheel, or who was last: an agent that let go between turns is still
    /// the one the corner and the hub name.</summary>
    internal string LastController { get { lock (_gate) return _driver is { } id ? _clients.GetValueOrDefault(id, "Connected agent") : _lastLabel; } }
    string _lastLabel = "";
    /// <summary>When an agent last took or used this workspace, on the Environment.TickCount64 clock.</summary>
    internal long LastActive { get { lock (_gate) return _lastActive; } }
    internal event Action? Changed;

    internal WorkspaceExternalAccess(string id, WorkspaceControl control)
    {
        _id = id;
        _control = control;
        Policy = WorkspaceAccessStore.Read(id);
        Handoffs = new(id, control.Folder) { Perform = Carry };
        _letGo = new Timer(_ => LetGoIfQuiet());
        Handoffs.Changed += Notify;
        Configure(Policy);
    }
    internal void Configure(WorkspaceAccessPolicy policy)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Policy = policy;
            if (!policy.Enabled)
            {
                WorkspacePipeServer? closing = _server;
                _server?.Dispose();
                _server = null;
                _clients.Clear();
                ReleaseDriver();
                WorkspaceAccessStore.Withdraw(_id, closing);
            }
            else if (_server is null)
            {
                var server = new WorkspacePipeServer(Attach, rejectClientProcess: _control.OwnsProcess);
                try { WorkspaceAccessStore.Publish(_id, server); _server = server; }
                catch { server.Dispose(); throw; }
            }
        }
        if (!policy.Enabled || !policy.DesktopRequests) Handoffs.CancelPending();
        Notify();
    }
    /// <summary>Puts this workspace's ticket back when something removed it while access is on.</summary>
    internal void KeepTicket()
    {
        lock (_gate)
        {
            if (_disposed || _server is null || !WorkspaceAccessStore.NeedsTicket(WorkspaceAccessStore.Connection(_id), _server)) return;
            try { WorkspaceAccessStore.Publish(_id, _server); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    /// <summary>One more agent in this workspace, through its own pipe or handed over by the router.</summary>
    internal WorkspacePipePeer Attach() => Attach("");

    /// <summary>One more agent, working in <paramref name="home"/>: its commands start there.</summary>
    internal WorkspacePipePeer Attach(string home)
    {
        Guid client = Guid.NewGuid();
        lock (_gate)
        {
            if (_disposed || !Policy.Enabled) throw new IOException("Agent access is off.");
            _clients.Add(client, "Connected agent");
        }
        var mcp = new WorkspaceMcp(_control, this, client, home);
        Notify();
        return new(mcp.Handle, () => { mcp.Dispose(); Disconnect(client); });
    }
    internal void Identify(Guid client, string label)
    {
        string clean = new(label.Where(c => !char.IsControl(c)).Take(60).ToArray());
        lock (_gate) if (_clients.ContainsKey(client)) _clients[client] = clean.Length == 0 ? "Connected agent" : clean;
        Notify();
    }

    /// <summary>Test-only: registers and acquires a client without a pipe, so the engine probe can
    /// call Use(client) directly and hold a lease past where it should have been disposed.</summary>
    internal Guid AcquireForTests()
    {
        Guid client = Guid.NewGuid();
        lock (_gate) _clients[client] = "probe";
        if (Acquire(client) is { } why) throw new InvalidOperationException(why);
        return client;
    }
    internal string? Acquire(Guid client)
    {
        lock (_gate)
        {
            ReclaimStale();
            if (_retired) return "This workspace just went to sleep. Try again; the next call wakes it.";
            if (_disposed || !Policy.Enabled || !_clients.ContainsKey(client)) return "Agent access is off. Ask the owner to enable it.";
            if (_uses.Count > 0 && _driver != client) return "The previous controller is stopping. Try again after it releases its current action.";
            if (_control.Driving == Driver.Owner) return "Paused: the owner has control. Do not retry until they give it back.";
            if (_driver is { } other && other != client)
            {
                // Agents sharing a workspace take turns. One that has gone quiet hands over rather
                // than holding the workspace until its session ends.
                if (Environment.TickCount64 - _lastActive < IdleHandoverMs)
                    return "Another agent is using this workspace. Try again in a minute.";
                _driver = null;
            }
            if (!_control.AgentTakes()) return "The owner has control.";
            _driver = client;
            _lastActive = Environment.TickCount64;
            _letGo.Change(IdleHandoverMs, Timeout.Infinite);
        }
        Notify();
        return null;
    }

    /// <summary>
    /// Closes this workspace to agents so it can sleep, but only if nobody holds or is using it
    /// right now; checked and closed under one lock, so no call can start in between. A call that
    /// arrives after is told to try again, and its retry wakes the workspace.
    /// </summary>
    internal bool Retire()
    {
        lock (_gate)
        {
            ReclaimStale();
            if (_disposed || _driver is not null || _uses.Count > 0
                || _control.Driving == Driver.Owner || _control.Commands.Activity.Running) return false;
            _retired = true;
            return true;
        }
    }
    bool _retired;

    /// <summary>
    /// Drops any Use() the caller never disposed of once it has run far longer than any real tool
    /// call could - see <see cref="MaxUseAge"/>. Must be called with <see cref="_gate"/> held.
    /// </summary>
    void ReclaimStale()
    {
        if (_uses.Count == 0) return;
        long cutoff = Environment.TickCount64 - (long)MaxUseAge.TotalMilliseconds;
        _uses.RemoveAll(use => use.Started < cutoff);
    }

    /// <summary>
    /// Agents rarely say they are finished. One that has gone quiet between turns lets go by itself,
    /// so the workspace stops counting as in use: the corner window fades and it can sleep.
    /// </summary>
    void LetGoIfQuiet()
    {
        lock (_gate)
        {
            ReclaimStale();
            if (_disposed || _driver is null) return;
            long quiet = Environment.TickCount64 - _lastActive;
            if (_uses.Count > 0 || quiet < IdleHandoverMs)
            {
                _letGo.Change(Math.Max(1000, IdleHandoverMs - quiet), Timeout.Infinite);
                return;
            }
            ReleaseDriver();
        }
        Notify();
    }

    /// <summary>
    /// Acquire, waiting out the owner or another agent for up to 45 seconds: under the 60-second
    /// tool timeout some clients use, so the agent hears why rather than a timeout.
    /// </summary>
    internal async Task<string?> AcquireWaiting(Guid client, CancellationToken cancel)
    {
        long until = Environment.TickCount64 + (long)AcquireWait.TotalMilliseconds;
        while (true)
        {
            string? why = Acquire(client);
            if (why is null) return null;
            lock (_gate) if (_disposed || !Policy.Enabled || !_clients.ContainsKey(client)) return why;
            if (Environment.TickCount64 > until) return why;
            await Task.Delay(750, cancel).ConfigureAwait(false);
        }
    }
    internal bool MayUse(Guid client)
    {
        lock (_gate) return !_disposed && Policy.Enabled && _driver == client && _control.Driving == Driver.Agent;
    }
    internal void Release(Guid client)
    {
        lock (_gate) if (_driver == client) ReleaseDriver();
        Notify();
    }
    internal UseLease? Use(Guid client)
    {
        lock (_gate)
        {
            if (!MayUse(client)) return null;
            var use = new ActiveUse(Environment.TickCount64);
            _uses.Add(use);
            _lastActive = use.Started;
            return new(_control.CurrentLease, () => ReleaseUse(use));
        }
    }
    /// <summary>
    /// Ends one Use(). Removing the exact token, rather than decrementing a count, is what makes
    /// this safe to call twice: if ReclaimStale already swept this one away and its real Dispose
    /// only unwinds later, there is nothing left to remove, so the late call is a no-op instead of
    /// undoing the reclaim or driving the count negative.
    /// </summary>
    void ReleaseUse(ActiveUse use)
    {
        lock (_gate)
        {
            if (!_uses.Remove(use)) return;
            _lastActive = Environment.TickCount64;
            if (!_disposed) _letGo.Change(IdleHandoverMs, Timeout.Infinite);
        }
    }
    /// <summary>One outstanding Use(), tracked by identity and birth tick so a lease nobody ever
    /// disposes can still be found and reclaimed - see <see cref="MaxUseAge"/>.</summary>
    sealed class ActiveUse(long started)
    { internal long Started => started; }
    internal sealed class UseLease(long lease, Action release) : IDisposable
    { internal long Lease => lease; public void Dispose() => release(); }
    internal WorkspaceHandoff RequestDesktop(Guid client, string kind, string target, string reason)
    {
        lock (_gate)
        {
            if (!MayUse(client) || !Policy.DesktopRequests)
                throw new InvalidOperationException("Desktop requests are disabled or this connection no longer has control.");
            // Programs are asked for through RequestProgram, which is the only thing that records
            // what to start. Naming one here would put a question to the owner that does nothing.
            if (kind is not ("file" or "url")) throw new ArgumentException("Request a document file or an HTTP(S) link.");
            return Handoffs.Request(kind, target, reason);
        }
    }

    /// <summary>
    /// Where a program opens follows who it is for. <paramref name="takeOver"/> false is the owner's
    /// own desktop, for something he asked for and will use himself; true moves a copy he already
    /// has open into this workspace, which is the only way a one-copy-per-session application can be
    /// driven in here. Both are one click for him and neither happens until he clicks.
    /// </summary>
    internal WorkspaceHandoff RequestProgram(Guid client, string program, string? arguments, string reason, bool takeOver)
    {
        lock (_gate)
        {
            if (!MayUse(client) || !Policy.DesktopRequests)
                throw new InvalidOperationException("Desktop requests are disabled or this connection no longer has control.");
            // The plain program name for both kinds: the corner window asks a takeover its own way
            // round now, so what it does no longer has to be spelled out inside the name.
            WorkspaceHandoff request = Handoffs.Request(takeOver ? "takeover" : "program", program, reason);
            lock (_programs)
            {
                foreach (string gone in _programs.Keys.Where(k => Handoffs.All.All(r => r.Id != k)).ToArray())
                    _programs.Remove(gone);
                _programs[request.Id] = (program, arguments);
            }
            return request;
        }
    }

    /// <summary>
    /// What each program request is actually for, kept here rather than on the request, which
    /// carries the sentence the owner reads and not a command line. Its own lock, held for the
    /// lookup only: Decide calls Carry while it holds the handoffs' lock, so taking this workspace's
    /// lock in there would order the two the opposite way round from RequestProgram.
    /// </summary>
    readonly Dictionary<string, (string Program, string? Arguments)> _programs = [];

    /// <summary>The owner clicked. Only Decide reaches this, and only for a request he approved.</summary>
    string? Carry(WorkspaceHandoff request)
    {
        (string Program, string? Arguments) what;
        lock (_programs) if (!_programs.TryGetValue(request.Id, out what)) return "That request is no longer on this workspace.";
        if (request.Kind == "takeover") return _control.TakeOver(what.Program, what.Arguments);
        try
        {
            // ShellExecute, exactly as if the owner had typed it into Start: his shell resolves the
            // name, so Store apps, file associations and App Paths all behave the way they do for him.
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(what.Program)
            { UseShellExecute = true, Arguments = what.Arguments ?? "" });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return "Windows would not start " + what.Program + " on the desktop: " + ex.Message;
        }
    }
    void ReleaseDriver()
    {
        if (_driver is null) return;
        _lastLabel = _clients.GetValueOrDefault(_driver.Value, _lastLabel);
        _driver = null;
        _control.AgentLetsGo();
    }
    void Disconnect(Guid client)
    {
        lock (_gate)
        {
            if (_driver == client) ReleaseDriver();
            _clients.Remove(client);
        }
        Notify();
    }

    internal object Status(Guid client) => new
    {
        workspace = _id, folder = _control.Folder, enabled = Policy.Enabled,
        fileAccess = "normal-windows-user", relativePaths = "workspace-folder", followsFileLinks = true,
        controller = Controller, hasControl = MayUse(client), ownerHasControl = _control.Driving == Driver.Owner,
        mainDesktop = Policy.DesktopRequests ? "ask-every-time" : "workspace-only", requests = Handoffs.All,
    };
    void Notify() => Changed?.Invoke();
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _letGo.Dispose();
            WorkspacePipeServer? closing = _server;
            _server?.Dispose();
            _server = null;
            ReleaseDriver();
            WorkspaceAccessStore.Withdraw(_id, closing);
        }
        Handoffs.CancelPending();
        Handoffs.Changed -= Notify;
    }
}
