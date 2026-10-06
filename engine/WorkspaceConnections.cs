using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Deskweave.AgentWorkspaces;

/// <summary>Explicit-click integration using the provider's configuration command, not a TOML rewrite.</summary>
internal static class WorkspaceConnections
{
    internal static string Bridge => Path.Combine(Path.GetDirectoryName(typeof(WorkspaceMcp).Assembly.Location)!,
        "Bridge", "ARS.WorkspaceBridge.exe");
    internal static string Name(string id) => "ars_workspace_" + id;
    internal static object Configuration(string id) => new
    {
        mcpServers = new Dictionary<string, object>
        {
            [Name(id)] = new { type = "stdio", command = Bridge, args = new[] { "--workspace", WorkspaceAccessStore.Connection(id) } },
        },
    };

    /// <summary>The single entry an agent app gets. Every workspace is reached through it.</summary>
    internal const string AppName = "ars";

    /// <summary>The entry from before ARS was renamed. It only ever comes out.</summary>
    internal const string FormerAppName = "deskweave";

    internal enum AgentApp { ClaudeCode, Codex }

    /// <summary>The agent apps ARS connects by itself, in the order first launch lists them.</summary>
    internal static readonly AgentApp[] Supported = Enum.GetValues<AgentApp>();

    internal static string DisplayName(AgentApp app) => app == AgentApp.ClaudeCode ? "Claude Code" : "Codex";

    /// <summary>A configuration target, never a provider login or an inferred account identity.</summary>
    internal sealed record AgentProfile(AgentApp App, string Name, string? Root, string Configuration, string? Launcher = null);

    // A seam keeps numbered owner profiles out of isolated connection probes.
    internal static Func<AgentApp, IReadOnlyList<AgentProfile>> Profiles = FindProfiles;

    internal static (int Connected, int Total) ProfileCounts(AgentApp app)
    {
        IReadOnlyList<AgentProfile> profiles = Profiles(app);
        return (profiles.Count(profile => ReadEntry(profile) == Entry.Current), profiles.Count);
    }

    static IReadOnlyList<AgentProfile> FindProfiles(AgentApp app) => FindProfiles(app,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable(app == AgentApp.ClaudeCode ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"));

    internal static IReadOnlyList<AgentProfile> FindProfiles(AgentApp app, string user, string? effective)
    {
        bool claude = app == AgentApp.ClaudeCode;
        string folder = claude ? ".claude" : ".codex";
        string file = claude ? ".claude.json" : "config.toml";
        List<AgentProfile> profiles = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? root, string configuration, string? launcher = null)
        {
            try
            {
                configuration = Path.GetFullPath(configuration);
                if (seen.Add(configuration)) profiles.Add(new(app, name, root is null ? null : Path.GetFullPath(root), configuration, launcher));
                else if (launcher is not null)
                {
                    int index = profiles.FindIndex(profile => string.Equals(profile.Configuration, configuration, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) profiles[index] = profiles[index] with { Launcher = launcher };
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        Add("Default", null, claude ? Path.Combine(user, file) : Path.Combine(user, folder, file));
        if (!string.IsNullOrWhiteSpace(effective))
            Add("Current profile", effective, Path.Combine(effective, file));
        // Only the named local convention is discovered. No recursion, directory sweep, auth
        // files or new empty profiles: custom locations remain the owner's explicit MCP setup.
        for (int number = 1; number <= 9; number++)
        {
            string root = Path.Combine(user, folder + number);
            string configuration = Path.Combine(root, file);
            if (File.Exists(configuration)) Add("Profile " + number, root, configuration, (claude ? "claude" : "codex") + number);
        }
        return profiles;
    }

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

    /// <summary>Whether the app's own configuration has a working ARS entry in it. A field for
    /// the same reason <see cref="Locate"/> is one: first launch, Settings and <see cref="KeepUp"/> all
    /// read through it, so a probe answers for every one of them at once. Only the intended
    /// profiles' own files can establish a connection; a wrapper's other root cannot vouch for it.</summary>
    internal static Func<AgentApp, bool> IsConnected = app => ProfileCounts(app) is var counts && counts.Total > 0 && counts.Connected == counts.Total;

    /// <summary>Whether the app's configuration has anything under ARS's name, working or not.</summary>
    internal static bool HasEntry(AgentApp app) => Profiles(app).Any(profile => ReadEntry(profile) != Entry.None);

    /// <summary>
    /// What an agent app's configuration holds under ARS's name. Stale is an entry that runs
    /// some other bridge or ticket: an older install, a moved folder, a test build. Counting one as
    /// connected left the agent pointed at a pipe nobody serves, with nothing ever replacing it.
    /// </summary>
    internal enum Entry { None, Current, Stale, Disabled, Unreadable }

    static Entry ReadEntry(AgentProfile target, bool ignoreDisabled = false, string name = AppName)
    {
        try
        {
            if (target.App == AgentApp.Codex)
            {
                string codex = target.Configuration;
                if (!File.Exists(codex)) return Entry.None;
                var table = new StringBuilder();
                bool inside = false, found = false;
                foreach (string line in File.ReadLines(codex))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is >= 0 and var end)
                    {
                        string header = trimmed[..(end + 1)];
                        inside = header == $"[mcp_servers.{name}]" || header == $"[mcp_servers.\"{name}\"]" || header == $"[mcp_servers.'{name}']";
                        found |= inside;
                        continue;
                    }
                    if (inside) table.AppendLine(line);
                }
                if (!found) return Entry.None;
                string text = table.ToString();
                // A provider-side off switch is owner intent, even if this bridge has moved.
                // Only this table's boolean counts: an env key or another server's flag does not.
                if (!ignoreDisabled && System.Text.RegularExpressions.Regex.IsMatch(text,
                    "(?m)^[ \\t]*(?:enabled|\"enabled\"|'enabled')[ \\t]*=[ \\t]*false[ \\t]*(?:#[^\\r\\n]*)?\\r?$"))
                    return Entry.Disabled;
                return Runs(TomlStrings(text, "command").FirstOrDefault(), TomlStrings(text, "args")) ? Entry.Current : Entry.Stale;
            }
            string claude = target.Configuration;
            if (!File.Exists(claude)) return Entry.None;
            using var stream = File.OpenRead(claude);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Entry.Unreadable;
            if (!document.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                || servers.ValueKind != JsonValueKind.Object || !servers.TryGetProperty(name, out JsonElement entry)) return Entry.None;
            string? command = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("command", out JsonElement c)
                && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            List<string> args = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("args", out JsonElement a)
                && a.ValueKind == JsonValueKind.Array
                ? [.. a.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.String ? arg.GetString() ?? "" : "")] : [];
            return Runs(command, args) ? Entry.Current : Entry.Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return Entry.Unreadable; }
    }

    /// <summary>Whether an entry runs this ARS's bridge against its one router ticket.</summary>
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
    /// Adds or removes ARS in one agent app's configuration, through that app's own command.
    /// Returns why it could not, or null. The one call that writes an agent's configuration, and a
    /// field so a probe stands in for every caller at once: first launch, Settings and the keep-up
    /// loop all come through here. No model runs and nothing else in the configuration moves.
    /// </summary>
    internal static Func<AgentApp, bool, CancellationToken, Task<string?>> SetConnected = Change;
    static readonly SemaphoreSlim[] Changes = [new(1, 1), new(1, 1)];
    static readonly AsyncLocal<bool> AutomaticChange = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<AgentApp, string> Failures = new();
    internal static string? LastFailure(AgentApp app) => Failures.GetValueOrDefault(app);

    static string? RememberResult(AgentApp app, string? why)
    {
        if (why is null) Failures.TryRemove(app, out _);
        else { Failures[app] = why; Trace.TraceWarning("ARS connection: {0}", why); }
        return why;
    }

    static async Task<string?> Change(AgentApp app, bool connect, CancellationToken cancel)
    {
        string? cli = Locate(app);
        if (cli is null) return RememberResult(app, DisplayName(app) + " isn't installed on this PC.");
        if (connect && !File.Exists(Bridge)) return RememberResult(app, "ARS's workspace bridge is missing. Reinstall ARS.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        bool automatic = AutomaticChange.Value;
        bool Permitted() => !connect || !TurnedOff(app) && (!automatic || AppSettingsStore.Current.ConnectAgents);
        List<string> failures = [];
        SemaphoreSlim gate = Changes[(int)app];
        bool entered = false;
        try
        {
            await gate.WaitAsync(bound.Token).ConfigureAwait(false);
            entered = true;
            foreach (AgentProfile profile in Profiles(app))
            {
                if (!Permitted()) break;
                if (automatic && ReadEntry(profile) is var now
                    && (now is Entry.Current or Entry.Disabled || now == Entry.None && Recorded(profile))) continue;
                try
                {
                    // A numbered launcher can honor its root when the default wrapper clears it.
                    string command = ProfileCommand(profile, cli);
                    if (await ChangeProfile(profile, command, connect, bound.Token, Permitted).ConfigureAwait(false) is { } why) failures.Add(why);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { failures.Add(DisplayName(app) + " (" + profile.Name + ") could not update its connection: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        { failures.Add(DisplayName(app) + " connection changes timed out. Try again in Settings."); }
        finally { if (entered) gate.Release(); }
        return RememberResult(app, failures.Count == 0 ? null : string.Join(" ", failures));
    }

    static string ProfileCommand(AgentProfile profile, string fallback)
    {
        // A test's Locate seam is authoritative. Real profile writes prefer the provider's native
        // CLI because user launcher wrappers can clear/replace scoped configuration variables.
        if (_locate != (Func<AgentApp, string?>)Discover) return fallback;
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? native = profile.App == AgentApp.ClaudeCode
            ? Existing(Path.Combine(user, ".local", "bin", "claude.exe"))
            : Existing(Path.Combine(user, ".local", "bin", "codex.exe")) ?? Packaged();
        return native ?? (profile.Launcher is { } alias ? Command(alias) : null) ?? fallback;
    }

    static async Task<string?> ChangeProfile(AgentProfile profile, string cli, bool connect, CancellationToken cancel, Func<bool>? permitted = null)
    {
        string name = DisplayName(profile.App) + " (" + profile.Name + ")";
        string[] scope = profile.App == AgentApp.ClaudeCode ? ["--scope", "user"] : [];
        // Replace, never duplicate: an entry from an older ARS, working or stale, comes out first.
        // A stale one left in place would make the add below refuse the name.
        Entry entry = ReadEntry(profile);
        if (entry == Entry.Unreadable) return $"{name} configuration could not be read. No change was requested.";
        if (permitted?.Invoke() == false) return null;
        // The entry from before the rename runs a bridge that is gone, so it comes out either way.
        if (ReadEntry(profile, true, FormerAppName) is not Entry.None and not Entry.Unreadable)
        {
            await Run(cli, ["mcp", "remove", .. scope, FormerAppName], cancel, profile.Root, profile.App, true).ConfigureAwait(false);
            if (ReadEntry(profile, true, FormerAppName) != Entry.None)
                return $"{name} kept its old Deskweave entry. Remove it in the provider's MCP settings.";
        }
        bool had = entry != Entry.None;
        if (had)
        {
            await Run(cli, ["mcp", "remove", .. scope, AppName], cancel, profile.Root, profile.App, true).ConfigureAwait(false);
            if (ReadEntry(profile) != Entry.None)
                return $"{name} kept its ARS entry. Its launcher may use a different configuration profile; remove it in the provider's MCP settings.";
        }
        if (!connect)
        {
            Unrecord(profile);
            return null;
        }
        if (permitted?.Invoke() == false) return null;
        await Run(cli, ["mcp", "add", .. scope, AppName, "--", Bridge, "--workspace", WorkspaceAccessStore.RouterTicket],
            cancel, profile.Root, profile.App, true).ConfigureAwait(false);
        // Never count a write to some other account/profile as this target's connection. This
        // also removes the old provider-wide vouch that could survive removal or a root switch.
        if (ReadEntry(profile) == Entry.Current)
        {
            Record(profile);
            return null;
        }
        return had
            // The old entry came out for the replacement and the new one did not go in. Saying
            // nothing changed would be a lie, and it would hide a connection that is gone.
            ? $"{name} did not accept the connection, and its earlier ARS entry came out with it. Connect it again in Settings."
            : $"{name} did not accept the connection in the intended profile. Its launcher may use another configuration profile. No unrelated entries were requested to change.";
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
    /// remembering is an agent they said no to, and nothing connects that one behind their back.
    /// </summary>
    internal static void Remember(AgentApp app, bool on) => AppSettingsStore.Update(s => s with
    {
        AgentsOff = on ? [.. s.AgentsOff.Where(off => off != app.ToString())]
            : s.AgentsOff.Contains(app.ToString()) ? s.AgentsOff : [.. s.AgentsOff, app.ToString()],
    });

    /// <summary>Whether the owner turned this agent off, on first launch or in Settings.</summary>
    internal static bool TurnedOff(AgentApp app) => AppSettingsStore.Current.AgentsOff.Contains(app.ToString());

    /// <summary>An agent with a repairable profile. Provider-side disabled entries stay off, and so
    /// does a profile ARS connected whose entry the owner took out themselves.</summary>
    internal static bool Missing(AgentApp app) => IsInstalled(app) && !IsConnected(app) && !TurnedOff(app)
        && Profiles(app).Any(profile => ReadEntry(profile) switch
        {
            Entry.Stale or Entry.Unreadable => true,
            Entry.None => !Recorded(profile),
            _ => false,
        });

    // The ledger of profiles ARS wrote itself into: AppSettings.ConnectedProfiles.
    static string Key(AgentProfile profile) => profile.App + "|" + profile.Configuration + "|" + profile.Root;
    static bool SameProfile(string key, AgentProfile profile) =>
        key.StartsWith(profile.App + "|" + profile.Configuration + "|", StringComparison.OrdinalIgnoreCase);
    static bool Recorded(AgentProfile profile) => AppSettingsStore.Current.ConnectedProfiles.Any(key => SameProfile(key, profile));

    static void Record(AgentProfile profile)
    {
        if (!Recorded(profile)) AppSettingsStore.Update(s => s with { ConnectedProfiles = [.. s.ConnectedProfiles, Key(profile)] });
    }

    static void Unrecord(AgentProfile profile)
    {
        if (Recorded(profile))
            AppSettingsStore.Update(s => s with { ConnectedProfiles = [.. s.ConnectedProfiles.Where(key => !SameProfile(key, profile))] });
    }

    static IEnumerable<AgentProfile> RecordedProfiles() => AppSettingsStore.Current.ConnectedProfiles
        .Select(key => key.Split('|'))
        .Where(part => part.Length == 3 && Enum.TryParse<AgentApp>(part[0], out _))
        .Select(part => new AgentProfile(Enum.Parse<AgentApp>(part[0]), "Earlier profile", part[2].Length == 0 ? null : part[2], part[1]));

    /// <summary>How often a PC with a supported agent still missing is looked at again.</summary>
    internal static TimeSpan KeepUpEvery = TimeSpan.FromMinutes(10);

    static CancellationTokenSource? _keepingUp;

    /// <summary>
    /// The owner pressed Start once, so an agent installed later is connected without being asked
    /// again. Keeps its inexpensive ten-minute check after existing profiles
    /// are connected, so another local profile created later is found too. Off the UI thread.
    /// Does nothing without consent, and never touches an agent the owner turned off.
    /// </summary>
    internal static void KeepUp()
    {
        if (_keepingUp is not null || !AppSettingsStore.Current.ConnectAgents) return;
        var stop = _keepingUp = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            AutomaticChange.Value = true;
            using var clock = new PeriodicTimer(KeepUpEvery);
            try
            {
                do
                {
                    // Both answers can change while this waits: consent can be withdrawn, and an
                    // agent can be turned off in Settings. Either one is read again every pass.
                    if (!AppSettingsStore.Current.ConnectAgents) continue;
                    foreach (AgentApp app in Supported)
                    {
                        try
                        {
                            // An entry an older ARS wrote before the ledger existed counts as written by
                            // ARS, so the owner taking it out later is respected too.
                            foreach (AgentProfile profile in Profiles(app))
                                if (ReadEntry(profile) is Entry.Current or Entry.Stale or Entry.Disabled) Record(profile);
                            if (!AppSettingsStore.Current.ConnectAgents || !Missing(app)) continue;
                            await SetConnected(app, true, stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        { RememberResult(app, DisplayName(app) + " connection could not be updated: " + ex.Message); }
                    }
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

    static async Task<(int Code, string Output)> Run(string cli, string[] arguments, CancellationToken cancel, string? profileRoot,
        AgentApp app = AgentApp.Codex, bool targetProfile = false)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        string homeVariable = app == AgentApp.ClaudeCode ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";
        if (profileRoot is not null) start.Environment[homeVariable] = profileRoot;
        else if (targetProfile) start.Environment.Remove(homeVariable);
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

    /// <summary>The file names every ARS bridge has had, before and after the rename.</summary>
    static readonly string[] BridgeNames = ["ARS.WorkspaceBridge.exe", "Deskweave.WorkspaceBridge.exe"];

    /// <summary>
    /// The servers in one profile that are ARS's to remove: under a name ARS has used (ars, deskweave,
    /// or a per-workspace ars_workspace_/deskweave_workspace_ one), running a file named like an ARS
    /// bridge that is either this install's or no longer there. A live copy elsewhere (a dev build),
    /// or anything else under a similar name, is left alone.
    /// </summary>
    internal static List<string> OwnedNames(AgentProfile profile) => [.. Servers(profile)
        .Where(server => (server.Name is AppName or FormerAppName
                || server.Name.StartsWith("ars_workspace_", StringComparison.Ordinal)
                || server.Name.StartsWith("deskweave_workspace_", StringComparison.Ordinal))
            && server.Command is { } command
            && BridgeNames.Contains(Path.GetFileName(command), StringComparer.OrdinalIgnoreCase)
            && (SamePath(command, Bridge) || !File.Exists(command)))
        .Select(server => server.Name)];

    /// <summary>Every MCP server one profile's configuration names, with the command it runs.</summary>
    static List<(string Name, string? Command)> Servers(AgentProfile profile)
    {
        List<(string, string?)> found = [];
        if (!File.Exists(profile.Configuration)) return found;
        if (profile.App == AgentApp.Codex)
        {
            string? name = null;
            var table = new StringBuilder();
            void Close()
            {
                if (name is not null) found.Add((name, TomlStrings(table.ToString(), "command").FirstOrDefault()));
                table.Clear();
            }
            foreach (string line in File.ReadLines(profile.Configuration))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is > 0 and var end)
                {
                    Close();
                    string header = trimmed[..(end + 1)];
                    string inner = header.StartsWith("[mcp_servers.", StringComparison.Ordinal) ? header[13..^1].Trim() : "";
                    // [mcp_servers.ars.env] is a sub-table of ars, not a server of its own.
                    name = inner.Length > 1 && inner[0] is '"' or '\'' && inner.IndexOf(inner[0], 1) == inner.Length - 1 ? inner[1..^1]
                        : inner.Length > 0 && !inner.Contains('.') ? inner : null;
                    continue;
                }
                if (name is not null) table.AppendLine(line);
            }
            Close();
            return found;
        }
        using var stream = File.OpenRead(profile.Configuration);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("mcpServers", out JsonElement servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty server in servers.EnumerateObject())
                found.Add((server.Name, server.Value.ValueKind == JsonValueKind.Object && server.Value.TryGetProperty("command", out JsonElement c)
                    && c.ValueKind == JsonValueKind.String ? c.GetString() : null));
        return found;
    }

    /// <summary>
    /// Uninstall: takes ARS's servers out of every profile it can find - the ones discovery looks at
    /// and the ones its ledger says it wrote to - through each agent's own command, and answers with
    /// one line per profile that still has one, so nothing is left behind silently. Unrelated servers
    /// and the owner's other settings are never touched. All profiles at once, inside 20 s: the
    /// uninstaller ends its hook at 30, and one agent that hangs must not cost another its cleanup.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RemoveOwnedConnections()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        AgentProfile[] targets = [.. Supported.SelectMany(app => Profiles(app)).Concat(RecordedProfiles())
            .DistinctBy(profile => profile.App + "|" + profile.Configuration.ToUpperInvariant())];
        string?[] left = await Task.WhenAll(targets.Select(async profile =>
        {
            string label = DisplayName(profile.App) + " (" + profile.Name + ")";
            try
            {
                if (OwnedNames(profile) is not { Count: > 0 } names) { Unrecord(profile); return null; }
                if (Locate(profile.App) is { } cli)
                {
                    string[] scope = profile.App == AgentApp.ClaudeCode ? ["--scope", "user"] : [];
                    foreach (string name in names)
                        await Run(ProfileCommand(profile, cli), ["mcp", "remove", .. scope, name], budget.Token,
                            profile.Root, profile.App, true).ConfigureAwait(false);
                }
                if (OwnedNames(profile) is not { Count: > 0 } still) { Unrecord(profile); return null; }
                return $"{label} still has {string.Join(", ", still)} in {profile.Configuration}. Remove it in the provider's MCP settings.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return $"{label}: {profile.Configuration} could not be cleaned ({ex.GetType().Name}). Check it for ARS entries."; }
        })).ConfigureAwait(false);
        List<string> failures = [.. left.OfType<string>()];
        foreach (string failure in failures) Trace.TraceWarning("ARS uninstall: {0}", failure);
        return failures;
    }

    /// <summary>
    /// Where an agent's command is, looked for once. <see cref="IsInstalled"/> is read while a
    /// window is being built - first launch's card, and twice over on every Settings render - and
    /// the answer walks PATH, the registry and a list of folders, so it is paid for one time. A
    /// command that was found does not move while ARS is running.
    ///
    /// Finding nothing is remembered only briefly, for the reason <see cref="WorkspaceBrowser"/>
    /// forgets a missing browser: ARS starts with Windows and sits in the tray while the
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
    /// Claude Code's own command. Four hardcoded paths and no PATH lookup would miss a Node under
    /// nvm-windows, fnm or Volta, anyone who had run `npm config set prefix`, a machine-wide
    /// install, and the winget, scoop and chocolatey shims, and every one of them would be told
    /// "No supported agent found on this PC" - with no override anywhere in Settings, so the
    /// product would do nothing for them. The four paths are still asked, last, so no PC they
    /// cover is worse off.
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
    /// PATH as it is now is read here too. ARS starts with Windows and stays in the tray, so
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
    /// the prefix the owner set for themselves, which covers `npm config set prefix` and what
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
