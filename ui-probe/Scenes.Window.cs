using System.Windows;
using System.Windows.Media;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Settings hosted in the real wide window, title bar included: references 05 and 06 whole.
/// It checks that the hub and Settings fit together.</summary>
static class WindowScenes
{
    [Scene("window-settings-control", "05-settings-control", 120, 20, 1200, 826)]
    static Task<FrameworkElement> Control(SceneContext scene) => Open(scene, "control");

    [Scene("window-settings-browser", "06-settings-browser", 120, 20, 1200, 826)]
    static async Task<FrameworkElement> Browser(SceneContext scene)
    {
        StoredWorkspace shop = scene.Workspace("shop");
        var accounts = SettingsActions.Accounts;
        bool shown = SettingsFeatures.Accounts;
        SettingsActions.Accounts = () =>
        [
            new SettingsAccount("Google", "you@gmail.com", Color.FromRgb(0x4A, 0x7B, 0xF7)),
            new SettingsAccount("GitHub", "yourname", Color.FromRgb(0x24, 0x29, 0x2F)),
            new SettingsAccount("Stripe", "Test mode", Color.FromRgb(0x63, 0x5B, 0xFF)),
        ];
        SettingsFeatures.Accounts = true;
        AppSettingsStore.Update(s => s with { AccountScopes = new Dictionary<string, string> { ["Stripe|Test mode"] = shop.Id } });
        try { return await Open(scene, "browser"); }
        finally
        {
            SettingsFeatures.Accounts = shown;
            SettingsActions.Accounts = accounts;
        }
    }

    static async Task<FrameworkElement> Open(SceneContext scene, string category)
    {
        var window = scene.Own(new MainWindow());
        window.Left = SceneContext.OffScreen.X;
        window.Top = SceneContext.OffScreen.Y;
        window.Show();
        await scene.Settle();
        window.ShowWide(null);
        window.Width = 1200;
        window.Height = 826;
        window.ShowSettings();
        ((SettingsView)window.SettingsSlot.Content).Show(category);
        await scene.Settle();
        return window;
    }
}
