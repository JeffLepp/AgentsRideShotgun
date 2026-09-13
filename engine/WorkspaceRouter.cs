using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Which outside agents a workspace takes, kept on its record as one string. Empty: none, just the
/// owner and the boss. "*": any agent. "folder:C:\x": agents working in that folder or below it.
/// "agent:codex": one agent app, by the name its MCP client reports.
/// </summary>
public static class WorkspaceHome
{
    public const string Anyone = "*";
    const string FolderPrefix = "folder:";
    const string AgentPrefix = "agent:";

    public static string Folder(string path) => FolderPrefix + Normal(path);
    public static string Agent(string client) => AgentPrefix + Key(client);
    public static bool IsFolder(string rule) => rule.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The rule in a word or two, for the tile and the menus. Empty for "just me".</summary>
    public static string Label(string rule) =>
        rule == Anyone ? "Any agent"
        : IsFolder(rule) ? Leaf(rule[FolderPrefix.Length..])
        : rule.StartsWith(AgentPrefix, StringComparison.Ordinal) ? DisplayName(rule[AgentPrefix.Length..])
        : string.Empty;

    /// <summary>What an agent calls itself, as a person would say it.</summary>
    public static string DisplayName(string client) => Key(client) switch
    {
        "claudecode" => "Claude Code",
        "codex" => "Codex",
        _ when client.Trim() is { Length: > 0 } name => char.ToUpperInvariant(name[0]) + name[1..],
        _ => "Agent",
    };

    /// <summary>One spelling per agent app: Claude Code reports "claude-code", Codex "codex-mcp-client".</summary>
    internal static string Key(string client)
    {
        string key = new(client.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return key.Contains("claudecode") ? "claudecode" : key.Contains("codex") ? "codex" : key;
    }

    /// <summary>
    /// Changes who a workspace takes. "Just me" also closes outside access at once, so an agent
    /// already inside stops on its next action instead of finishing on borrowed time.
    /// </summary>
    public static void Set(string id, string rule)
    {
        if (WorkspaceStore.Update(id, workspace => workspace with { Agents = rule }) is null) return;
        WorkspaceAccessPolicy policy = WorkspaceAccessStore.Read(id) with { Enabled = rule.Length > 0 };
        WorkspaceExternalAccess? live = WorkspaceRuntime.Of(id)?.Access;
        // Restrictions land before the write, so a failed save can never leave access wider.
        if (!policy.Enabled) live?.Configure(policy);
        WorkspaceAccessStore.Write(id, policy);
        live?.Configure(policy);
    }

    /// <summary>Where one agent goes: an existing workspace, or the name and rule of a new one.</summary>
    internal sealed record Route(string? Existing, string Name, string Rule);

    /// <summary>
    /// The best fit for an agent working in <paramref name="cwd"/> that calls itself
    /// <paramref name="client"/>. A folder beats a named agent beats "any agent"; a deeper folder
    /// beats a shallower one; among shared workspaces an idle one beats a busy one. When nothing
    /// fits, a new workspace kept for the agent's project folder, so its next session and any other
    /// agent working there land in the same place.
    /// </summary>
    internal static Route Decide(IEnumerable<StoredWorkspace> all, string cwd, string client, Func<string, bool> busy)
    {
        string here = Normal(cwd);
        StoredWorkspace? best = null;
        int bestFit = 0;
        foreach (StoredWorkspace workspace in all)
        {
            string rule = workspace.Agents;
            int fit = IsFolder(rule) ? (Inside(here, rule[FolderPrefix.Length..]) ? 1000 + rule.Length : 0)
                : rule.StartsWith(AgentPrefix, StringComparison.Ordinal)
                    ? (client.Length > 0 && rule[AgentPrefix.Length..] == Key(client) ? 100 : 0)
                : rule == Anyone ? (busy(workspace.Id) ? 1 : 2)
                : 0;
            if (fit > bestFit) { best = workspace; bestFit = fit; }
        }
        if (best is not null) return new(best.Id, best.Name, best.Agents);
        string project = ProjectFolder(here);
        if (project.Length > 0) return new(null, Leaf(project), Folder(project));
        if (client.Length > 0) return new(null, DisplayName(client), Agent(client));
        return new(null, "Agents", Anyone);
    }

    /// <summary>
    /// The folder an agent's work is about, or empty when its folder says nothing: home, a drive,
    /// Desktop, Documents, Windows, Program Files or Deskweave's own folders. One workspace for every
    /// agent started from home would pile unrelated work into one place.
    /// </summary>
    internal static string ProjectFolder(string cwd)
    {
        string full = Normal(cwd);
        if (full.Length == 0 || !Directory.Exists(full)) return string.Empty;
        if (string.Equals(Path.GetPathRoot(full + "\\")?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        foreach (Environment.SpecialFolder vague in new[] { Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
            if (full.Equals(Normal(Environment.GetFolderPath(vague)), StringComparison.OrdinalIgnoreCase)) return string.Empty;
        foreach (string owned in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppContext.BaseDirectory, WorkspaceStore.Root, WorkspaceAccessStore.Root })
            if (Inside(full, Normal(owned))) return string.Empty;
        return full;
    }

    static string Normal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }

    static bool Inside(string path, string folder) => path.Length > 0 && folder.Length > 0
        && (path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));

    static string Leaf(string folder) => Path.GetFileName(folder) is { Length: > 0 } leaf ? leaf : folder;
}

/// <summary>
/// One MCP connection for every outside agent. An agent app is connected once, and Deskweave picks
/// the workspace the first time its agent uses one: never on connect, because every session of a
/// connected agent connects and most of them never open a window.
/// </summary>
internal sealed class WorkspaceRouter : IDisposable
{
    static WorkspaceRouter? _current;
    readonly WorkspacePipeServer _server;

    WorkspaceRouter()
    {
        // A program running inside a workspace cannot reach this, the same rule the per-workspace
        // pipes keep: web content that got a command run must not be able to drive a desktop.
        _server = new WorkspacePipeServer(() => new Session().Peer, maxClients: 32, rejectClientProcess: InsideAWorkspace);
        try { WorkspaceAccessStore.PublishRouter(_server); }
        catch { _server.Dispose(); throw; }
    }

    /// <summary>
    /// The most workspaces the router will have running at once, the owner's own included: one per
    /// 3 GB of memory, 2 to 10. Past that an agent is told to ask the owner, not handed a slow PC.
    /// </summary>
    internal static int MaxRunning => (int)Math.Clamp(WorkspaceLimits.PhysicalMemory() / (3UL << 30), 2, 10);

    internal static void Start()
    {
        if (_current is not null) return;
        try { _current = new WorkspaceRouter(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void Stop()
    {
        _current?.Dispose();
        _current = null;
    }

    public void Dispose()
    {
        WorkspaceAccessStore.WithdrawRouter();
        _server.Dispose();
    }

    static bool InsideAWorkspace(int processId)
    {
        bool Owned() => WorkspaceRuntime.Running.Any(runtime => runtime.Plane?.OwnsProcess(processId) == true);
        try
        {
            Dispatcher? ui = Application.Current?.Dispatcher;
            return ui is null || ui.CheckAccess() ? Owned() : ui.Invoke(Owned);
        }
        // Closing down: refuse rather than guess.
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException) { return true; }
    }

    /// <summary>
    /// Chooses, creates if need be, and starts an agent's workspace. Runs on the UI thread, where
    /// runtimes live, and never throws: a failure here is the agent's to read, not the app's to die of.
    /// </summary>
    static (WorkspaceRuntime? Runtime, string? Why) Place(string cwd, string client)
    {
        try
        {
            IReadOnlyList<StoredWorkspace> all = WorkspaceStore.All();
            WorkspaceHome.Route route = WorkspaceHome.Decide(all, cwd, client,
                id => WorkspaceRuntime.Of(id)?.Access?.HasDriver == true);
            bool starting = route.Existing is not { } existing || WorkspaceRuntime.Of(existing) is null;
            int running = WorkspaceRuntime.Running.Count;
            if (starting && running >= MaxRunning)
                return (null, $"Deskweave already has {running} workspaces running, the most this PC runs smoothly. "
                    + "Ask the owner to stop one, or to set one up for agents to share.");
            StoredWorkspace? workspace = route.Existing is { } id
                ? all.FirstOrDefault(w => w.Id == id)
                : WorkspaceStore.Update(WorkspaceStore.Create(route.Name).Id, created => created with { Agents = route.Rule });
            if (workspace is null) return (null, "That workspace was just deleted. Try again.");
            WorkspaceAccessPolicy policy = WorkspaceAccessStore.Read(workspace.Id);
            if (!policy.Enabled)
            {
                policy = policy with { Enabled = true };
                WorkspaceAccessStore.Write(workspace.Id, policy);
            }
            WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
            if (runtime.Access is { Policy.Enabled: false } access) access.Configure(policy);
            return (runtime, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "Deskweave could not open a workspace: " + ex.Message);
        }
    }

    /// <summary>
    /// One agent's connection. It answers the handshake and the tool list itself, and joins a
    /// workspace on the first tool call. After that everything goes straight to that workspace,
    /// until the workspace stops or stops taking agents; then the next call finds it a new one.
    /// </summary>
    sealed class Session
    {
        string _cwd = string.Empty;
        string _client = string.Empty;
        string? _hello;
        WorkspacePipePeer? _bound;
        WorkspaceRuntime? _runtime;
        WorkspaceExternalAccess? _access;

        internal WorkspacePipePeer Peer => new(Handle, Close);

        void Close()
        {
            _bound?.Closed();
            _bound = null;
        }

        async Task<string?> Handle(string body, CancellationToken cancel)
        {
            JsonElement call;
            try { using var document = JsonDocument.Parse(body); call = document.RootElement.Clone(); }
            catch (JsonException) { return Error(null, -32700, "Invalid JSON."); }
            if (call.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Expected one JSON-RPC request.");
            string method = Text(call, "method");
            object? id = call.TryGetProperty("id", out JsonElement raw)
                && raw.ValueKind is JsonValueKind.Number or JsonValueKind.String ? raw : null;
            JsonElement parameters = call.TryGetProperty("params", out JsonElement given)
                && given.ValueKind == JsonValueKind.Object ? given : default;

            switch (method)
            {
                case "deskweave/context":
                    if (parameters.ValueKind == JsonValueKind.Object) _cwd = Text(parameters, "cwd");
                    return null;
                case "initialize":
                    _hello = body;
                    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("clientInfo", out JsonElement info)
                        && info.ValueKind == JsonValueKind.Object) _client = Text(info, "name");
                    return Ok(id, new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "deskweave", version = "1" },
                        instructions = WorkspaceMcp.RouterInstructions,
                    });
                case "tools/list":
                    return Ok(id, new { tools = WorkspaceMcp.ExternalToolSchemas });
                case "ping":
                    return Ok(id, new { });
            }
            if (id is null) return null;                     // a notification: nothing to answer
            if (method != "tools/call") return Error(id, -32601, "no such method: " + method);

            string tool = parameters.ValueKind == JsonValueKind.Object ? Text(parameters, "name") : string.Empty;
            WorkspacePipePeer? peer = Current();
            if (peer is null && tool == "status")
                return Ok(id, Say("No workspace yet. Deskweave picks one the first time you use a workspace tool."));
            if (peer is null && tool == "release") return Ok(id, Say("Nothing to release."));
            if (peer is null)
            {
                (peer, string? why) = await Bind(cancel).ConfigureAwait(false);
                if (peer is null) return Ok(id, Fail(why ?? "Deskweave could not open a workspace."));
            }
            return await peer.Handle(body, cancel).ConfigureAwait(false);
        }

        /// <summary>The workspace this session is in, or null once it stopped or stopped taking agents.</summary>
        WorkspacePipePeer? Current()
        {
            if (_bound is null) return null;
            if (_runtime?.Access is { } access && ReferenceEquals(access, _access) && access.Policy.Enabled) return _bound;
            _bound.Closed();
            (_bound, _runtime, _access) = (null, null, null);
            return null;
        }

        async Task<(WorkspacePipePeer?, string?)> Bind(CancellationToken cancel)
        {
            Dispatcher? ui = Application.Current?.Dispatcher;
            if (ui is null || ui.HasShutdownStarted) return (null, "Deskweave is closing.");
            (WorkspaceRuntime? runtime, string? why) = await ui.InvokeAsync(() => Place(_cwd, _client), DispatcherPriority.Normal, cancel);
            if (runtime?.Access is not { } access) return (null, why ?? "The workspace stopped while it was starting. Try again.");
            WorkspacePipePeer peer;
            try { peer = access.Attach(); }
            catch (IOException ex) { return (null, ex.Message); }
            // The workspace hears this agent's own hello, so the owner sees its name on the tile.
            if (_hello is not null) await peer.Handle(_hello, cancel).ConfigureAwait(false);
            (_bound, _runtime, _access) = (peer, runtime, access);
            return (peer, null);
        }

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
    }

    static string Ok(object? id, object result) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    static string Error(object? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    static object Say(string text) => new { content = new object[] { new { type = "text", text } } };

    static object Fail(string text) => new { content = new object[] { new { type = "text", text } }, isError = true };
}
