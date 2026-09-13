using System.IO;
using HiveMind.AgentWorkspaces;
using Microsoft.Win32;

namespace Deskweave;

/// <summary>
/// Settings > General > Start with Windows: one "Deskweave" value under the user's Run key,
/// pointing at the exe that is running, started in the background. Kept in step with the setting
/// at every launch and every change, so the default (on) is real without a click. Nothing is
/// written until first launch has been answered.
/// </summary>
internal static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Deskweave";
    internal const string Background = "--background";

    /// <summary>False when Windows refused the change, so Settings can say so instead of showing a
    /// switch that is on while nothing starts.</summary>
    internal static bool Sync(AppSettings settings)
    {
        if (!settings.FirstRunDone || Environment.ProcessPath is not { } exe) return true;
        try
        {
            using RegistryKey? run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (run is null) return false;
            string command = "\"" + exe + "\" " + Background;
            if (settings.StartWithWindows)
            {
                if (run.GetValue(Name) as string != command) run.SetValue(Name, command);
            }
            else if (run.GetValue(Name) is not null) run.DeleteValue(Name, false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
