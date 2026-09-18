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
    // Whose conversation the plane's "has read a page" flag belongs to right now, and the flags of
    // the others. The built-in boss is Guid.Empty, and the plane starts out holding its flag.
    Guid? _conversation = Guid.Empty;
    readonly Dictionary<Guid, bool> _read = [];
    bool _supervisor;
    int _activeUses;
    long _lastActive;
    bool _disposed;
    readonly Timer _letGo;

    /// <summary>How long a controller can sit idle before it lets go: another agent sharing the
    /// workspace can take over, and the corner window fades. Its next tool call takes it back.</summary>
    internal static long IdleHandoverMs { get; set; } = 30_000;

    /// <summary>How long a tool waits for its turn. The engine probe shortens it; nothing else changes it.</summary>
    internal static TimeSpan AcquireWait { get; set; } = TimeSpan.FromSeconds(45);
    internal WorkspaceAccessPolicy Policy { get; private set; }
    internal WorkspaceHandoffs Handoffs { get; }
    internal bool HasDriver { get { lock (_gate) return _driver is not null; } }
    internal string Controller { get { lock (_gate) return _driver is { } id ? _clients.GetValueOrDefault(id, "Connected agent") : ""; } }
    /// <summary>When an agent last took or used this workspace, on the Environment.TickCount64 clock.</summary>
    internal long LastActive { get { lock (_gate) return _lastActive; } }
    internal event Action? Changed;

    internal WorkspaceExternalAccess(string id, WorkspaceControl control)
    {
        _id = id;
        _control = control;
        Policy = WorkspaceAccessStore.Read(id);
        Handoffs = new(id, control.Folder);
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
            // The shared control plane includes the built-in boss, even with external access off.
            _control.ConfigureWebContentBlocking(policy.BlockProgramsAfterWebContent);
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
    internal string? Acquire(Guid client)
    {
        lock (_gate)
        {
            if (_disposed || !Policy.Enabled || !_clients.ContainsKey(client)) return "Agent access is off. Ask the owner to enable it.";
            if (_supervisor) return "The built-in boss is working. Wait for it to finish.";
            if (_activeUses > 0 && _driver != client) return "The previous controller is stopping. Try again after it releases its current action.";
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
            Converse(client);
            _driver = client;
            _lastActive = Environment.TickCount64;
            _letGo.Change(IdleHandoverMs, Timeout.Infinite);
        }
        Notify();
        return null;
    }

    /// <summary>
    /// Agents rarely say they are finished. One that has gone quiet between turns lets go by itself,
    /// so the workspace stops counting as in use: the corner window fades and it can sleep.
    /// </summary>
    void LetGoIfQuiet()
    {
        lock (_gate)
        {
            if (_disposed || _driver is null) return;
            long quiet = Environment.TickCount64 - _lastActive;
            if (_activeUses > 0 || quiet < IdleHandoverMs)
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
            _activeUses++;
            _lastActive = Environment.TickCount64;
            return new(_control.CurrentLease, () =>
            {
                lock (_gate)
                {
                    _activeUses--;
                    _lastActive = Environment.TickCount64;
                    if (!_disposed) _letGo.Change(IdleHandoverMs, Timeout.Infinite);
                }
            });
        }
    }
    internal sealed class UseLease(long lease, Action release) : IDisposable
    { internal long Lease => lease; public void Dispose() => release(); }
    internal WorkspaceHandoff RequestDesktop(Guid client, string kind, string target, string reason)
    {
        lock (_gate)
        {
            if (!MayUse(client) || !Policy.DesktopRequests)
                throw new InvalidOperationException("Desktop requests are disabled or this connection no longer has control.");
            return Handoffs.Request(kind, target, reason);
        }
    }
    void ReleaseDriver()
    {
        if (_driver is null) return;
        _driver = null;
        if (_control.Driving != Driver.Owner) _control.Release();
    }
    void Disconnect(Guid client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
            _read.Remove(client);
            if (_conversation == client) _conversation = null;
            if (_driver == client) ReleaseDriver();
        }
        Notify();
    }

    /// <summary>Gives the plane this one's own "has read a page" flag, keeping the last holder's.</summary>
    void Converse(Guid who)
    {
        if (_conversation == who) return;
        bool held = _control.SwapUntrusted(_read.GetValueOrDefault(who));
        if (_conversation is { } before) _read[before] = held;
        _conversation = who;
    }
    internal bool BeginSupervisor()
    {
        lock (_gate)
        {
            if (_disposed || _driver is not null || _supervisor || _activeUses > 0) return false;
            _supervisor = true;
            Converse(Guid.Empty);
            return true;
        }
    }
    internal void EndSupervisor() { lock (_gate) _supervisor = false; Notify(); }
    internal object Status(Guid client) => new
    {
        workspace = _id, folder = _control.Folder, enabled = Policy.Enabled,
        fileAccess = "normal-windows-user", relativePaths = "workspace-folder", followsFileLinks = true,
        controller = Controller, hasControl = MayUse(client), ownerHasControl = _control.Driving == Driver.Owner,
        hasReadWebContent = _control.ReadUntrustedContent,
        blockProgramsAfterWebContent = _control.BlockProgramsAfterWebContent,
        programsBlockedAfterWebContent = _control.ProgramsBlockedAfterWebContent,
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
