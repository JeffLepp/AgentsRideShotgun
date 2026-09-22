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
        // Inlined in Main: vpk checks statically that Run() is reached from here.
        try
        {
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ =>
                {
                    // While our files still exist: take Deskweave out of the agents' configs, or
                    // every agent session afterwards tries a bridge that is gone. The owner's
                    // workspaces and settings stay; Settings has Delete all Deskweave data.
                    // Velopack ends this after 30 s, so the quick part goes first.
                    Cleanup("startup registration", () =>
                    {
                        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                        // A different copy may now own startup. Remove only this install's value.
                        string own = "\"" + Environment.ProcessPath + "\" " + StartWithWindows.Background;
                        if (string.Equals(run?.GetValue("Deskweave") as string, own, StringComparison.OrdinalIgnoreCase))
                            run!.DeleteValue("Deskweave", false);
                    });
                    Cleanup("agent connections", () =>
                    {
                        ProductContext.Configure("Deskweave");
                        ModuleEntry.Uninstall();
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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    static void Cleanup(string step, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (error is not OutOfMemoryException) { LogSetupFailure(step, error); }
    }

    static void LogSetupFailure(string step, Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskweave");
            Directory.CreateDirectory(folder);
            // Exception types identify a failed cleanup without persisting provider configuration.
            File.AppendAllText(Path.Combine(folder, "setup-errors.log"),
                $"{DateTimeOffset.UtcNow:O} {step}: {error.GetType().Name} (0x{error.HResult:X8}){Environment.NewLine}");
        }
        catch { /* Logging must not keep an installer hook alive. */ }
    }
}
