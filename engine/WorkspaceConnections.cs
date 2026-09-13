using System.Diagnostics;
using System.IO;
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

    internal static object AppConfiguration => new
    {
        mcpServers = new Dictionary<string, object>
        {
            [AppName] = new { type = "stdio", command = Bridge, args = new[] { "--workspace", WorkspaceAccessStore.RouterTicket } },
        },
    };

    internal static bool IsInstalled(AgentApp app) => (app == AgentApp.ClaudeCode ? WorkspaceAgent.FindCli() : FindCodex()) is not null;

    /// <summary>Whether the app's own configuration has Deskweave in it. Reads the file, never writes it.</summary>
    internal static bool IsConnected(AgentApp app)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (app == AgentApp.Codex)
            {
                string codex = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
                    ? home : Path.Combine(profile, ".codex"), "config.toml");
                return File.Exists(codex) && File.ReadLines(codex)
                    .Any(line => line.Trim() is "[mcp_servers.deskweave]" or "[mcp_servers.\"deskweave\"]");
            }
            string claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } folder
                ? Path.Combine(folder, ".claude.json") : Path.Combine(profile, ".claude.json");
            if (!File.Exists(claude)) return false;
            using var stream = File.OpenRead(claude);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                && servers.ValueKind == JsonValueKind.Object && servers.TryGetProperty(AppName, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>
    /// Adds or removes Deskweave in one agent app's configuration, through that app's own command.
    /// Returns why it could not, or null. No model runs and nothing else in the configuration moves.
    /// </summary>
    internal static async Task<string?> SetConnected(AgentApp app, bool connect, CancellationToken cancel = default)
    {
        string name = app == AgentApp.ClaudeCode ? "Claude Code" : "Codex";
        string? cli = app == AgentApp.ClaudeCode ? WorkspaceAgent.FindCli() : FindCodex();
        if (cli is null) return name + " isn't installed on this PC.";
        if (connect && !File.Exists(Bridge)) return "Deskweave's workspace bridge is missing. Reinstall Deskweave.";
        string[] scope = app == AgentApp.ClaudeCode ? ["--scope", "user"] : [];
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        // Replace, never duplicate: an entry from an older Deskweave comes out first.
        if (IsConnected(app)) await Run(cli, ["mcp", "remove", .. scope, AppName], bound.Token, null).ConfigureAwait(false);
        if (!connect) return IsConnected(app) ? $"{name} kept its Deskweave entry. Remove it in {name}'s MCP settings." : null;
        var added = await Run(cli, ["mcp", "add", .. scope, AppName, "--", Bridge, "--workspace", WorkspaceAccessStore.RouterTicket],
            bound.Token, null).ConfigureAwait(false);
        return added.Code == 0 ? null : $"{name} did not accept the connection. Nothing else was changed.";
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
            if (IsConnected(app)) await SetConnected(app, false).ConfigureAwait(false);
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
