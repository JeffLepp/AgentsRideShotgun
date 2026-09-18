using HiveMind.AgentWorkspaces;
using HiveMind.Product;
using Microsoft.Win32;
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
    static void Main()
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
                    try
                    {
                        ProductContext.Configure("Deskweave");
                        ModuleEntry.Uninstall(removeData: false);
                        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                        run?.DeleteValue("Deskweave", false);
                    }
                    catch { /* an uninstall that throws is worse than one that misses something */ }
                })
                .Run();
        }
        catch { /* a build that was not installed (out\, the probes) has nothing for Velopack to read */ }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
