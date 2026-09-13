using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HiveMind.AgentWorkspaces;

public enum ThemeChoice { FollowWindows, Light, Dark }
public enum CloseChoice { KeepRunning, Quit }
public enum AgentPlacement { OnePerProject, OneShared, AskMe }
public enum ControlMode { WorkAlongside, TakeTurns, FullStop }
public enum DesktopOpen { AskFirst, Always, Never }
public enum BrowserChoice { Auto, Chrome, Edge }
public enum CornerShow { ComesAndGoes, Always, Off }
public enum CornerPosition { BottomRight, BottomLeft, TopRight, WhereILeaveIt }
public enum CornerSize { Small, Medium, Large }
public enum CornerClick { UseItHere, OpenWorkspace }
public enum ScreenshotMode { KeySteps, Continuous, Off }
public enum PreviewSmoothness { Balanced, Smooth, BatterySaver }

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
    public CloseChoice CloseButton { get; init; } = CloseChoice.KeepRunning;
    // Check for updates and Send crash reports arrive with a release channel (MVP_SPEC, out of scope).
    /// <summary>The first-launch window was answered, with Start or Skip.</summary>
    public bool FirstRunDone { get; init; }

    // Agents
    public bool RemindAgents { get; init; }
    public AgentPlacement AgentsGo { get; init; } = AgentPlacement.OnePerProject;
    /// <summary>0 is Auto: the engine's one per 3 GB of memory, 2 to 10. Otherwise 2 to 10.</summary>
    public int RunningAtOnce { get; init; }
    /// <summary>0 is Never.</summary>
    public int SleepMinutes { get; init; } = 10;

    // Control
    public ControlMode Control { get; init; } = ControlMode.TakeTurns;
    public int CarryOnSeconds { get; init; } = 20;
    public DesktopOpen DesktopRequests { get; init; } = DesktopOpen.AskFirst;
    /// <summary>As "Ctrl+Alt+P". Empty registers nothing.</summary>
    public string PauseHotkey { get; init; } = "Ctrl+Alt+P";
    public string CornerHotkey { get; init; } = "Ctrl+Alt+D";

    // Browser & accounts
    public bool ShareSignIns { get; init; } = true;
    public BrowserChoice Browser { get; init; } = BrowserChoice.Auto;
    public bool OpenBrowserEarly { get; init; } = true;
    /// <summary>An account, as "site|name", to the one workspace id it is kept for. An account
    /// that is not listed is available to all workspaces.</summary>
    public IReadOnlyDictionary<string, string> AccountScopes { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    // Corner window
    public CornerShow CornerShow { get; init; } = CornerShow.ComesAndGoes;
    public CornerPosition CornerPosition { get; init; } = CornerPosition.BottomRight;
    public CornerSize CornerSize { get; init; } = CornerSize.Small;
    public int FadeAfterSeconds { get; init; } = 5;
    public CornerClick CornerClick { get; init; } = CornerClick.UseItHere;
    public bool CornerPinned { get; init; }
    /// <summary>Where the owner left it and how wide he grew it, in DIPs. Null until he moves or
    /// resizes it.</summary>
    public double? CornerLeft { get; init; }
    public double? CornerTop { get; init; }
    public double? CornerWidth { get; init; }

    // Notifications
    public bool NotifyNeedsYou { get; init; } = true;
    public bool NotifyTestFinished { get; init; }
    public bool NotifyOnlyWhenCornerCannot { get; init; } = true;
    public bool NotifySound { get; init; }
    public bool FollowDoNotDisturb { get; init; } = true;

    // History & screenshots
    public ScreenshotMode Screenshots { get; init; } = ScreenshotMode.KeySteps;
    public int ContinuousSeconds { get; init; } = 2;
    /// <summary>0 is Forever.</summary>
    public int KeepHistoryDays { get; init; } = 7;

    // Performance
    public WorkspacePower NewWorkspaceSpeed { get; init; } = WorkspacePower.Fast;
    public PreviewSmoothness Smoothness { get; init; } = PreviewSmoothness.Balanced;
    public bool PausePreviewsOnBattery { get; init; } = true;

    // Privacy & safety
    public bool PauseAfterWebPage { get; init; } = true;
    public WorkspaceMode Restrictions { get; init; } = WorkspaceMode.Free;

    /// <summary>Whatever was on disk, coerced to the choices Settings actually offers.</summary>
    internal AppSettings Sane()
    {
        var d = new AppSettings();
        static T Known<T>(T value, T fallback) where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;
        static int Pick(int value, int fallback, params int[] allowed) => allowed.Contains(value) ? value : fallback;
        // Both shortcuts are rebindable, never removable, so a broken one goes back to its default.
        static string Key(string? text, string fallback) => WorkspaceHotkey.Parse(text, out _, out _) ? text!.Trim() : fallback;
        static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
        return this with
        {
            Schema = 1,
            Theme = Known(Theme, d.Theme),
            CloseButton = Known(CloseButton, d.CloseButton),
            AgentsGo = Known(AgentsGo, d.AgentsGo),
            RunningAtOnce = RunningAtOnce is 0 or (>= 2 and <= 10) ? RunningAtOnce : 0,
            SleepMinutes = Pick(SleepMinutes, d.SleepMinutes, 0, 5, 10, 30),
            Control = Known(Control, d.Control),
            CarryOnSeconds = Pick(CarryOnSeconds, d.CarryOnSeconds, 10, 20, 30, 60),
            DesktopRequests = Known(DesktopRequests, d.DesktopRequests),
            PauseHotkey = Key(PauseHotkey, d.PauseHotkey),
            CornerHotkey = Key(CornerHotkey, d.CornerHotkey),
            Browser = Known(Browser, d.Browser),
            // A copy nobody else holds, so the only way to change a scope is Update.
            AccountScopes = new ReadOnlyDictionary<string, string>((AccountScopes ?? d.AccountScopes)
                .Where(scope => scope.Key.Length > 0 && !string.IsNullOrEmpty(scope.Value))
                .ToDictionary(scope => scope.Key, scope => scope.Value, StringComparer.Ordinal)),
            CornerShow = Known(CornerShow, d.CornerShow),
            CornerPosition = Known(CornerPosition, d.CornerPosition),
            CornerSize = Known(CornerSize, d.CornerSize),
            FadeAfterSeconds = Pick(FadeAfterSeconds, d.FadeAfterSeconds, 3, 5, 10),
            CornerClick = Known(CornerClick, d.CornerClick),
            CornerLeft = Finite(CornerLeft),
            CornerTop = Finite(CornerTop),
            CornerWidth = Finite(CornerWidth) is { } width ? Math.Clamp(width, 220, 1600) : null,
            Screenshots = Known(Screenshots, d.Screenshots),
            ContinuousSeconds = Pick(ContinuousSeconds, d.ContinuousSeconds, 1, 2, 5),
            KeepHistoryDays = Pick(KeepHistoryDays, d.KeepHistoryDays, 0, 1, 7, 30),
            NewWorkspaceSpeed = Known(NewWorkspaceSpeed, d.NewWorkspaceSpeed),
            Smoothness = Known(Smoothness, d.Smoothness),
            Restrictions = Known(Restrictions, d.Restrictions),
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
            if (!System.IO.File.Exists(File) || new FileInfo(File).Length > MaxBytes) return new AppSettings().Sane();
            AppSettings? read = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File), Json);
            return read is { Schema: 1 } ? read.Sane() : new AppSettings().Sane();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings().Sane();
        }
    }

    // The same bound both ways, so the store never writes a file it would refuse to read.
    const int MaxBytes = 1_048_576;

    static bool Write(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) return false;
        string temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(temporary, json);
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
