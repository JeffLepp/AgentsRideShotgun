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
    bool _supervisor;
    int _activeUses;
    long _lastActive;
    bool _disposed;

    /// <summary>How long a controller can sit idle before another agent sharing the workspace takes over.</summary>
    const long IdleHandoverMs = 30_000;

    /// <summary>How long a tool waits for its turn. The engine probe shortens it; nothing else changes it.</summary>
    internal static TimeSpan AcquireWait { get; set; } = TimeSpan.FromSeconds(45);
    internal WorkspaceAccessPolicy Policy { get; private set; }
    internal WorkspaceHandoffs Handoffs { get; }
    internal bool HasDriver { get { lock (_gate) return _driver is not null; } }
    internal string Controller { get { lock (_gate) return _driver is { } id ? _clients.GetValueOrDefault(id, "Connected agent") : ""; } }
    internal event Action? Changed;

    internal WorkspaceExternalAccess(string id, WorkspaceControl control)
    {
        _id = id;
        _control = control;
        Policy = WorkspaceAccessStore.Read(id);
        Handoffs = new(id, control.Folder);
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
                _server?.Dispose();
                _server = null;
                _clients.Clear();
                ReleaseDriver();
                WorkspaceAccessStore.Withdraw(_id);
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
    /// <summary>One more agent in this workspace, through its own pipe or handed over by the router.</summary>
    internal WorkspacePipePeer Attach()
    {
        Guid client = Guid.NewGuid();
        lock (_gate)
        {
            if (_disposed || !Policy.Enabled) throw new IOException("Agent access is off.");
            _clients.Add(client, "Connected agent");
        }
        var mcp = new WorkspaceMcp(_control, this, client);
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
            _driver = client;
            _lastActive = Environment.TickCount64;
        }
        Notify();
        return null;
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
            return new(_control.CurrentLease, () => { lock (_gate) { _activeUses--; _lastActive = Environment.TickCount64; } });
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
        lock (_gate) { _clients.Remove(client); if (_driver == client) ReleaseDriver(); }
        Notify();
    }
    internal bool BeginSupervisor()
    {
        lock (_gate)
        {
            if (_disposed || _driver is not null || _supervisor || _activeUses > 0) return false;
            _supervisor = true;
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
            _server?.Dispose();
            _server = null;
            ReleaseDriver();
            WorkspaceAccessStore.Withdraw(_id);
        }
        Handoffs.CancelPending();
        Handoffs.Changed -= Notify;
    }
}
