using System.Diagnostics;
using System.IO;

namespace Deskweave.AgentWorkspaces;

public sealed partial class WorkspaceBrowser
{
    /// <summary>
    /// A Chromium ARS can drive. They all speak the same DevTools protocol; what differs is the exe,
    /// the folder under Program Files or LOCALAPPDATA it installs to, where it keeps its profile, and
    /// the page that turns on a live connection.
    /// </summary>
    sealed record Chromium(string Exe, string Vendor, string Inspect)
    {
        internal string Profile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Vendor, "User Data");
    }

    // The order is only the fallback when the owner's default browser is none of these. Brave and
    // Vivaldi accept chrome:// pages; Edge redirects them, so it gets its own.
    static readonly Chromium[] Chromiums =
    [
        new("chrome.exe", @"Google\Chrome", "chrome://inspect/#remote-debugging"),
        new("brave.exe", @"BraveSoftware\Brave-Browser", "chrome://inspect/#remote-debugging"),
        new("vivaldi.exe", "Vivaldi", "chrome://inspect/#remote-debugging"),
        new("msedge.exe", @"Microsoft\Edge", "edge://inspect/#remote-debugging"),
    ];

    static Chromium? Kind(string path) => Chromiums.FirstOrDefault(c =>
        c.Exe.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    static bool IsEdge(string path) => Kind(path)?.Exe == "msedge.exe";

    /// <summary>The owner's default browser for https, when it is a Chromium ARS knows.</summary>
    static string? DefaultChromium()
    {
        try
        {
            using var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string id) return null;
            using var command = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(id + @"\shell\open\command");
            if (command?.GetValue(null) is not string text) return null;
            int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            string path = end < 0 ? string.Empty : text[..(end + 4)].Trim().Trim('"');
            return File.Exists(path) && Kind(path) is not null ? path : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Where each Chromium lands when installed for every user or just this one.</summary>
    static IEnumerable<string> InstallGuesses(Chromium browser)
    {
        string application = Path.Combine(browser.Vendor, "Application", browser.Exe);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), application);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), application);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), application);
    }

    /// <summary>A Chromium the owner has open, which beats one installed and never used.</summary>
    static string? RunningChromium()
    {
        // No profile files or command lines are examined, only which exe owns a visible window.
        foreach (Chromium browser in Chromiums)
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(browser.Exe)))
                using (process)
                    try
                    {
                        if (process.MainWindowHandle != 0 && process.MainModule?.FileName is { } path && File.Exists(path))
                            return path;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return null;
    }
}
