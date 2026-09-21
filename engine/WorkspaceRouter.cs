using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Which outside agents a workspace takes, kept on its record as one string. Empty: none, just the
/// owner. "*": any agent. "folder:C:\x": agents working in that folder or below it.
/// "agent:codex": one agent app, by the name its MCP client reports.
/// </summary>
public static class WorkspaceHome
{
    public const string Anyone = "*";
    public const string Scratch = "scratch";
    const string FolderPrefix = "folder:";
    const string AgentPrefix = "agent:";

    public static string Folder(string path) => FolderPrefix + Normal(path);
    public static string Agent(string client) => AgentPrefix + Key(client);
    public static bool IsFolder(string rule) => rule.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The rule in a word or two, for the tile and the menus. Empty for "just me".</summary>
    public static string Label(string rule) =>
        rule == Scratch ? "Scratch"
        : rule == Anyone ? "Any agent"
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
    /// Automatic placement is stable across agent apps and activity: one workspace per project,
    /// and one Scratch outside projects. Legacy agent/shared rules must not swallow new projects.
    /// An empty rule is always private, even on a record named Scratch.
    /// </summary>
    internal static Route Decide(IEnumerable<StoredWorkspace> all, string cwd, string client, Func<string, bool> busy)
    {
        string project = ProjectFolder(cwd);
        string rule = project.Length > 0 ? Folder(project) : Scratch;
        StoredWorkspace? existing = all
            .Where(w => string.Equals(w.Agents, rule, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Created).ThenBy(w => w.Id, StringComparer.Ordinal).FirstOrDefault();
        return existing is not null ? new(existing.Id, existing.Name, existing.Agents)
            : new(null, project.Length > 0 ? Leaf(project) : "Scratch", rule);
    }

    /// <summary>Creates only the saved Scratch record, never a desktop, browser or agent.</summary>
    internal static StoredWorkspace EnsureScratch()
    {
        StoredWorkspace? existing = WorkspaceStore.All().FirstOrDefault(w => w.Agents == Scratch);
        if (existing is not null) return existing;
        StoredWorkspace created = WorkspaceStore.Create("Scratch");
        return WorkspaceStore.Update(created.Id, w => w with { Agents = Scratch }) ?? created;
    }

    /// <summary>
    /// The nearest repository (including a worktree's .git file), else the nearest project
    /// manifest. A repository wins over its package subfolders, so monorepos stay together;
    /// a nested repository is its own project. Broad personal/system roots are never projects.
    /// Bounded: this decides on the UI thread while a connected agent waits for its first tool
    /// call, and on a disconnected mapped drive or an unreachable UNC host every folder test below
    /// sits out the SMB timeout - 20 to 45 seconds, per level of the walk, with nothing to cancel.
    /// Past the bound the answer is no project, which is already what an unreadable parent or an
    /// owned folder gets, and <see cref="Decide"/> reads that as Scratch.
    /// </summary>
    internal static string ProjectFolder(string cwd)
    {
        string found = string.Empty;
        ExceptionDispatchInfo? failed = null;
        long until = Environment.TickCount64 + (long)ProjectBound.TotalMilliseconds;
        // One probe at a time, and the bound covers the queue as well as the walk. A wedged
        // filesystem call cannot be cancelled, so the one thread left behind is waited out where a
        // thread per call would pile up; a healthy probe holds this for microseconds.
        if (!_probing.Wait(ProjectBound)) return string.Empty;
        var probe = new Thread(() =>
        {
            try { found = Identify(cwd); }
            catch (Exception ex) { failed = ExceptionDispatchInfo.Capture(ex); }
            finally { _probing.Release(); }
        })
        { IsBackground = true, Name = "Deskweave project folder" };
        probe.Start();
        if (!probe.Join((int)Math.Max(0, until - Environment.TickCount64))) return string.Empty;
        // Whatever it could not read is still the caller's to handle, on the caller's thread.
        failed?.Throw();
        return found;
    }

    /// <summary>How long any caller waits for a folder to identify itself before settling for Scratch.</summary>
    static readonly TimeSpan ProjectBound = TimeSpan.FromSeconds(2);

    static readonly SemaphoreSlim _probing = new(1, 1);

    /// <summary>The walk itself, off the caller's thread so a stalled drive cannot hold it.</summary>
    static string Identify(string cwd)
    {
        string full = Normal(cwd);
        if (full.Length == 0 || !Directory.Exists(full)) return string.Empty;
        foreach (string owned in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppContext.BaseDirectory, WorkspaceStore.Root, WorkspaceAccessStore.Root })
            if (Inside(full, Normal(owned))) return string.Empty;
        string? manifest = null;
        try
        {
            for (DirectoryInfo? folder = new(full); folder is not null && !BroadRoot(folder.FullName); folder = folder.Parent)
            {
                string path = folder.FullName;
                if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))
                    || Directory.Exists(Path.Combine(path, ".hg")) || Directory.Exists(Path.Combine(path, ".svn")))
                    return Normal(path);
                if (manifest is null && HasProjectManifest(path)) manifest = Normal(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A missing or unreadable parent is not a reason to route into a guessed project.
            return string.Empty;
        }
        return manifest ?? string.Empty;
    }

    static bool BroadRoot(string path) => string.Equals(Normal(path), Normal(Path.GetPathRoot(path) ?? ""), StringComparison.OrdinalIgnoreCase)
        || new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments }.Any(f => string.Equals(Normal(path), Normal(Environment.GetFolderPath(f)), StringComparison.OrdinalIgnoreCase));

    static readonly string[] ManifestNames = new[] { "package.json", "pyproject.toml", "Cargo.toml", "go.mod",
        "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "composer.json", "Gemfile" };

    static readonly string[] ManifestExtensions = new[] { ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj" };

    // Ask Windows for those five extensions instead of listing the folder and sorting it out here.
    // The walk runs this on every parent level, and reading every name costs the whole listing in a
    // downloads-sized folder and a round trip per level on a network path. A three-letter pattern
    // matches more than it looks like ("*.sln" also finds .slnx, and old 8.3 aliases), so the name
    // that came back is checked again and still only these five count.
    static bool HasProjectManifest(string path) => ManifestNames.Any(name => File.Exists(Path.Combine(path, name)))
        || ManifestExtensions.Any(extension =>
            Directory.EnumerateFiles(path, "*" + extension, SearchOption.TopDirectoryOnly)
                .Any(file => string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase)));

    static string Normal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
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
    readonly Lock _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _retry;
    bool _disposed;

    /// <summary>How soon a ticket that could not be written is tried again. Nothing runs otherwise.</summary>
    internal static TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    WorkspaceRouter()
    {
        // A program running inside a workspace cannot reach this, the same rule the per-workspace
        // pipes keep: web content that got a command run must not be able to drive a desktop.
        _server = new WorkspacePipeServer(() => new Session().Peer, maxClients: 32, rejectClientProcess: InsideAWorkspace);
        Keep();
    }

    /// <summary>The pipe this router serves, for the probes.</summary>
    internal static string? Pipe => _current?._server.Name;

    /// <summary>
    /// Keeps the router's ticket, and each running workspace's, naming a pipe that answers for as long
    /// as the app is open. Runs at start and whenever something in the ticket folder changes; only
    /// while a write keeps failing does it try again on a clock. A ticket that went missing while
    /// the app was open used to stay missing, and every agent was told Deskweave was not running.
    /// </summary>
    void Keep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (WorkspaceAccessStore.NeedsTicket(WorkspaceAccessStore.RouterTicket, _server))
                    WorkspaceAccessStore.PublishRouter(_server);
                _watcher ??= Watch();
                _retry?.Dispose();
                _retry = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher?.Dispose();
                _watcher = null;
                _retry ??= new Timer(_ => Keep(), null, RetryEvery, RetryEvery);
            }
        }
        Dispatcher? ui = Application.Current?.Dispatcher;
        if (ui is null) KeepWorkspaceTickets();
        else if (!ui.HasShutdownStarted) ui.BeginInvoke(KeepWorkspaceTickets);
    }

    static void KeepWorkspaceTickets()
    {
        foreach (WorkspaceRuntime runtime in WorkspaceRuntime.Running) runtime.Access?.KeepTicket();
    }

    FileSystemWatcher Watch()
    {
        var watcher = new FileSystemWatcher(WorkspaceAccessStore.Root, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        watcher.Deleted += (_, _) => Keep();
        watcher.Renamed += (_, _) => Keep();
        watcher.Changed += (_, _) => Keep();
        // The folder itself went, or Windows dropped events: look again with a new watcher.
        watcher.Error += (_, _) =>
        {
            lock (_gate) { if (ReferenceEquals(_watcher, watcher)) _watcher = null; }
            watcher.Dispose();
            Keep();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>
    /// The most workspaces the router will have running at once, the owner's own included: one per
    /// 3 GB of memory, 1 to 10. Past that the quietest one sleeps to make room, and when every one is
    /// in use the agent waits its turn rather than being handed a slow PC.
    /// </summary>
    internal static int MaxRunning { get; set; } = (int)Math.Clamp(WorkspaceLimits.PhysicalMemory() / (3UL << 30), 1, 10);

    const string Full = "Every workspace this PC runs smoothly is in use right now. Try again in a minute.";

    internal static void Start()
    {
        if (_current is not null) return;
        // A Deskweave that crashed or was killed left tickets naming pipes nobody serves.
        WorkspaceAccessStore.SweepStale();
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _retry?.Dispose();
            _retry = null;
        }
        WorkspaceAccessStore.WithdrawRouter(_server);
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
    internal static (WorkspaceRuntime? Runtime, string? Why) Place(string cwd, string client)
    {
        try
        {
            IReadOnlyList<StoredWorkspace> all = WorkspaceStore.All();
            WorkspaceHome.Route route = WorkspaceHome.Decide(all, cwd, client,
                id => WorkspaceRuntime.Of(id)?.Access?.HasDriver == true);
            StoredWorkspace? workspace = route.Existing is { } id
                ? all.FirstOrDefault(w => w.Id == id) : null;
            if (route.Existing is not null && workspace is null) return (null, "That workspace was just deleted. Try again.");
            WorkspaceAccessPolicy policy = workspace is null ? new() : WorkspaceAccessStore.Read(workspace.Id);
            WorkspaceRuntime? current = workspace is null ? null : WorkspaceRuntime.Of(workspace.Id);
            bool configured = workspace is not null
                && File.Exists(Path.Combine(WorkspaceAccessStore.Folder(workspace.Id), "access.json"));
            // A first-use Scratch record has no policy yet. Once access was explicitly configured,
            // reconnecting must never turn it back on, even if the routing rule is still present.
            if (configured && (!policy.Enabled || current?.Access is { Policy.Enabled: false }))
                return (null, "Agent access is off for this workspace. Ask the owner to enable it.");
            int running = WorkspaceRuntime.Running.Count;
            if (current is null && running >= MaxRunning && !WorkspaceRuntime.SleepQuietest()) return (null, Full);
            workspace ??= WorkspaceStore.Update(WorkspaceStore.Create(route.Name).Id, created => created with { Agents = route.Rule });
            if (workspace is null) return (null, "That workspace was just deleted. Try again.");
            if (!policy.Enabled)
            {
                policy = policy with { Enabled = true };
                WorkspaceAccessStore.Write(workspace.Id, policy);
            }
            WorkspaceRuntime runtime = WorkspaceRuntime.Start(workspace);
            if (!configured && runtime.Access is { Policy.Enabled: false } access) access.Configure(policy);
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

            if (WorkspaceMcp.ValidateCall(parameters, out string tool, out _) is { } invalid)
                return Error(id, -32602, invalid);
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
            // A full PC is a wait, not a failure (MVP_SPEC, Sleep): under the 60-second tool timeout.
            (WorkspaceRuntime? runtime, string? why) = (null, null);
            for (long until = Environment.TickCount64 + 45_000; ; await Task.Delay(1500, cancel).ConfigureAwait(false))
            {
                (runtime, why) = await ui.InvokeAsync(() => Place(_cwd, _client), DispatcherPriority.Normal, cancel);
                if (why != Full || Environment.TickCount64 > until) break;
            }
            if (runtime?.Access is not { } access) return (null, why ?? "The workspace stopped while it was starting. Try again.");
            WorkspacePipePeer peer;
            try { peer = access.Attach(_cwd); }
            catch (IOException ex) { return (null, ex.Message); }
            // The workspace hears this agent's own hello, so the owner sees its name on the tile.
            // A disconnect while attaching must also remove this not-yet-bound client.
            try
            {
                if (_hello is not null) await peer.Handle(_hello, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            catch { peer.Closed(); throw; }
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
