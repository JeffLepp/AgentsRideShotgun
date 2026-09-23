using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

namespace Deskweave;

/// <summary>
/// Settings rows that are built and wired to the store but stay hidden until the behavior behind
/// them exists (WAVE1.md C.3: Settings shows only controls something already obeys). The Wave 2
/// slice that builds that behavior turns its flag on; <see cref="AllOnForScenes"/> forces every
/// flag on for the length of one ui-probe scene, so 05, 06 and 15 still show their full pages.
/// </summary>
internal static class SettingsFeatures
{
    /// <summary>Accounts > Signed in: the account list with each account's scope, Add account and
    /// Sign out - the whole Accounts category, which leaves the nav while this is off. Wave 2 slice
    /// E, when it reads the agent browser's profile and fills <see cref="SettingsActions.Accounts"/>,
    /// <see cref="SettingsActions.AddAccount"/> and <see cref="SettingsActions.SignOut"/>.</summary>
    internal static bool Accounts;

    /// <summary>History &amp; privacy > Screenshots > Save screenshots. Wave 2 slice F, when it
    /// writes screenshots by this setting. Storage, Clear and Delete all data already read the real
    /// evidence store, so they stay shown either way.</summary>
    internal static bool History;

    /// <summary>History &amp; privacy > Storage > Agent browser: Deskweave has no reliable way to
    /// measure the agent browser profile yet. Wave 2 slice E, alongside the account list above.</summary>
    internal static bool BrowserData;

    /// <summary>History &amp; privacy > Storage > Made by agents in your projects: Deskweave has no
    /// reliable way to find a workspace's project folder yet. Wave 2 slice F.</summary>
    internal static bool ProjectFiles;

    /// <summary>Turns every flag above on, for the length of one <c>using</c> block, and puts each
    /// back the way it was on <see cref="IDisposable.Dispose"/>. A scene builds its page inside the
    /// block; the built tree keeps whatever visibility it was given, so restoring before the
    /// screenshot (or right after, as here) never undoes it - only later scenes in the same run see
    /// the flags go back to their real, mostly-off defaults.</summary>
    internal static IDisposable AllOnForScenes()
    {
        var was = (Accounts, History, BrowserData, ProjectFiles);
        Accounts = History = BrowserData = ProjectFiles = true;
        return new Scope(() => (Accounts, History, BrowserData, ProjectFiles) = was);
    }

    sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal enum AgentState { NotInstalled, Found, Connected }

/// <summary>An account signed in in the agent browser. Its scope is AppSettings.AccountScopes[Key]:
/// the one workspace it is kept for, or all of them when it is not there.</summary>
internal sealed record SettingsAccount(string Site, string Name, Color Tile)
{
    internal string Key => Site + "|" + Name;
}

/// <summary>One project workspace's own files, shown under "Made by agents in your projects"
/// (reference 15) while <see cref="SettingsFeatures.ProjectFiles"/> is on. <see cref="Bytes"/> is
/// null until Wave 2 can measure a real project folder; a scene supplies fixture rows instead.</summary>
internal sealed record ProjectStorageEntry(string Name, string Path, long? Bytes);

/// <summary>One kind of data Deskweave keeps, shown as a row in the Storage group (reference 15) and
/// a segment of its meter, in this order. <see cref="Measure"/> is null-safe: null means the size is
/// not known yet, drawn as a placeholder dash and left out of the total and the meter. <see cref="Clear"/>
/// is a field on <see cref="SettingsActions"/>, so the gate can swap it for a harmless stand-in.</summary>
internal sealed record StorageKind(string Label, string? Hint, string ClearNoun, Func<long?> Measure,
    Func<bool> Visible, Func<bool> CanClear, string? DisabledTooltip, Action Clear,
    string MeterBrush, double MeterOpacity);

/// <summary>The Storage group's model (WAVE1B.md C.5): the four kinds Deskweave can size today, in
/// meter order, and the per-project rows below them.</summary>
internal static class SettingsStorage
{
    internal static IReadOnlyList<StorageKind> Kinds() =>
    [
        new("Screenshots and history", "Kept for 7 days", "screenshots and history",
            () => SettingsActions.HistoryBytes(), () => true, () => true, null,
            SettingsActions.ClearHistory, "AccentBrush", 1.0),
        new("Agent browser", "Clearing signs agents out of every account", "agent browser data",
            SettingsActions.BrowserDataBytes, () => SettingsFeatures.BrowserData, () => true, null,
            SettingsActions.ClearBrowserData, "AccentBrush", 0.5),
        new("Scratch files", null, "Scratch files",
            () => SettingsActions.ScratchBytes(), () => true, () => !SettingsActions.ScratchRunning(), "Scratch is in use",
            SettingsActions.ClearScratch, "FaintInkBrush", 1.0),
        new("Logs", null, "logs",
            () => SettingsActions.LogsBytes(), () => true, () => true, null,
            SettingsActions.ClearLogs, "AsleepBrush", 1.0),
    ];

    internal static IReadOnlyList<ProjectStorageEntry> Projects() => SettingsActions.Projects();
}

/// <summary>
/// What Settings does outside its own view: the Run key, agent configurations, the clipboard,
/// Explorer, deleting Deskweave's data, and measuring and clearing what it keeps. Each is a field so
/// the UI gate can stand in for it, and nothing the gate runs reaches the owner's registry, agents,
/// real files or a real quit.
/// </summary>
internal static class SettingsActions
{
    internal static Func<AppSettings, bool> SyncStartup = StartWithWindows.Sync;
    internal static Func<WorkspaceConnections.AgentApp, (int Connected, int Total)> ReadProfileCounts = WorkspaceConnections.ProfileCounts;
    internal static Func<WorkspaceConnections.AgentApp, string?> ReadConnectionFailure = WorkspaceConnections.LastFailure;

    /// <summary>What an agent row shows. Not a seam of its own: it reads the two engine fields
    /// (<see cref="WorkspaceConnections.Locate"/>, <see cref="WorkspaceConnections.IsConnected"/>)
    /// that first launch, Settings and the keep-up loop all share, so a probe stands in once.</summary>
    internal static AgentState ReadAgent(WorkspaceConnections.AgentApp app) =>
        !WorkspaceConnections.IsInstalled(app) ? AgentState.NotInstalled
        : WorkspaceConnections.IsConnected(app) ? AgentState.Connected : AgentState.Found;

    /// <summary>The owner asked for this agent, by a switch here or on first launch: remember the
    /// answer, so nothing connects one he turned off, then do it. Null, or why it could not.</summary>
    internal static Task<string?> Connect(WorkspaceConnections.AgentApp app, bool on)
    {
        WorkspaceConnections.Remember(app, on);
        return WorkspaceConnections.SetConnected(app, on, default);
    }

    internal static Action<string> CopyText = Clipboard.SetText;

    internal static Action<string> OpenFolder = folder =>
    {
        Directory.CreateDirectory(folder);
        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        explorer.ArgumentList.Add(folder);
        Process.Start(explorer)?.Dispose();
    };

    internal static Func<IReadOnlyList<SettingsAccount>> Accounts = () => [];
    internal static Action? AddAccount;
    internal static Action<SettingsAccount>? SignOut;

    /// <summary>Made by agents in your projects (Wave 2 slice F fills this in with real project
    /// folders); a scene overrides it with fixture rows.</summary>
    internal static Func<IReadOnlyList<ProjectStorageEntry>> Projects = () => [];

    internal static Action<IReadOnlyList<string>> DeleteAllData = folders => DeleteEverything(folders, null);

    /// <summary>Velopack's Update.exe, one folder above the running version; null for a copy that
    /// was not installed, such as a build in out/.</summary>
    internal static string? Uninstaller => Environment.ProcessPath is { } exe
        && Path.GetDirectoryName(Path.GetDirectoryName(exe)) is { } root
        && File.Exists(Path.Combine(root, "Update.exe")) ? Path.Combine(root, "Update.exe") : null;

    /// <summary>Delete all Deskweave data, then remove the program too. Update.exe runs the same
    /// uninstall as Apps &amp; features, which takes Deskweave out of the agents' configs.</summary>
    internal static Action Uninstall = () => DeleteEverything(DataFolders, Uninstaller);

    /// <summary>Deskweave's two data folders: local (workspaces, settings, logs) and roaming.</summary>
    internal static IReadOnlyList<string> DataFolders => [ProductContext.LocalRoot, ProductContext.RoamingRoot];

    /// <summary>What another agent pastes into its MCP settings: the one Deskweave entry every
    /// workspace is reached through.</summary>
    internal static string SetupText() => JsonSerializer.Serialize(WorkspaceConnections.AppConfiguration,
        new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // --- Storage: sizes and clears (WAVE1B.md C.5) ------------------------------------------------

    /// <summary>Bytes of history: the step log and screenshots each workspace keeps in its evidence
    /// folder (WorkspaceEvidence). Read-only, so it needs no seam of its own.</summary>
    internal static Func<long> HistoryBytes = MeasureHistory;

    static long MeasureHistory()
    {
        long total = 0;
        foreach (string file in HistoryFiles())
            total += FileBytes(file);
        return total;
    }

    /// <summary>Clears the screenshots and step logs kept for every workspace, but keeps their
    /// folders, so a workspace that is running goes on writing its log and screenshots where it was.
    /// A field, so the gate can swap it for a harmless stand-in.</summary>
    internal static Action ClearHistory = ClearHistoryReal;

    static void ClearHistoryReal()
    {
        foreach (string file in HistoryFiles())
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static List<string> HistoryFiles()
    {
        var files = new List<string>();
        try
        {
            if (!Directory.Exists(WorkspaceStore.Root)) return files;
            foreach (string workspace in Directory.EnumerateDirectories(WorkspaceStore.Root))
            {
                string evidence = Path.Combine(workspace, "evidence");
                if (Directory.Exists(evidence)) files.AddRange(Directory.EnumerateFiles(evidence, "*", SearchOption.AllDirectories));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>Deskweave has no reliable source for the agent browser profile's size yet (Wave 2
    /// slice E); a scene overrides this with a fixture number.</summary>
    internal static Func<long?> BrowserDataBytes = () => null;

    /// <summary>A field, so the gate can swap it for a harmless stand-in; Wave 2 slice E fills in the
    /// real clear once it owns the agent browser.</summary>
    internal static Action ClearBrowserData = () => { };

    // The Scratch workspace's own folder holds its sandbox redirects (appdata, local, temp,
    // chrome-profile), its evidence (already counted above) and its workspace.json record, alongside
    // whatever files the owner or an agent actually left in it. Only that last part is "Scratch
    // files" - the rest is either machine state nobody asked to keep or counted under a different row.
    static readonly string[] ScratchSkip = ["appdata", "local", "temp", "chrome-profile", "evidence", "last-frame.png", "workspace.json"];

    internal static Func<long> ScratchBytes = MeasureScratch;

    static long MeasureScratch()
    {
        string? folder = ScratchFolder();
        if (folder is null) return 0;
        long total = 0;
        foreach (string entry in SafeEntries(folder))
            if (!ScratchSkip.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                total += EntryBytes(entry);
        return total;
    }

    internal static Func<bool> ScratchRunning = () =>
        ScratchWorkspace() is { } scratch && WorkspaceRuntime.Of(scratch.Id) is not null;

    /// <summary>A field, so the gate can swap it for a harmless stand-in.</summary>
    internal static Action ClearScratch = ClearScratchReal;

    static void ClearScratchReal()
    {
        if (ScratchRunning()) return;
        string? folder = ScratchFolder();
        if (folder is null) return;
        foreach (string entry in SafeEntries(folder).ToArray())
            if (!ScratchSkip.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                RemoveEntry(entry);
    }

    static StoredWorkspace? ScratchWorkspace() => WorkspaceStore.All().FirstOrDefault(w => w.Agents == WorkspaceHome.Scratch);

    static string? ScratchFolder()
    {
        StoredWorkspace? scratch = ScratchWorkspace();
        return scratch is null ? null : WorkspaceStore.FolderOf(scratch.Id);
    }

    internal static Func<long> LogsBytes = () => EntryBytes(ProductContext.Local("logs"));

    /// <summary>A field, so the gate can swap it for a harmless stand-in.</summary>
    internal static Action ClearLogs = ClearLogsReal;

    static void ClearLogsReal()
    {
        string folder = ProductContext.Local("logs");
        foreach (string entry in SafeEntries(folder).ToArray())
            RemoveEntry(entry);
    }

    /// <summary>Under 1 MB in KB, under 1 GB in MB with no decimals, else GB with one decimal
    /// (WAVE1B.md C.5).</summary>
    internal static string FormatStorageBytes(long bytes)
    {
        const long Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        if (bytes < Mb) return Math.Round(bytes / (double)Kb, MidpointRounding.AwayFromZero).ToString("0") + " KB";
        if (bytes < Gb) return Math.Round(bytes / (double)Mb, MidpointRounding.AwayFromZero).ToString("0") + " MB";
        return (bytes / (double)Gb).ToString("0.0") + " GB";
    }

    static IEnumerable<string> SafeEntries(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    static long FileBytes(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The recursive byte count of one file or folder. Never throws: a folder Windows will
    /// not let Deskweave read counts as empty rather than failing the whole total.</summary>
    static long EntryBytes(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    total += FileBytes(file);
                return total;
            }
            return File.Exists(entry) ? new FileInfo(entry).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // --- Deleting everything --------------------------------------------------------------------

    static void DeleteEverything(IReadOnlyList<string> folders, string? uninstaller)
    {
        Application application = Application.Current;
        if (!QuitQuestion.Ask(application.MainWindow)) return;
        // Nothing may run out of a folder that is about to go, and nothing starts at sign-in any more.
        WorkspaceRuntime.Rest();
        SyncStartup(AppSettingsStore.Current with { StartWithWindows = false, FirstRunDone = true });
        // Exit is raised after the app's own shutdown has saved what it saves, so this is the last word.
        application.Exit += (_, _) =>
        {
            foreach (string folder in folders) Remove(folder);
            if (uninstaller is null) return;
            var start = new ProcessStartInfo(uninstaller) { UseShellExecute = false };
            start.ArgumentList.Add("uninstall");
            start.ArgumentList.Add("--silent");
            Process.Start(start)?.Dispose();
        };
        if (application is App confirmedApp) confirmedApp.ShutdownAfterConfirmedQuit();
        else application.Shutdown();
    }

    static void Remove(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // Deskweave may be installed inside its own local folder; its program files stay.
        string? program = Environment.ProcessPath is { } exe ? Path.GetDirectoryName(exe) + Path.DirectorySeparatorChar : null;
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder).ToArray())
        {
            if (program is not null && program.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            RemoveEntry(entry);
        }
        try { Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes one file or folder, retrying briefly: a process that has only just stopped
    /// can still hold a file open for a moment.</summary>
    static void RemoveEntry(string entry)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else if (File.Exists(entry)) File.Delete(entry);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
