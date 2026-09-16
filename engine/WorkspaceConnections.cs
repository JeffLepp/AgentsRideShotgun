using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

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

    /// <summary>Where an agent app's own command lives, or null when it is not on this PC. A field so
    /// the probes can point it at a stub and never reach the owner's real installation.</summary>
    internal static Func<AgentApp, string?> Locate =
        app => app == AgentApp.ClaudeCode ? WorkspaceAgent.FindCli() : FindCodex();

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
    /// never writes it.</summary>
    internal static Func<AgentApp, bool> IsConnected = app => ReadEntry(app) == Entry.Current;

    /// <summary>Whether the app's configuration has anything under Deskweave's name, working or not.</summary>
    internal static bool HasEntry(AgentApp app) => ReadEntry(app) != Entry.None;

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
        if (!connect) return HasEntry(app) ? $"{name} kept its Deskweave entry. Remove it in {name}'s MCP settings." : null;
        var added = await Run(cli, ["mcp", "add", .. scope, AppName, "--", Bridge, "--workspace", WorkspaceAccessStore.RouterTicket],
            bound.Token, null).ConfigureAwait(false);
        // What the configuration says now, not what the command claimed: one that exits 0 without
        // writing the entry has connected nothing.
        if (added.Code == 0 && IsConnected(app)) return null;
        return had && !IsConnected(app)
            // The old entry came out for the replacement and the new one did not go in. Saying
            // nothing changed would be a lie, and it would hide a connection that is gone.
            ? $"{name} did not accept the connection, and its earlier Deskweave entry came out with it. Connect it again in Settings."
            : $"{name} did not accept the connection. Nothing else was changed.";
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
        string? cli = FindCodex();
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

    internal static async Task RemoveOwnedConnections()
    {
        foreach (AgentApp app in Enum.GetValues<AgentApp>())
            if (HasEntry(app)) await SetConnected(app, false, default).ConfigureAwait(false);
        if (!Directory.Exists(WorkspaceAccessStore.Root)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (string folder in Directory.EnumerateDirectories(WorkspaceAccessStore.Root))
        {
            string id = Path.GetFileName(folder);
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) continue;
            await Codex(id, false, timeout.Token).ConfigureAwait(false);
            WorkspaceAccessStore.Withdraw(id);
        }
    }

    static string? FindCodex()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string direct = Path.Combine(profile, ".local", "bin", "codex.exe");
        if (File.Exists(direct)) return direct;
        foreach (string root in new[] { Path.Combine(roaming, "npm", "node_modules", "@openai"),
            Path.Combine(profile, ".vscode", "extensions") })
        {
            if (!Directory.Exists(root)) continue;
            string? found = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .Where(p => p.Contains("codex", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}
