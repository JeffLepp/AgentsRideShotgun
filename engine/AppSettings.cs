using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HiveMind.AgentWorkspaces;

public enum ThemeChoice { FollowWindows, Light, Dark }

/// <summary>Settings > General > Agent screens: a desktop behind the agent's windows, or a plain fill.</summary>
public enum AgentScreenLook { Full, Simple }
public enum CornerShow { ComesAndGoes, Always, Off }
public enum ScreenshotMode { KeySteps, Continuous, Off }
public enum PreviewSmoothness { Balanced, BatterySaver }

/// <summary>
/// Every choice in Settings, with the defaults the MVP spec gives it (design/MVP_SPEC.md,
/// Surfaces 4). One file for the whole app, owned by the owner: it sits in Deskweave's own local
/// folder, never in a workspace folder an agent can write to. Per-workspace records keep their
/// own values; these are the app's behavior and the defaults new workspaces start from.
/// </summary>
public sealed record AppSettings
{
    public int Schema { get; init; } = 1;

    // General
    public bool StartWithWindows { get; init; } = true;
    public ThemeChoice Theme { get; init; } = ThemeChoice.FollowWindows;
    // Check for updates and Send crash reports arrive with a release channel (MVP_SPEC, out of scope).
    /// <summary>The first-launch window was answered, by Start or by closing it. It is never shown again.</summary>
    public bool FirstRunDone { get; init; }

    /// <summary>
    /// Start was pressed on first launch. It is the owner's consent to write Deskweave into an
    /// agent's own configuration, and it keeps: a supported agent installed later is connected
    /// without asking again. Closing first launch leaves it false and nothing is ever written.
    /// </summary>
    public bool ConnectAgents { get; init; }

    /// <summary>
    /// The supported agents the owner said no to, by name: a switch left off on first launch, or
    /// one turned off in Settings later. They are never connected by themselves. Everything else
    /// supported is, once Start was pressed, including an agent installed after it.
    /// </summary>
    public IReadOnlyList<string> AgentsOff { get; init; } = [];

    // Agents
    /// <summary>As "Ctrl+Alt+P". Empty registers nothing.</summary>
    public string PauseHotkey { get; init; } = "Ctrl+Alt+P";

    // Accounts
    /// <summary>An account, as "site|name", to the one workspace id it is kept for. An account
    /// that is not listed is available to all workspaces.</summary>
    public IReadOnlyDictionary<string, string> AccountScopes { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    // Corner window
    public CornerShow CornerShow { get; init; } = CornerShow.ComesAndGoes;
    public bool CornerPinned { get; init; }
    /// <summary>Where the owner left it and how wide he grew it, in DIPs. Null until he moves or
    /// resizes it.</summary>
    public double? CornerLeft { get; init; }
    public double? CornerTop { get; init; }
    public double? CornerWidth { get; init; }

    // History & screenshots
    public ScreenshotMode Screenshots { get; init; } = ScreenshotMode.KeySteps;

    /// <summary>How an agent's screen looks behind its windows (<see cref="WorkspaceWall"/>).</summary>
    public AgentScreenLook AgentScreen { get; init; } = AgentScreenLook.Full;

    /// <summary>Whatever was on disk, coerced to the choices Settings actually offers.</summary>
    internal AppSettings Sane()
    {
        var d = new AppSettings();
        static T Known<T>(T value, T fallback) where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;
        // The Pause shortcut is rebindable, never removable, so a broken one goes back to its default.
        static string Key(string? text, string fallback) => WorkspaceHotkey.Parse(text, out _, out _) ? text!.Trim() : fallback;
        static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
        return this with
        {
            Schema = 1,
            Theme = Known(Theme, d.Theme),
            PauseHotkey = Key(PauseHotkey, d.PauseHotkey),
            // A copy nobody else holds, so the only way to change a scope is Update.
            AccountScopes = new ReadOnlyDictionary<string, string>((AccountScopes ?? d.AccountScopes)
                .Where(scope => scope.Key.Length > 0 && !string.IsNullOrEmpty(scope.Value))
                .ToDictionary(scope => scope.Key, scope => scope.Value, StringComparer.Ordinal)),
            CornerShow = Known(CornerShow, d.CornerShow),
            CornerLeft = Finite(CornerLeft),
            CornerTop = Finite(CornerTop),
            CornerWidth = Finite(CornerWidth) is { } width ? Math.Clamp(width, 220, 1600) : null,
            // Whatever is on disk: only names this build still knows, each once.
            AgentsOff = [.. (AgentsOff ?? d.AgentsOff).Where(a => Enum.TryParse<WorkspaceConnections.AgentApp>(a, out _))
                .Distinct(StringComparer.Ordinal)],
            Screenshots = Known(Screenshots, d.Screenshots),
            AgentScreen = Known(AgentScreen, d.AgentScreen),
        };
    }
}

/// <summary>
/// Reads and writes <see cref="AppSettings"/>. Read once, then kept; every change goes through
/// <see cref="Update"/>, which saves and tells every listener, so a switch in Settings and the
/// behavior behind it can never disagree.
/// </summary>
public static class AppSettingsStore
{
    static readonly Lock Gate = new();
    // Changes are announced one at a time, in the order they were made. Listeners must not wait on
    // another thread that could itself be changing a setting; UI listeners use BeginInvoke.
    static readonly object Announcing = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static AppSettings? _current;
    static long _version;
    static string? _testFile;

    internal static string File => _testFile ?? HiveMind.Product.ProductContext.Local("settings.json");

    /// <summary>After every change, on the thread that made it. UI listeners dispatch to their own.</summary>
    public static event Action<AppSettings>? Changed;

    public static AppSettings Current { get { lock (Gate) return _current ??= Read(); } }

    /// <summary>Applies a change, saves it and raises <see cref="Changed"/>. False when it could
    /// not be written; the change still holds for this session.</summary>
    public static bool Update(Func<AppSettings, AppSettings> change)
    {
        lock (Announcing)
        {
            AppSettings next;
            bool saved;
            long mine;
            lock (Gate)
            {
                next = change(_current ??= Read()).Sane();
                _current = next;
                mine = ++_version;
                saved = Write(next);
            }
            // One listener failing must not keep the change from the others; it is already saved.
            foreach (Action<AppSettings> listener in Changed?.GetInvocationList().Cast<Action<AppSettings>>() ?? [])
            {
                // A listener changed a setting itself: that newer change was already announced to
                // everyone, so the rest of this older announcement would only repeat or rewind it.
                lock (Gate) if (_version != mine) break;
                try { listener(next); }
                catch (Exception failure) { Debug.WriteLine("A settings listener failed: " + failure); }
            }
            return saved;
        }
    }

    static AppSettings Read()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new AppSettings().Sane();
            if (new FileInfo(File).Length > MaxBytes) return Unusable();
            AppSettings? read = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File), Json);
            return read is { Schema: 1 } ? read.Sane() : new AppSettings().Sane();
        }
        catch (JsonException)
        {
            return Unusable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable right now, which says nothing about what is in it. Leave it alone.
            return new AppSettings().Sane();
        }
    }

    /// <summary>
    /// A settings file that exists and cannot be used - truncated by a power loss, or past the size
    /// both sides agree on. Starting again from the defaults silently takes away first run, the
    /// connected agents, the theme, the hotkey and the corner placement with no evidence left, so the
    /// file is kept next door as settings.bad.json first. A failed rename changes nothing and is not
    /// allowed to escape: defaults either way.
    /// </summary>
    static AppSettings Unusable()
    {
        try
        {
            string? folder = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(folder))
                System.IO.File.Move(File, Path.Combine(folder, "settings.bad.json"), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { }
        return new AppSettings().Sane();
    }

    // The same bound both ways, so the store never writes a file it would refuse to read.
    const int MaxBytes = 1_048_576;

    // UTF-8 with no byte order mark: the same bytes File.WriteAllText put here before.
    static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static bool Write(AppSettings settings)
    {
        byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(settings, Json));
        if (bytes.Length > MaxBytes) return false;
        string temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            // The rename is atomic, but the contents are not on the disk yet when it runs: Windows can
            // record the rename while the bytes are still in the file cache, and a power loss then
            // leaves a settings.json that is named right and empty - the file Read has to move aside as
            // unusable. Flushing to the device first means the disk holds either the old file or the
            // whole new one.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            System.IO.File.Move(temporary, File, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Points the store at a probe's own file, and forgets what it had read.</summary>
    internal static IDisposable UseFileForTests(string path)
    {
        lock (Gate)
        {
            if (_testFile is not null) throw new InvalidOperationException("A settings override is already active.");
            _testFile = Path.GetFullPath(path);
            _current = null;
        }
        return new Scope();
    }

    sealed class Scope : IDisposable
    {
        public void Dispose() { lock (Gate) { _testFile = null; _current = null; } }
    }
}
