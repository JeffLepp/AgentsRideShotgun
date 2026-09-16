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
            DateTime claudeStamp = Stamp(realClaude);
            DateTime codexStamp = Stamp(realCodex);
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

            // An agent installed after consent is connected on its own, without a second prompt.
            File.Delete(codexFile);
            WorkspaceConnections.Locate = app => app == WorkspaceConnections.AgentApp.Codex ? null : Environment.ProcessPath;
            AppSettingsStore.Update(s => s with { ConnectAgents = true, FirstRunDone = true });
            WorkspaceConnections.KeepUpEvery = TimeSpan.FromSeconds(1);
            WorkspaceConnections.KeepUp();
            Thread.Sleep(1500);
            Check(!WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex),
                "An agent that is not installed yet is left alone");
            WorkspaceConnections.Locate = _ => Environment.ProcessPath;
            Check(Until(() => WorkspaceConnections.IsConnected(WorkspaceConnections.AgentApp.Codex)) && Entries(codexFile) == 1,
                "After Start, an agent installed later is connected by itself, once, with no second prompt");
            WorkspaceConnections.StopKeepingUp();

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
                && WorkspaceMcp.Scope.Contains("Use Deskweave whenever your work needs a window", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Do this without being asked", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Do not use Deskweave for anything else", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("builds, unit tests", StringComparison.Ordinal),
                "A connected agent is told to use Deskweave for windows by itself, and not for code, builds, tests or file work");

            Check(Stamp(realClaude) == claudeStamp && Stamp(realCodex) == codexStamp,
                "The owner's own Claude Code and Codex configuration is untouched by every check above");
        }
        finally
        {
            WorkspaceConnections.StopKeepingUp();
            WorkspaceConnections.Locate = wasLocate;
            WorkspaceConnections.KeepUpEvery = TimeSpan.FromMinutes(10);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", wasClaude);
            Environment.SetEnvironmentVariable("CODEX_HOME", wasCodex);
            AppSettingsStore.Update(_ => before);
        }

        static IReadOnlyList<(WorkspaceConnections.AgentApp App, string Why)> Connect() =>
            WorkspaceConnections.Connect(WorkspaceConnections.Supported).GetAwaiter().GetResult();

        static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        // Missing stays missing: a path that does not exist reads as the same sentinel both times.
        static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path)
            : Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
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
        bool claude = args.Contains("--scope");
        if (Environment.GetEnvironmentVariable(claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME") is not { Length: > 0 } home) return 2;
        string path = Path.Combine(home, claude ? ".claude.json" : "config.toml");
        if (args.Length < 3 || !args.Contains(WorkspaceConnections.AppName)) return 2;
        if (args[1] == "remove")
        {
            if (!File.Exists(path)) return 0;
            if (claude) Save(path, Without(Read(path)));
            else File.WriteAllLines(path, Trimmed(File.ReadAllLines(path)));
            return 0;
        }
        if (args[1] != "add") return 2;
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
        List<string> lines = [.. File.Exists(path) ? Trimmed(File.ReadAllLines(path)) : []];
        lines.Add("[mcp_servers." + WorkspaceConnections.AppName + "]");
        lines.Add("command = " + Quoted(command[0]));
        lines.Add("args = [" + string.Join(", ", command.Skip(1).Select(Quoted)) + "]");
        File.WriteAllLines(path, lines);
        return 0;

        static string Quoted(string value) => "\"" + value.Replace(@"\", @"\\") + "\"";
    }

    static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    static JsonObject Without(JsonObject document)
    {
        (document["mcpServers"] as JsonObject)?.Remove(WorkspaceConnections.AppName);
        return document;
    }

    static void Save(string path, JsonObject document) => File.WriteAllText(path, document.ToJsonString());

    /// <summary>A TOML file without its [mcp_servers.deskweave] table.</summary>
    static IEnumerable<string> Trimmed(string[] lines)
    {
        bool inside = false;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith('[')) inside = line.Trim() == "[mcp_servers." + WorkspaceConnections.AppName + "]";
            if (!inside) yield return line;
        }
    }
}
