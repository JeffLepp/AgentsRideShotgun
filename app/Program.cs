using Deskweave.AgentWorkspaces;
using Deskweave.Product;
using Microsoft.Win32;
using System.IO;
using Velopack;

namespace Deskweave;

/// <summary>
/// The entry point, by hand rather than generated from App.xaml: Setup and the uninstaller start
/// this exe with reserved arguments, and <see cref="VelopackApp"/> has to answer those and exit
/// before WPF starts.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // These commands must precede Velopack auto-apply and WPF/singleton activation.
        ProductContext.Configure(ProductContext.DefaultFolderName);
        if (args.Contains("--hivemind") && !IsHiveMindHost(args)) return 2;
        App.HostedByHiveMind = IsHiveMindHost(args);
        if (App.HostedByHiveMind) App.StartInBackground = true;
        if (args.Length == 1 && args[0] is "--storage" or "--storage-open")
            return StorageController.Command(null, true, args[0] == "--storage-open").GetAwaiter().GetResult();
        if (args.Length is 2 or 3 && args[0] == "--storage-clean")
        {
            if (args.Length == 3 && args[2] != "--dry-run") return 2;
            return StorageController.Command(args[1], args.Length == 3).GetAwaiter().GetResult();
        }
        if (args.Length > 0 && args[0] is "--storage" or "--storage-clean" or "--storage-open") return 2;
        if (args.Length == 1 && args[0] == "--bridge-path")
        { Console.WriteLine(WorkspaceConnections.Bridge); return 0; }
        if (args.Length is 1 or 3 && args[0] is "--restart" or "--update" or "--update-status")
        {
            if (args.Length == 3 && (args[0] != "--update" || args[1] != "--source" || !Path.IsPathFullyQualified(args[2]))) return 2;
            return ControllerLifecycle.Command(args[0] == "--restart" ? "restart" : args[0] == "--update" ? "update" : "status",
                args.Length == 3 ? args[2] : null).GetAwaiter().GetResult();
        }
        if (args.Length == 3 && args[0] == "--restart-after")
        {
            if (!ControllerLifecycle.WaitForPrevious(args[1], args[2])) return 1;
            args = [StartWithWindows.Background];
            App.StartInBackground = true;
        }
        // Inlined in Main: vpk checks statically that Run() is reached from here.
        if (!App.HostedByHiveMind) try
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(ApplyUpdateOnStart(args, App.InstanceName))
                .OnBeforeUninstallFastCallback(_ =>
                {
                    // While our files still exist: take ARS out of the agents' configs, or
                    // every agent session afterwards tries a bridge that is gone. The owner's
                    // workspaces and settings stay; Settings has Delete all ARS data.
                    // Velopack ends this after 30 s, so the quick part goes first.
                    Cleanup("startup registration", () =>
                    {
                        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                        // A different copy may now own startup. Remove only this install's value.
                        string own = "\"" + Environment.ProcessPath + "\" " + StartWithWindows.Background;
                        foreach (string name in new[] { StartWithWindows.Name, FormerName.Name })
                            if (string.Equals(run?.GetValue(name) as string, own, StringComparison.OrdinalIgnoreCase))
                                run!.DeleteValue(name, false);
                    });
                    Cleanup("agent connections", () =>
                    {
                        ProductContext.Configure(ProductContext.DefaultFolderName);
                        foreach (string left in ModuleEntry.Uninstall()) LogSetupLine("agent connections: " + left);
                    });
                })
                .Run();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogSetupFailure("bootstrap", error);
            // Installer callbacks must never fall through to WPF, including when an installed
            // payload is damaged. Velopack normally handles these arguments and exits itself.
            if (args.Any(arg => arg.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase))) return 1;
        }

        if (NotElevated.Handle(args)) return 0;
        // The installer's shortcuts carry this ID. Without it a copy started any other way (at sign-in,
        // after an update) gets a taskbar button of its own next to a pinned ARS.
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Velopack applies a downloaded update at startup unless told not to, and applying stops every
    /// running copy and restarts. Only a start the owner made applies it, with no ARS already
    /// running: an agent's bridge starts it with --background (as Windows does at sign-in), and a
    /// running copy may have an agent at work.
    /// </summary>
    internal static bool ApplyUpdateOnStart(string[] args, string instance)
    {
        if (IsHiveMindHost(args)) return false;
        if (args.Contains(StartWithWindows.Background)) return false;
        if (Updates.OtherControllerReason().Length > 0) return false;
        if (!Mutex.TryOpenExisting(instance, out Mutex? running)) return true;
        running.Dispose();
        return false;
    }

    internal static bool IsHiveMindHost(string[] args) => args.Length == 1 && args[0] == "--hivemind";

    /// <summary>Velopack's shortcut ID, velopack. plus the --packId in tools/build-installer.ps1.</summary>
    internal const string AppUserModelId = "velopack.ARSApp";

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    static void Cleanup(string step, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (error is not OutOfMemoryException) { LogSetupFailure(step, error); }
    }

    /// <summary>A cleanup the owner has to finish by hand. Paths and server names, never configuration values.</summary>
    static void LogSetupLine(string line)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductContext.DefaultFolderName);
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "setup-errors.log"), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch { /* Logging must not keep an installer hook alive. */ }
    }

    static void LogSetupFailure(string step, Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductContext.DefaultFolderName);
            Directory.CreateDirectory(folder);
            // Exception types identify a failed cleanup without persisting provider configuration.
            File.AppendAllText(Path.Combine(folder, "setup-errors.log"),
                $"{DateTimeOffset.UtcNow:O} {step}: {error.GetType().Name} (0x{error.HResult:X8}){Environment.NewLine}");
        }
        catch { /* Logging must not keep an installer hook alive. */ }
    }
}
