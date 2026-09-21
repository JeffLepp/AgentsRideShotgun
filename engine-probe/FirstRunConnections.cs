using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HiveMind.AgentWorkspaces;

/// <summary>
/// What first launch does outside its own window (design/MVP_SPEC.md, Surfaces 5 and Behavior):
/// detecting the supported agents, connecting them on Start, keeping exactly one entry each across
/// repeated launches, picking up an agent installed later, and doing none of it without consent.
///
/// It runs against isolated configuration roots and a stub agent command. The owner's own
/// <c>~/.claude.json</c> and <c>~/.codex</c> are never read or written, and no model is ever run.
/// </summary>
internal static class FirstRunConnections
{
    internal static void Run(string root, Action<bool, string> Check)
    {
        string claudeHome = Path.Combine(root, "claude");
        string codexHome = Path.Combine(root, "codex");
        string claudeFile = Path.Combine(claudeHome, ".claude.json");
        string codexFile = Path.Combine(codexHome, "config.toml");
        Directory.CreateDirectory(claudeHome);
        Directory.CreateDirectory(codexHome);
        string? wasClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string? wasCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        var wasLocate = WorkspaceConnections.Locate;
        AppSettings before = AppSettingsStore.Current;
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string realClaude = Path.Combine(profile, ".claude.json");
            string realCodex = Path.Combine(profile, ".codex");
            string claudeEntry = OwnEntry(realClaude);
            string codexEntry = OwnEntry(Path.Combine(realCodex, "config.toml"));
            Check(!Same(claudeFile, realClaude) && !Same(codexHome, realCodex)
                && WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.ClaudeCode) == false
                && WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex) == false,
                "Connection checks read isolated configuration roots, not the owner's own agent configuration");

            // Nothing installed: nothing is written, and the one line says which agent it was.
            WorkspaceConnections.Locate = _ => null;
            Check(WorkspaceConnections.Supported.All(app => !WorkspaceConnections.IsInstalled(app)),
                "An agent app with no command on this PC is not offered");
            IReadOnlyList<(WorkspaceConnections.AgentApp App, string Why)> refused = Connect();
            Check(refused.Count == WorkspaceConnections.Supported.Length
                && refused.All(r => r.Why.EndsWith("isn't installed on this PC.", StringComparison.Ordinal))
                && !File.Exists(claudeFile) && !File.Exists(codexFile),
                "Connecting an agent that is not installed writes no configuration and says why in one line");

            // Both installed: one press connects both, each pointed at Deskweave's one ticket.
            WorkspaceConnections.Locate = _ => Environment.ProcessPath;
            Check(Connect().Count == 0 && WorkspaceConnections.Supported.All(WorkspaceConnections.IsConnected),
                "One Start connects every supported agent found on this PC");
            Check(Pointed(claudeFile) && Pointed(codexFile),
                "A connected agent is pointed at Deskweave's own bridge and its one router ticket");

            // Repeated launches: still one entry each, never a second.
            for (int again = 0; again < 3; again++) Connect();
            Check(Entries(claudeFile) == 1 && Entries(codexFile) == 1,
                "Four launches leave exactly one Deskweave entry in each agent's configuration");

            // A replacement that fails: the old entry is already out, so the one line has to say
            // so. Claiming nothing changed would hide a connection the owner no longer has.
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", "refuse");
            string? refusedReplacement = Set(WorkspaceConnections.AgentApp.ClaudeCode, true);
            Check(refusedReplacement is { } gone && gone.Contains("came out with it", StringComparison.Ordinal)
                && !WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.ClaudeCode) && Entries(claudeFile) == 0,
                "A replacement that fails says the earlier entry came out with it, not that nothing changed");
            Check(Set(WorkspaceConnections.AgentApp.ClaudeCode, true) is { } nothing
                && nothing.EndsWith("Nothing else was changed.", StringComparison.Ordinal) && Entries(claudeFile) == 0,
                "A connection that fails with no entry to replace says nothing else changed, and nothing did");

            // A command that exits 0 and writes nothing has connected nothing, whatever it says.
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", "silent");
            Check(Set(WorkspaceConnections.AgentApp.ClaudeCode, true) is not null
                && !WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.ClaudeCode),
                "An agent command that exits without writing the entry is not reported as connected");
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", null);
            Check(Set(WorkspaceConnections.AgentApp.ClaudeCode, true) is null && Entries(claudeFile) == 1,
                "Connecting again after a refusal puts the one entry back");

            // A command whose own environment is not Deskweave's: `claude` on PATH can be a wrapper
            // that clears CLAUDE_CONFIG_DIR before calling the real one, so the add lands in a file
            // the read never opens. Nothing crashes - the owner is told the connection he has just
            // made was refused, and told again every ten minutes. The tool that did the write is
            // asked whether the write happened, because it is the only thing that knows.
            Set(WorkspaceConnections.AgentApp.ClaudeCode, false);
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", "elsewhere");
            string shim = Path.Combine(claudeHome, "shim", ".claude.json");
            Check(Set(WorkspaceConnections.AgentApp.ClaudeCode, true) is null
                && Entries(shim) == 1 && Entries(claudeFile) == 0
                && WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.ClaudeCode),
                "An agent that writes where Deskweave cannot read is connected on its own command's word, not called refused");
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", null);
            Check(Set(WorkspaceConnections.AgentApp.ClaudeCode, false) is null
                && !WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.ClaudeCode),
                "Disconnecting forgets what the agent's command showed, so nothing goes on saying connected");
            if (Directory.Exists(Path.GetDirectoryName(shim)!)) Directory.Delete(Path.GetDirectoryName(shim)!, recursive: true);
            Set(WorkspaceConnections.AgentApp.ClaudeCode, true);

            // An entry under Deskweave's name that runs something else: an older install, a moved
            // folder, a probe build. The owner's own configuration had one pointing at a gate's
            // fixture, and Settings called it connected while every agent call failed.
            WriteStale(claudeFile, codexFile, root);
            Check(WorkspaceConnections.Supported.All(app => WorkspaceConnections.HasEntry(app)
                    && !WorkspaceConnections.IsConnected(app) && WorkspaceConnections.Missing(app)),
                "An entry that runs another bridge or ticket counts as stale, not connected");
            Check(Connect().Count == 0 && WorkspaceConnections.Supported.All(WorkspaceConnections.IsConnected)
                && Entries(claudeFile) == 1 && Entries(codexFile) == 1 && Pointed(claudeFile) && Pointed(codexFile)
                && File.ReadAllText(claudeFile).Contains("another.exe", StringComparison.Ordinal)
                && File.ReadAllText(codexFile).Contains("[mcp_servers.another]", StringComparison.Ordinal),
                "Connecting replaces a stale entry with one for this Deskweave and leaves the agent's other servers alone");
            File.WriteAllLines(codexFile, ["[mcp_servers.deskweave]", "command = '" + WorkspaceConnections.Bridge + "'",
                "args = [\"--workspace\", '" + WorkspaceAccessStore.RouterTicket + "']"]);
            Check(WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex),
                "An entry in the literal-string form Codex itself writes counts as connected, so nothing rewrites it");

            // An agent installed after consent is connected on its own, without a second prompt.
            File.Delete(codexFile);
            WorkspaceConnections.Locate = app => app == WorkspaceConnections.AgentApp.Codex ? null : Environment.ProcessPath;
            AppSettingsStore.Update(s => s with { ConnectAgents = true, FirstRunDone = true, AgentsOff = [] });
            WorkspaceConnections.KeepUpEvery = TimeSpan.FromSeconds(1);
            WorkspaceConnections.KeepUp();
            Thread.Sleep(1500);
            Check(!WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex),
                "An agent that is not installed yet is left alone");
            WorkspaceConnections.Locate = _ => Environment.ProcessPath;
            Check(Until(() => WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex)) && Entries(codexFile) == 1,
                "After Start, an agent installed later is connected by itself, once, with no second prompt");

            // The loop mends an entry that went stale, as one a moved Deskweave leaves behind would be.
            WorkspaceConnections.StopKeepingUp();
            WriteStale(claudeFile, codexFile, root);
            WorkspaceConnections.KeepUp();
            Check(Until(() => WorkspaceConnections.Supported.All(WorkspaceConnections.IsConnected))
                && Entries(claudeFile) == 1 && Entries(codexFile) == 1 && Pointed(claudeFile) && Pointed(codexFile),
                "After Start, the keep-up loop replaces stale entries by itself, once each");
            WorkspaceConnections.StopKeepingUp();

            // Turned off in Settings, with the loop still running because the other agent has left
            // this PC and is still worth waiting for. It has to leave the one the owner took out.
            Check(Set(WorkspaceConnections.AgentApp.Codex, false) is null && Entries(codexFile) == 0,
                "Turning an agent off in Settings takes Deskweave out of its configuration");
            Set(WorkspaceConnections.AgentApp.ClaudeCode, false);
            WorkspaceConnections.Locate = app => app == WorkspaceConnections.AgentApp.ClaudeCode ? null : Environment.ProcessPath;
            WorkspaceConnections.Remember(WorkspaceConnections.AgentApp.Codex, false);
            WorkspaceConnections.KeepUp();
            Thread.Sleep(2500);
            Check(!WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex) && Entries(codexFile) == 0,
                "An agent the owner turned off is never connected again by the keep-up loop");
            WorkspaceConnections.Remember(WorkspaceConnections.AgentApp.Codex, true);
            Check(Until(() => WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex)) && Entries(codexFile) == 1,
                "Turning it back on in Settings lets the keep-up loop connect it again, once");
            WorkspaceConnections.StopKeepingUp();
            WorkspaceConnections.Locate = _ => Environment.ProcessPath;

            // Without Start nothing keeps up: closing first launch really does change nothing.
            File.Delete(codexFile);
            AppSettingsStore.Update(s => s with { ConnectAgents = false });
            WorkspaceConnections.KeepUp();
            Thread.Sleep(3000);
            Check(!WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex),
                "Without the owner's Start nothing is ever written to an agent's configuration");
            WorkspaceConnections.StopKeepingUp();

            // The scoped instruction that comes with connecting, in both directions.
            Check(WorkspaceMcp.RouterInstructions.Contains(WorkspaceMcp.Scope, StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Use Deskweave automatically for agent-operated browser and GUI work", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("use your normal approved desktop-opening tools", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Do not silently divert a user-requested desktop action into a workspace", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Do not use Deskweave for anything else", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("builds, unit tests", StringComparison.Ordinal),
                "A connected agent is told to use Deskweave for windows by itself, and not for code, builds, tests or file work");

            Check(OwnEntry(realClaude) == claudeEntry && OwnEntry(Path.Combine(realCodex, "config.toml")) == codexEntry,
                "Deskweave's entry in the owner's own Claude Code and Codex configuration is exactly as it was");
        }
        finally
        {
            WorkspaceConnections.StopKeepingUp();
            Environment.SetEnvironmentVariable("DESKWEAVE_STUB", null);
            WorkspaceConnections.Locate = wasLocate;
            WorkspaceConnections.KeepUpEvery = TimeSpan.FromMinutes(10);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", wasClaude);
            Environment.SetEnvironmentVariable("CODEX_HOME", wasCodex);
            AppSettingsStore.Update(_ => before);
        }

        static IReadOnlyList<(WorkspaceConnections.AgentApp App, string Why)> Connect() =>
            WorkspaceConnections.Connect(WorkspaceConnections.Supported).GetAwaiter().GetResult();

        static string? Set(WorkspaceConnections.AgentApp app, bool on) =>
            WorkspaceConnections.SetConnected(app, on, default).GetAwaiter().GetResult();

        static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deskweave's own entry in one of the owner's real configuration files, as its text, or nothing
    /// when it has none. It is the only part of that file these checks could ever write, and a whole
    /// file would be the wrong thing to compare: an agent session of the owner's own rewrites its
    /// history while the probe runs, which says nothing about Deskweave.
    /// </summary>
    static string OwnEntry(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                return string.Concat(Table(File.ReadAllLines(path), ours: true));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                && servers.TryGetProperty(WorkspaceConnections.AppName, out JsonElement ours) ? ours.GetRawText() : "";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return "unreadable"; }
    }

    /// <summary>
    /// Stale Deskweave entries in both agents' own formats, beside a server that is not Deskweave's:
    /// Claude Code's JSON, and Codex's TOML with the literal strings Codex writes.
    /// </summary>
    static void WriteStale(string claudeFile, string codexFile, string root)
    {
        string bridge = Path.Combine(root, "moved", "Bridge", "Deskweave.WorkspaceBridge.exe");
        string ticket = Path.Combine(root, "moved", "agent-workspaces.access", "router.json");
        File.WriteAllText(claudeFile, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["another"] = new { type = "stdio", command = "another.exe", args = Array.Empty<string>() },
                [WorkspaceConnections.AppName] = new { type = "stdio", command = bridge, args = new[] { "--workspace", ticket }, env = new { } },
            },
        }));
        File.WriteAllLines(codexFile, ["[mcp_servers.another]", "command = 'another.exe'", "",
            "[mcp_servers.deskweave]", "command = '" + bridge + "'", "args = [\"--workspace\", '" + ticket + "']"]);
    }

    static bool Until(Func<bool> ready)
    {
        var waited = Stopwatch.StartNew();
        while (!ready() && waited.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        return ready();
    }

    /// <summary>Whether the entry runs Deskweave's own bridge against the router ticket.</summary>
    static bool Pointed(string configuration)
    {
        string text = File.ReadAllText(configuration);
        return text.Contains(Escaped(WorkspaceConnections.Bridge), StringComparison.OrdinalIgnoreCase)
            && text.Contains(Escaped(WorkspaceAccessStore.RouterTicket), StringComparison.OrdinalIgnoreCase);

        // Both formats escape a Windows separator the same way.
        static string Escaped(string path) => path.Replace(@"\", @"\\");
    }

    /// <summary>How many Deskweave entries an agent's configuration holds. More than one is the bug.</summary>
    static int Entries(string configuration)
    {
        if (!File.Exists(configuration)) return 0;
        if (configuration.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            return File.ReadLines(configuration).Count(line => line.Trim() == "[mcp_servers.deskweave]");
        using var json = JsonDocument.Parse(File.ReadAllText(configuration));
        return json.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
            && servers.TryGetProperty("deskweave", out _) ? 1 : 0;
    }

    /// <summary>
    /// The stub `claude mcp` / `codex mcp` command this probe hands to
    /// <see cref="WorkspaceConnections.Locate"/>, so nothing here runs the owner's real agent.
    /// Claude Code passes --scope and keeps JSON; Codex passes none and keeps TOML. It writes only
    /// under the isolated root the environment names, and it starts no model.
    /// </summary>
    internal static int Cli(string[] args)
    {
        // Claude Code takes --scope and is the one asked `mcp get`; Codex takes neither and is the
        // one asked `mcp list --json`.
        bool claude = args.Contains("--scope") || args.Length > 1 && args[1] == "get";
        if (Environment.GetEnvironmentVariable(claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME") is not { Length: > 0 } home) return 2;
        // Standing in for a PATH shim that hands the real command a configuration folder Deskweave's
        // own environment does not name, which is what `claude` resolving to a wrapper that clears
        // CLAUDE_CONFIG_DIR does. The entry is written and the file Deskweave reads never shows it.
        if (Environment.GetEnvironmentVariable("DESKWEAVE_STUB") == "elsewhere")
            Directory.CreateDirectory(home = Path.Combine(home, "shim"));
        string path = Path.Combine(home, claude ? ".claude.json" : "config.toml");
        if (args.Length < 3) return 2;
        if (args[1] is "get" or "list") return Shown(path, claude);
        if (!args.Contains(WorkspaceConnections.AppName)) return 2;
        if (args[1] == "remove")
        {
            if (!File.Exists(path)) return 0;
            if (claude) Save(path, Without(Read(path)));
            else File.WriteAllLines(path, Table(File.ReadAllLines(path), ours: false));
            return 0;
        }
        if (args[1] != "add") return 2;
        // Standing in for an agent command that will not take the entry, or says it did and wrote
        // nothing. The probe asks for it in the environment; without it every add is a real write.
        string? how = Environment.GetEnvironmentVariable("DESKWEAVE_STUB");
        if (how is "refuse" or "silent") return how == "refuse" ? 3 : 0;
        string[] command = [.. args.SkipWhile(a => a != "--").Skip(1)];
        if (command.Length == 0) return 2;
        if (claude)
        {
            JsonObject document = File.Exists(path) ? Read(path) : [];
            var servers = document["mcpServers"] as JsonObject ?? [];
            servers[WorkspaceConnections.AppName] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = command[0],
                ["args"] = new JsonArray([.. command.Skip(1).Select(a => (JsonNode)JsonValue.Create(a)!)]),
            };
            document["mcpServers"] = servers;
            Save(path, document);
            return 0;
        }
        List<string> lines = [.. File.Exists(path) ? Table(File.ReadAllLines(path), ours: false) : []];
        lines.Add("[mcp_servers." + WorkspaceConnections.AppName + "]");
        lines.Add("command = " + Quoted(command[0]));
        lines.Add("args = [" + string.Join(", ", command.Skip(1).Select(Quoted)) + "]");
        File.WriteAllLines(path, lines);
        return 0;

        static string Quoted(string value) => "\"" + value.Replace(@"\", @"\\") + "\"";
    }

    /// <summary>
    /// The stub's read side, in the shapes the real commands print. `claude mcp get deskweave`
    /// labels Command and Args on lines of their own and exits 1 for a name it does not have,
    /// because Claude Code has no --json for mcp (2.1.278). `codex mcp list --json` prints the
    /// array of servers <see cref="WorkspaceConnections.Codex"/> already reads.
    /// </summary>
    static int Shown(string path, bool claude)
    {
        if (claude)
        {
            if ((File.Exists(path) ? Read(path)["mcpServers"] as JsonObject : null)
                ?[WorkspaceConnections.AppName] is not JsonObject entry) return 1;
            Console.WriteLine(WorkspaceConnections.AppName + ":");
            Console.WriteLine("  Scope: User config (available in all your projects)");
            // The real command prints a tick or a cross here. What Deskweave reads is below it:
            // whether the entry is there and what it would run, not whether it answered just now.
            Console.WriteLine("  Status: Connected");
            Console.WriteLine("  Type: stdio");
            Console.WriteLine("  Command: " + entry["command"]?.GetValue<string>());
            Console.WriteLine("  Args: " + string.Join(' ',
                (entry["args"] as JsonArray ?? new JsonArray()).Select(a => a?.GetValue<string>() ?? "")));
            return 0;
        }
        string table = File.Exists(path) ? string.Join('\n', Table(File.ReadAllLines(path), ours: true)) : "";
        var servers = new JsonArray();
        if (table.Length > 0)
            servers.Add(new JsonObject
            {
                ["name"] = WorkspaceConnections.AppName,
                ["enabled"] = true,
                ["transport"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = WorkspaceConnections.TomlStrings(table, "command").FirstOrDefault() ?? "",
                    ["args"] = new JsonArray([.. WorkspaceConnections.TomlStrings(table, "args")
                        .Select(a => (JsonNode)JsonValue.Create(a)!)]),
                },
            });
        Console.WriteLine(servers.ToJsonString());
        return 0;
    }

    static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    static JsonObject Without(JsonObject document)
    {
        (document["mcpServers"] as JsonObject)?.Remove(WorkspaceConnections.AppName);
        return document;
    }

    static void Save(string path, JsonObject document) => File.WriteAllText(path, document.ToJsonString());

    /// <summary>A TOML file's [mcp_servers.deskweave] table, or everything but it.</summary>
    static IEnumerable<string> Table(string[] lines, bool ours)
    {
        bool inside = false;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith('[')) inside = line.Trim() == "[mcp_servers." + WorkspaceConnections.AppName + "]";
            if (inside == ours) yield return line;
        }
    }
}
