using System.Windows;

namespace Deskweave.UiProbe;

/// <summary>Settings in the real wide window, including its title bar.</summary>
static class WindowScenes
{
    [Scene("window-settings-agents", "05-settings-agents", 120, 20, 1200, 826)]
    static Task<FrameworkElement> Agents(SceneContext scene) => Open(scene, "agents");

    [Scene("window-settings-accounts", "06-settings-accounts", 120, 20, 1200, 826)]
    static Task<FrameworkElement> Accounts(SceneContext scene) => Open(scene, "accounts");

    [Scene("window-settings-storage", "15-settings-storage", 120, 20, 1200, 826)]
    static Task<FrameworkElement> Storage(SceneContext scene) => Open(scene, "history");

    static async Task<FrameworkElement> Open(SceneContext scene, string category)
    {
        using IDisposable fixture = SettingsFixtures.Use(scene, category);
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
        await scene.Settle(550);
        return window;
    }
}
