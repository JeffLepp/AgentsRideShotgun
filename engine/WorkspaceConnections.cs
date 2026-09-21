using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HiveMind.AgentWorkspaces;

/// <summary>Explicit-click integration using the provider's configuration command, not a TOML rewrite.</summary>
internal static class WorkspaceConnections
{
    internal static string Bridge => Path.Combine(Path.GetDirectoryName(typeof(WorkspaceMcp).Assembly.Location)!,
        "Bridge", "Deskweave.WorkspaceBridge.exe");
    internal static string Name(string id) => "deskweave_workspace_" + id;
    internal static object Configuration(string id) => new
    {
        mcpServers = new Dictionary<string, object>
        {
            [Name(id)] = new { type = "stdio", command = Bridge, args = new[] { "--workspace", WorkspaceAccessStore.Connection(id) } },
        },
    };

    internal static bool IsOwned(JsonElement entry, string id)
    {
        if (!entry.TryGetProperty("transport", out var transport)) return false;
        return transport.TryGetProperty("command", out var command)
            && string.Equals(command.GetString(), Bridge, StringComparison.OrdinalIgnoreCase)
            && transport.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            && args.GetArrayLength() == 2 && args[0].GetString() == "--workspace"
            && string.Equals(args[1].GetString(), WorkspaceAccessStore.Connection(id), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The single entry an agent app gets. Every workspace is reached through it.</summary>
    internal const string AppName = "deskweave";

    internal enum AgentApp { ClaudeCode, Codex }

    /// <summary>The agent apps Deskweave connects by itself, in the order first launch lists them.</summary>
    internal static readonly AgentApp[] Supported = Enum.GetValues<AgentApp>();

    internal static string DisplayName(AgentApp app) => app == AgentApp.ClaudeCode ? "Claude Code" : "Codex";

    /// <summary>Where an agent app's own command lives, or null when it is not on this PC. A seam so
    /// the probes can point it at a stub and never reach the owner's real installation. Everything
    /// that asks where an agent is asks through here, and putting a stub in place forgets what
    /// discovery already found, so a real installation can never be answered from memory afterwards.</summary>
    internal static Func<AgentApp, string?> Locate
    {
        get => _locate;
        set { _locate = value; Forget(); }
    }

    static Func<AgentApp, string?> _locate = Discover;

    internal static object AppConfiguration => new
    {
        mcpServers = new Dictionary<string, object>
        {
            [AppName] = new { type = "stdio", command = Bridge, args = new[] { "--workspace", WorkspaceAccessStore.RouterTicket } },
        },
    };

    internal static bool IsInstalled(AgentApp app) => Locate(app) is not null;

    /// <summary>Whether the app's own configuration has a working Deskweave entry in it. A field for
    /// the same reason <see cref="Locate"/> is one: first launch, Settings and <see cref="KeepUp"/> all
    /// read through it, so a probe answers for every one of them at once. The default reads the file,
    /// never writes it, and falls back on what the app's own command <see cref="Shows"/> a connect,
    /// for the PC where the file it reads is not the file the agent writes.</summary>
    internal static Func<AgentApp, bool> IsConnected = app => ReadEntry(app) == Entry.Current || Vouched(app);

    /// <summary>Whether the app's configuration has anything under Deskweave's name, working or not.</summary>
    internal static bool HasEntry(AgentApp app) => ReadEntry(app) != Entry.None;

    /// <summary>
    /// The agents whose own command showed Deskweave's entry in place after a connect. Only
    /// <see cref="Change"/> writes it, and a disconnect takes it back, so nothing here ever claims
    /// a connection that was not just made or is no longer wanted.
    ///
    /// It is what keeps the cheap readers cheap. <see cref="Shows"/> costs a process, which is
    /// fine once after a write and out of the question on every Settings render, so its answer is
    /// kept for the rest of the run: without it, a PC where the file read looks in the wrong place
    /// would show "Found on this PC" in Settings forever and have <see cref="KeepUp"/> connecting
    /// an already-connected agent every ten minutes until the app closes.
    /// </summary>
    static bool Vouched(AgentApp app)
    {
        lock (Vouches) return _vouched.Contains(app);
    }

    static void Vouch(AgentApp app, bool shown)
    {
        lock (Vouches)
            if (shown) _vouched.Add(app);
            else _vouched.Remove(app);
    }

    static readonly Lock Vouches = new();
    static readonly HashSet<AgentApp> _vouched = [];

    /// <summary>
    /// What an agent app's configuration holds under Deskweave's name. Stale is an entry that runs
    /// some other bridge or ticket: an older install, a moved folder, a test build. Counting one as
    /// connected left the agent pointed at a pipe nobody serves, with nothing ever replacing it.
    /// </summary>
    internal enum Entry { None, Current, Stale }

    static Entry ReadEntry(AgentApp app)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (app == AgentApp.Codex)
            {
                string codex = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
                    ? home : Path.Combine(profile, ".codex"), "config.toml");
                if (!File.Exists(codex)) return Entry.None;
                var table = new StringBuilder();
                bool inside = false, found = false;
                foreach (string line in File.ReadLines(codex))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        inside = trimmed is "[mcp_servers.deskweave]" or "[mcp_servers.\"deskweave\"]";
                        found |= inside;
                        continue;
                    }
                    if (inside) table.AppendLine(line);
                }
                if (!found) return Entry.None;
                string text = table.ToString();
                return Runs(TomlStrings(text, "command").FirstOrDefault(), TomlStrings(text, "args")) ? Entry.Current : Entry.Stale;
            }
            string claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } folder
                ? Path.Combine(folder, ".claude.json") : Path.Combine(profile, ".claude.json");
            if (!File.Exists(claude)) return Entry.None;
            using var stream = File.OpenRead(claude);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                || servers.ValueKind != JsonValueKind.Object || !servers.TryGetProperty(AppName, out JsonElement entry)) return Entry.None;
            string? command = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("command", out JsonElement c)
                && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            List<string> args = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("args", out JsonElement a)
                && a.ValueKind == JsonValueKind.Array
                ? [.. a.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.String ? arg.GetString() ?? "" : "")] : [];
            return Runs(command, args) ? Entry.Current : Entry.Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Entry.None; }
    }

    /// <summary>Whether an entry runs this Deskweave's bridge against its one router ticket.</summary>
    static bool Runs(string? command, IReadOnlyList<string> args) =>
        command is not null && SamePath(command, Bridge) && args.Count == 2 && args[0] == "--workspace"
        && SamePath(args[1], WorkspaceAccessStore.RouterTicket);

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// The strings one key holds in a TOML table: its value, or each string of an array. Enough for
    /// the command and args Codex writes, as basic "..." or literal '...' strings; not a TOML reader.
    /// </summary>
    internal static List<string> TomlStrings(string table, string key)
    {
        List<string> found = [];
        var start = System.Text.RegularExpressions.Regex.Match(table, @"(?m)^[ \t]*" + key + @"[ \t]*=[ \t]*");
        if (!start.Success) return found;
        int i = start.Index + start.Length;
        bool array = i < table.Length && table[i] == '[';
        if (array) i++;
        while (i < table.Length)
        {
            char c = table[i];
            if (c is '"' or '\'')
            {
                var text = new StringBuilder();
                for (i++; i < table.Length && table[i] != c; i++)
                {
                    if (c == '\'' || table[i] != '\\' || i + 1 >= table.Length) { text.Append(table[i]); continue; }
                    char escaped = table[++i];
                    int hex = escaped == 'u' ? 4 : escaped == 'U' ? 8 : 0;
                    if (hex > 0 && i + hex < table.Length
                        && int.TryParse(table.AsSpan(i + 1, hex), System.Globalization.NumberStyles.HexNumber, null, out int code))
                    {
                        text.Append(char.ConvertFromUtf32(code));
                        i += hex;
                    }
                    else text.Append(escaped switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => escaped });
                }
                found.Add(text.ToString());
                i++;
                if (!array) break;
            }
            else if (array && c == ']' || !array && c is '\n' or '#') break;
            else if (c == '#') { while (i < table.Length && table[i] != '\n') i++; }
            else i++;
        }
        return found;
    }

    /// <summary>
    /// Adds or removes Deskweave in one agent app's configuration, through that app's own command.
    /// Returns why it could not, or null. The one call that writes an agent's configuration, and a
    /// field so a probe stands in for every caller at once: first launch, Settings and the keep-up
    /// loop all come through here. No model runs and nothing else in the configuration moves.
    /// </summary>
    internal static Func<AgentApp, bool, CancellationToken, Task<string?>> SetConnected = Change;

    static async Task<string?> Change(AgentApp app, bool connect, CancellationToken cancel)
    {
        string name = DisplayName(app);
        string? cli = Locate(app);
        if (cli is null) return name + " isn't installed on this PC.";
        if (connect && !File.Exists(Bridge)) return "Deskweave's workspace bridge is missing. Reinstall Deskweave.";
        string[] scope = app == AgentApp.ClaudeCode ? ["--scope", "user"] : [];
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        // Replace, never duplicate: an entry from an older Deskweave, working or stale, comes out first.
        // A stale one left in place would make the add below refuse the name.
        bool had = HasEntry(app);
        if (had) await Run(cli, ["mcp", "remove", .. scope, AppName], bound.Token, null).ConfigureAwait(false);
        // Whatever the remove did, what this run was told about the entry has stopped being true.
        Vouch(app, false);
        if (!connect) return HasEntry(app) ? $"{name} kept its Deskweave entry. Remove it in {name}'s MCP settings." : null;
        await Run(cli, ["mcp", "add", .. scope, AppName, "--", Bridge, "--workspace", WorkspaceAccessStore.RouterTicket],
            bound.Token, null).ConfigureAwait(false);
        // What the configuration holds now, not what the command claimed: one that exits 0 without
        // writing the entry has connected nothing. The file first, because it costs nothing; the
        // app's own command after, because it is the one that knows where it wrote. Its exit code
        // is not the question either - an add that refuses a name it already holds has still left
        // the owner connected, and telling him it failed would be the same lie in reverse.
        if (IsConnected(app)) return null;
        if (await Shows(app, cli, bound.Token).ConfigureAwait(false)) { Vouch(app, true); return null; }
        return had
            // The old entry came out for the replacement and the new one did not go in. Saying
            // nothing changed would be a lie, and it would hide a connection that is gone.
            ? $"{name} did not accept the connection, and its earlier Deskweave entry came out with it. Connect it again in Settings."
            : $"{name} did not accept the connection. Nothing else was changed.";
    }

    /// <summary>
    /// Whether the app's own command shows Deskweave's entry in place, running this install's
    /// bridge against its one router ticket - <see cref="Runs"/>'s question, asked of the tool that
    /// did the write instead of a file.
    ///
    /// The two can disagree, because the command's environment is not Deskweave's. `claude` on PATH
    /// may be a shim that clears CLAUDE_CONFIG_DIR before calling the real one, and then the add
    /// lands in ~/.claude.json while <see cref="ReadEntry"/> opens the folder that variable names.
    /// Nothing crashes: the owner is told a connection he just made was refused, and the keep-up
    /// loop makes it again every ten minutes forever. The command cannot disagree with itself.
    ///
    /// False is "did not show it", never "it is not there". A command that fails, times out or
    /// prints something this does not understand leaves <see cref="ReadEntry"/>'s answer standing,
    /// so a provider that changes its output turns the fix off rather than breaking connecting.
    /// </summary>
    static async Task<bool> Shows(AgentApp app, string cli, CancellationToken cancel)
    {
        // Its own budget inside Change's 30 s: `claude mcp get` health-checks the server it names,
        // and a bridge that cannot reach Deskweave waits. A check that hangs must not spend the
        // time the write was given, or cost the owner twice the wait before he is told no.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bound.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var shown = await Run(cli, app == AgentApp.Codex ? ["mcp", "list", "--json"] : ["mcp", "get", AppName],
                bound.Token, null).ConfigureAwait(false);
            if (shown.Code != 0) return false;   // Claude Code exits 1 for a name it does not have
            return app == AgentApp.Codex ? ShownByCodex(shown.Output) : ShownByClaude(shown.Output);
        }
        // A command that is not there any more, or one the app is still writing its answer to.
        // Cancellation the caller asked for is the app closing, and belongs to the caller.
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception
            || ex is OperationCanceledException && !cancel.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Deskweave's entry in what `codex mcp list --json` prints, which is the array
    /// <see cref="Codex"/> already reads for a single workspace's own entry.
    /// </summary>
    static bool ShownByCodex(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement server in document.RootElement.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String || name.GetString() != AppName) continue;
            if (!server.TryGetProperty("transport", out JsonElement transport)
                || transport.ValueKind != JsonValueKind.Object) return false;
            string? command = transport.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            List<string> args = transport.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Array
                ? [.. a.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.String ? arg.GetString() ?? "" : "")] : [];
            return Runs(command, args);
        }
        return false;
    }

    /// <summary>
    /// Deskweave's entry in what `claude mcp get deskweave` prints. Claude Code has no --json for
    /// mcp (2.1.278), and `mcp list` puts the name, the command and the args on one line with no
    /// separator between them, which is not something to take a path out of. `get` labels Command
    /// and Args on lines of their own, and health-checks the one server it was asked about rather
    /// than every server the owner has.
    ///
    /// Its args come back joined by spaces. Deskweave writes exactly two, the second a path that
    /// can hold spaces itself, so only the first space between them is a separator - and any other
    /// entry that splits wrong is one <see cref="Runs"/> was going to refuse anyway. Whether the
    /// health check passed is not read: the question is what the agent would run, and Deskweave's
    /// own router may still be starting.
    /// </summary>
    static bool ShownByClaude(string text)
    {
        string? command = null, arguments = null;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Command:", StringComparison.Ordinal)) command ??= trimmed["Command:".Length..].Trim();
            else if (trimmed.StartsWith("Args:", StringComparison.Ordinal)) arguments ??= trimmed["Args:".Length..].Trim();
        }
        if (command is null || arguments is null) return false;
        int between = arguments.IndexOf(' ');
        string[] args = between < 0 ? [arguments] : [arguments[..between], arguments[(between + 1)..]];
        return Runs(command, args);
    }

    /// <summary>
    /// Connects the given agent apps and answers with the ones that would not, each with the one
    /// line to show the owner. Safe to repeat: <see cref="SetConnected"/> replaces an entry rather
    /// than adding a second, so a PC that has seen ten first launches still has one per agent.
    /// </summary>
    internal static async Task<IReadOnlyList<(AgentApp App, string Why)>> Connect(
        IEnumerable<AgentApp> apps, CancellationToken cancel = default)
    {
        List<(AgentApp, string)> refused = [];
        foreach (AgentApp app in apps)
            if (await SetConnected(app, true, cancel).ConfigureAwait(false) is { } why) refused.Add((app, why));
        return refused;
    }

    /// <summary>
    /// The owner's answer for one agent, from a switch on first launch or in Settings. Off is what
    /// is kept: everything supported is connected once Start was pressed, so the only thing worth
    /// remembering is an agent he said no to, and nothing connects that one behind his back.
    /// </summary>
    internal static void Remember(AgentApp app, bool on) => AppSettingsStore.Update(s => s with
    {
        AgentsOff = on ? [.. s.AgentsOff.Where(off => off != app.ToString())]
            : s.AgentsOff.Contains(app.ToString()) ? s.AgentsOff : [.. s.AgentsOff, app.ToString()],
    });

    /// <summary>Whether the owner turned this agent off, on first launch or in Settings.</summary>
    internal static bool TurnedOff(AgentApp app) => AppSettingsStore.Current.AgentsOff.Contains(app.ToString());

    /// <summary>An agent the keep-up loop should still connect: on this PC, not connected, not refused.</summary>
    internal static bool Missing(AgentApp app) => IsInstalled(app) && !IsConnected(app) && !TurnedOff(app);

    /// <summary>How often a PC with a supported agent still missing is looked at again.</summary>
    internal static TimeSpan KeepUpEvery = TimeSpan.FromMinutes(10);

    static CancellationTokenSource? _keepingUp;

    /// <summary>
    /// The owner pressed Start once, so an agent installed later is connected without being asked
    /// again (MVP_SPEC, Behavior). Looks now and then every <see cref="KeepUpEvery"/>, and stops
    /// itself once no supported agent is still missing, so the ordinary PC pays nothing. Off the UI
    /// thread: finding an agent's command walks folders. Does nothing without consent, and never
    /// touches an agent the owner turned off.
    /// </summary>
    internal static void KeepUp()
    {
        if (_keepingUp is not null || !AppSettingsStore.Current.ConnectAgents) return;
        var stop = _keepingUp = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var clock = new PeriodicTimer(KeepUpEvery);
            try
            {
                do
                {
                    // Both answers can change while this waits: consent can be withdrawn, and an
                    // agent can be turned off in Settings. Either one is read again every pass.
                    if (!AppSettingsStore.Current.ConnectAgents) continue;
                    await Connect(Supported.Where(Missing), stop.Token).ConfigureAwait(false);
                    // Only an answer ends this, not an empty PC: every supported agent is either
                    // connected or turned off. One that is not installed yet is what it waits for.
                    if (Supported.All(app => IsConnected(app) || TurnedOff(app))) return;
                }
                while (await clock.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { }   // the app is closing
            // Leaves the field empty for the next KeepUp, unless StopKeepingUp already took it.
            finally { Interlocked.CompareExchange(ref _keepingUp, null, stop); stop.Dispose(); }
        }, stop.Token);
    }

    /// <summary>App exit, and the probes between runs. Nothing is left writing an agent's configuration.</summary>
    internal static void StopKeepingUp()
    {
        if (Interlocked.Exchange(ref _keepingUp, null) is not { } stop) return;
        // Disposed by the loop it stops, which may still be inside a wait on this token.
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal static async Task<string> Codex(string id, bool connect, CancellationToken cancel = default, string? profileRoot = null)
    {
        // Through the seam, like every other caller: what is on this PC is one question with one
        // answer, and a probe that stands in for it must stand in for this too.
        string? cli = Locate(AgentApp.Codex);
        if (cli is null) return "Codex is not installed in a supported local location. You can still copy the MCP connection for another agent.";
        if (!File.Exists(Bridge)) return "The packaged workspace bridge is missing. Reinstall Deskweave.";
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        var listed = await Run(cli, ["mcp", "list", "--json"], bound.Token, profileRoot).ConfigureAwait(false);
        if (listed.Code != 0) return "Codex could not read its configuration. No connection was changed.";
        using var document = JsonDocument.Parse(listed.Output);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return "Codex returned an unsupported configuration format. Nothing changed.";
        JsonElement? existing = document.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("name", out var name) && name.GetString() == Name(id))
            .Select(e => (JsonElement?)e).FirstOrDefault();
        if (existing is { } entry && !IsOwned(entry, id))
            return "That connection name is already used by another configuration. Deskweave left it unchanged.";
        if (connect && existing is not null) return "Already connected. Start this workspace, then restart its MCP connection in Codex settings.";
        if (!connect && existing is null) return "This workspace has no Codex connection to remove.";
        string[] arguments = connect
            ? ["mcp", "add", Name(id), "--", Bridge, "--workspace", WorkspaceAccessStore.Connection(id)]
            : ["mcp", "remove", Name(id)];
        var result = await Run(cli, arguments, bound.Token, profileRoot).ConfigureAwait(false);
        if (result.Code != 0) return "Codex did not complete the configuration change. Check its MCP settings.";
        return connect
            ? "Connected. Start this workspace and restart its MCP connection in Codex settings. Future app and browser work can use this workspace."
            : "Codex connection removed. Turn Agent access off to disconnect existing clients immediately.";
    }

    static async Task<(int Code, string Output)> Run(string cli, string[] arguments, CancellationToken cancel, string? profileRoot)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (profileRoot is not null) start.Environment["CODEX_HOME"] = profileRoot;
        using var process = Process.Start(start) ?? throw new IOException("Codex did not start.");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancel);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            await stderr.ConfigureAwait(false); // do not display provider configuration or credentials
            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    /// <summary>
    /// Uninstall: takes out the entries that run this install's bridge, and only those. An entry
    /// under the same name that runs anything else (another copy, a server the owner set up by
    /// hand) is not ours to remove. Both agents at once, inside 20 s: the uninstaller ends its hook
    /// at 30, and one agent that hangs must not cost the other its cleanup.
    /// </summary>
    internal static async Task RemoveOwnedConnections()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.WhenAll(Enum.GetValues<AgentApp>().Select(async app =>
        {
            try { if (IsConnected(app)) await SetConnected(app, false, budget.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        })).ConfigureAwait(false);
        if (!Directory.Exists(WorkspaceAccessStore.Root)) return;
        foreach (string folder in Directory.EnumerateDirectories(WorkspaceAccessStore.Root))
        {
            string id = Path.GetFileName(folder);
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) continue;
            try { await Codex(id, false, budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            WorkspaceAccessStore.Withdraw(id);
        }
    }

    /// <summary>
    /// Where an agent's command is, looked for once. <see cref="IsInstalled"/> is read while a
    /// window is being built - first launch's card, and twice over on every Settings render - and
    /// the answer walks PATH, the registry and a list of folders, so it is paid for one time. A
    /// command that was found does not move while Deskweave is running.
    ///
    /// Finding nothing is remembered only briefly, for the reason <see cref="WorkspaceBrowser"/>
    /// forgets a missing browser: Deskweave starts with Windows and sits in the tray while the
    /// owner installs an agent, and the keep-up loop is waiting for exactly that agent.
    /// </summary>
    static string? Discover(AgentApp app)
    {
        lock (Known)
            if (_known.TryGetValue(app, out (string? Path, long Until) was)
                && (was.Path is not null || Environment.TickCount64 < was.Until))
                return was.Path;
        string? found = app == AgentApp.ClaudeCode ? FindClaude() : FindCodex();
        lock (Known) _known[app] = (found, Environment.TickCount64 + 30_000);
        return found;
    }

    static readonly Lock Known = new();
    static readonly Dictionary<AgentApp, (string? Path, long Until)> _known = [];

    /// <summary>Forgets where the agents' commands were, so the next look reads this PC again.</summary>
    static void Forget()
    {
        lock (Known) _known.Clear();
    }

    /// <summary>
    /// Claude Code's own command. Until 2026-09-19 this was four hardcoded paths and no PATH lookup
    /// at all, so a Node under nvm-windows, fnm or Volta, anyone who had run `npm config set
    /// prefix`, a machine-wide install, and the winget, scoop and chocolatey shims were every one of
    /// them told "No supported agent found on this PC" - with no override anywhere in Settings, so
    /// the product did nothing for them. The four paths are still asked, last, so no PC this already
    /// found is worse off.
    /// </summary>
    static string? FindClaude()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Command("claude") ?? Existing(
            Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(roaming, "npm", "claude.cmd"), Path.Combine(roaming, "npm", "claude"));
    }

    static string? FindCodex()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Command("codex") ?? Existing(Path.Combine(home, ".local", "bin", "codex.exe")) ?? Packaged();
    }

    static string? Existing(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>The extensions CreateProcess can start, in the order PATHEXT names them.</summary>
    static readonly string[] Runnable = [".exe", ".bat", ".cmd"];

    /// <summary>
    /// What Process.Start can run for a bare agent command name, resolved the way Windows itself
    /// resolves one: PATH, then the App Paths key Win+R reads, then the global command folders a
    /// PATH inherited at login may not name. The first two are <see cref="WorkspacePrograms"/>'s,
    /// the same lookups an agent's `open` uses, rather than a second copy of them here.
    ///
    /// Its third lookup, the Start Menu, is deliberately not asked: no agent CLI installs a
    /// shortcut, and reading every shell link measured 7.4 s, which is not something to spend while
    /// first launch draws its card.
    ///
    /// Only what CreateProcess can start is accepted - an .exe, or the .cmd shim npm writes, whose
    /// arguments Process.Start quotes for cmd when they are given through ArgumentList, as
    /// <see cref="Run"/> gives them. The .ps1 and the extensionless shell script npm leaves beside
    /// that shim are not things CreateProcess can run, so neither is ever returned.
    /// </summary>
    static string? Command(string name) =>
        WorkspacePrograms.PathFile(name, Runnable)
        ?? WorkspacePrograms.AppPath(name)
        ?? Folders().SelectMany(f => Runnable.Select(e => Path.Combine(f, name + e))).FirstOrDefault(File.Exists);

    /// <summary>
    /// The global command folders to look in once PATH and App Paths have not answered: npm's
    /// global prefix, and the shim folders winget, scoop, chocolatey, Volta, pnpm, Yarn and Bun
    /// keep, beside where the agents' own installers put a command.
    ///
    /// PATH as it is now is read here too. Deskweave starts with Windows and stays in the tray, so
    /// an agent installed during the session put its folder on a PATH this process will never be
    /// handed; the registry is where that installer actually wrote it.
    /// </summary>
    static IEnumerable<string> Folders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<string> folders =
        [
            Path.Combine(home, ".local", "bin"),
            .. NpmPrefixes(),
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
            Path.Combine(home, "scoop", "shims"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin"),
            Path.Combine(local, "Volta", "bin"),
            Environment.GetEnvironmentVariable("PNPM_HOME") is { Length: > 0 } pnpm ? pnpm : Path.Combine(local, "pnpm"),
            Path.Combine(local, "Yarn", "bin"),
            Path.Combine(home, ".bun", "bin"),
            .. LivePath(),
        ];
        // A relative entry would put a bare name in front of Process.Start, and the same folder
        // twice would look for the same file twice.
        return folders.Where(f => f.Length > 0 && Path.IsPathRooted(f)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where npm puts a global command: its per-user default, a machine-wide Node's own folder, and
    /// the prefix the owner set for himself, which covers `npm config set prefix` and what
    /// nvm-windows, fnm and Volta leave behind. Read from the environment and ~/.npmrc, never by
    /// running npm, because this is answered while a window is being built.
    /// </summary>
    static List<string> NpmPrefixes()
    {
        List<string> prefixes =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
        ];
        if (Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX") is { Length: > 0 } set) prefixes.Add(set);
        try
        {
            string npmrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
            if (File.Exists(npmrc))
                foreach (string line in File.ReadLines(npmrc))
                    if (line.IndexOf('=') is > 0 and var at
                        && line[..at].Trim().Equals("prefix", StringComparison.OrdinalIgnoreCase))
                        prefixes.Add(Environment.ExpandEnvironmentVariables(line[(at + 1)..].Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return prefixes;
    }

    /// <summary>PATH as this PC has it now, the user's then the machine's, from the two registry
    /// values an installer writes rather than the copy this process was started with.</summary>
    static List<string> LivePath()
    {
        List<string> folders = [];
        foreach ((RegistryKey root, string key) in new[]
        {
            (Registry.CurrentUser, "Environment"),
            (Registry.LocalMachine, @"System\CurrentControlSet\Control\Session Manager\Environment"),
        })
            try
            {
                using RegistryKey? entry = root.OpenSubKey(key);
                if (entry?.GetValue("Path") is string path)
                    folders.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => Environment.ExpandEnvironmentVariables(f.Trim().Trim('"'))));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                or UnauthorizedAccessException or ArgumentException) { }
        return folders;
    }

    /// <summary>
    /// The executable inside Codex's own npm package, for an install whose shim is not anywhere the
    /// lookups above reach. Bounded to that one package folder, and reached only on a miss.
    ///
    /// What used to sit beside it was the same sweep over ~/.vscode/extensions with
    /// SearchOption.AllDirectories - tens of thousands of files and gigabytes on a working
    /// developer's PC, walked on the UI thread while first launch drew its card, for a copy bundled
    /// inside an editor extension that is not the owner's installed CLI anyway. It is gone.
    /// </summary>
    static string? Packaged()
    {
        foreach (string prefix in NpmPrefixes())
        {
            string root = Path.Combine(prefix, "node_modules", "@openai");
            if (!Directory.Exists(root)) continue;
            try
            {
                if (Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() is { } found) return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
