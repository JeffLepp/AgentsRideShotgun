using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;
using Microsoft.Win32;

namespace Deskweave;

/// <summary>
/// Settings rows that are built but stay hidden until the behavior behind them exists. The Wave 2
/// slice that builds that behavior turns its flag on; the ui-probe's scenes force them on to
/// photograph them.
/// </summary>
internal static class SettingsFeatures
{
    /// <summary>Agents > Remind agents to test in Deskweave. Wave 2 slice D, when it writes that line
    /// into the agents' global instructions.</summary>
    internal static bool RemindAgents;

    /// <summary>Browser &amp; accounts > Signed in: the account list with each account's scope, Add
    /// account and Sign out. Wave 2 slice E, when it reads the agent browser's profile and fills
    /// <see cref="SettingsActions.Accounts"/>, <see cref="SettingsActions.AddAccount"/> and
    /// <see cref="SettingsActions.SignOut"/>.</summary>
    internal static bool Accounts;
}

internal enum AgentState { NotInstalled, Found, Connected }

/// <summary>An account signed in in the agent browser. Its scope is AppSettings.AccountScopes[Key]:
/// the one workspace it is kept for, or all of them when it is not there.</summary>
internal sealed record SettingsAccount(string Site, string Name, Color Tile)
{
    internal string Key => Site + "|" + Name;
}

/// <summary>
/// What Settings does outside its own view: the Run key, agent configurations, the clipboard,
/// Explorer, the browsers on this PC and deleting Deskweave's data. Each is a field so the UI gate
/// can stand in for it, and nothing the gate runs reaches the owner's registry, agents or files.
/// </summary>
internal static class SettingsActions
{
    internal static Func<AppSettings, bool> SyncStartup = StartWithWindows.Sync;

    internal static Func<WorkspaceConnections.AgentApp, AgentState> ReadAgent = app =>
        !WorkspaceConnections.IsInstalled(app) ? AgentState.NotInstalled
        : WorkspaceConnections.IsConnected(app) ? AgentState.Connected : AgentState.Found;

    /// <summary>Adds or removes Deskweave in the agent's own configuration. Null, or why it could not.</summary>
    internal static Func<WorkspaceConnections.AgentApp, bool, Task<string?>> Connect =
        (app, on) => WorkspaceConnections.SetConnected(app, on);

    internal static Action<string> CopyText = Clipboard.SetText;

    internal static Action<string> OpenFolder = folder =>
    {
        Directory.CreateDirectory(folder);
        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        explorer.ArgumentList.Add(folder);
        Process.Start(explorer)?.Dispose();
    };

    internal static Func<IReadOnlyList<BrowserChoice>> InstalledBrowsers = FindBrowsers;

    internal static Func<IReadOnlyList<SettingsAccount>> Accounts = () => [];
    internal static Action? AddAccount;
    internal static Action<SettingsAccount>? SignOut;

    internal static Action<IReadOnlyList<string>> DeleteAllData = DeleteEverything;

    /// <summary>Deskweave's two data folders: local (workspaces, settings, logs) and roaming.</summary>
    internal static IReadOnlyList<string> DataFolders => [ProductContext.LocalRoot, ProductContext.RoamingRoot];

    /// <summary>What another agent pastes into its MCP settings: the one Deskweave entry every
    /// workspace is reached through.</summary>
    internal static string SetupText() => JsonSerializer.Serialize(WorkspaceConnections.AppConfiguration,
        new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Bytes of history: the step log and screenshots each workspace keeps in its evidence
    /// folder (WorkspaceEvidence).</summary>
    internal static long HistoryBytes()
    {
        long total = 0;
        foreach (string file in HistoryFiles())
            try { total += new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Deletes the history files but keeps their folders, so a workspace that is running
    /// goes on writing its log and screenshots where it was.</summary>
    internal static void ClearHistory()
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

    // The same places WorkspaceBrowser looks, one browser at a time: its App Paths entry, then the
    // folders each installer uses.
    static IReadOnlyList<BrowserChoice> FindBrowsers()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string files = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string files86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var found = new List<BrowserChoice>(2);
        if (Installed("chrome.exe", Path.Combine(files, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(files86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")))
            found.Add(BrowserChoice.Chrome);
        if (Installed("msedge.exe", Path.Combine(files86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(files, "Microsoft", "Edge", "Application", "msedge.exe")))
            found.Add(BrowserChoice.Edge);
        return found;
    }

    static bool Installed(string exe, params string[] folders)
    {
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return folders.Any(File.Exists);
    }

    static void DeleteEverything(IReadOnlyList<string> folders)
    {
        // Nothing may run out of a folder that is about to go, and nothing starts at sign-in any more.
        WorkspaceRuntime.Rest();
        SyncStartup(AppSettingsStore.Current with { StartWithWindows = false, FirstRunDone = true });
        Application application = Application.Current;
        // Exit is raised after the app's own shutdown has saved what it saves, so this is the last word.
        application.Exit += (_, _) => { foreach (string folder in folders) Remove(folder); };
        if (application is App app) app.RequestQuit();
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
            // A process that has only just stopped can hold a file for a moment.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
        try { Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
