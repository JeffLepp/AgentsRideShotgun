using System.Windows;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Hub (Stack) and a workspace in full: references 03, 04.</summary>
static class HubScenes
{
    static IReadOnlyList<HubEntry> Working(SceneContext scene) =>
    [
        new HubEntry("shop") { Name = "shop", Working = true, AgentText = "Claude Code", Preview = scene.Site("shop") },
        new HubEntry("blog") { Name = "blog", Working = true, NeedsYou = true, AgentText = "Codex wants you", Preview = scene.Site("blog") },
    ];

    static IReadOnlyList<HubEntry> Asleep(SceneContext scene) =>
    [
        new HubEntry("landing-page") { Name = "landing-page", Age = "2h", SidebarAge = "2h ago", Preview = scene.Site("docs") },
        new HubEntry("api") { Name = "api", Age = "yesterday", SidebarAge = "Yesterday" },
        new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday" },
    ];

    [Scene("hub", "03-hub", 1076, 24, 340, 804)]
    static async Task<FrameworkElement> Hub(SceneContext scene)
    {
        var window = scene.Own(new MainWindow());
        window.Left = SceneContext.OffScreen.X;
        window.Top = SceneContext.OffScreen.Y;
        window.Show();
        await scene.Settle();
        window.Hub.LoadFixture(Working(scene), Asleep(scene));
        window.ShowStack();
        window.Width = 340;
        window.Height = 804;
        await scene.Settle();
        return window;
    }

    [Scene("workspace-full", "04-workspace-full", 120, 20, 1200, 826)]
    static async Task<FrameworkElement> WorkspaceFull(SceneContext scene)
    {
        var window = scene.Own(new MainWindow());
        window.Left = SceneContext.OffScreen.X;
        window.Top = SceneContext.OffScreen.Y;
        window.Show();
        await scene.Settle();
        window.Hub.LoadFixture(Working(scene), Asleep(scene));
        window.ShowWide("shop");
        window.Width = 1200;
        window.Height = 826;
        window.OpenWorkspaceView!.LoadFixture("shop", @"C:\code\shop",
            [("Claude Code", true), ("Codex next", false)],
            scene.Site("shop"),
            [
                ("10:41:52", "Opened ", "localhost:5173"),
                ("10:42:03", "Clicked Add to cart", ""),
                ("10:42:09", "Clicked Checkout", ""),
            ],
            [
                ("checkout.png", "Claude · 10:42"),
                ("hero.png", "You · 10:39"),
                ("test-notes.md", "Claude · 10:31"),
            ]);
        await scene.Settle();
        return window;
    }

    /// <summary>Behavior checks for the hub, run at the end of the UI gate.</summary>
    internal static async Task Gate()
    {
        // Program.Run() already scopes AppSettingsStore for the whole gate; a second scope here
        // would throw "already active" when this runs as part of that gate rather than alone.
        // It also leaves ShellPreferences on whatever mode its own persistence check saved last,
        // so start this scene from the same blank slate a fresh install would see.
        new ShellPreferences().Save();
        var window = new MainWindow { ShowActivated = false, Left = SceneContext.OffScreen.X, Top = SceneContext.OffScreen.Y };
        try
        {
            window.Show();
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "stack", "The hub opens on the stack");
            Program.Check(window.MinWidth == 320 && window.MinHeight == 480, "The stack has its own minimum size");
            StoredWorkspace shop = WorkspaceStore.Create("shop");
            StoredWorkspace asleepOne = WorkspaceStore.Create("asleep-one");
            await Task.Delay(300);
            Program.Check(window.Hub.Asleep.Any(e => e.Id == shop.Id) && window.Hub.Asleep.Any(e => e.Id == asleepOne.Id),
                "A stored workspace with no computer shows under Asleep");
            using WorkspaceRuntime runtime = WorkspaceRuntime.Start(shop);
            await Task.Delay(300);
            Program.Check(window.Hub.Working.Any(e => e.Id == shop.Id) && !window.Hub.Asleep.Any(e => e.Id == shop.Id),
                "Starting a workspace's computer moves it from Asleep to Working");
            window.ShowWide(shop.Id);
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "wide" && window.MinWidth == 960 && window.MinHeight == 600,
                "Opening a workspace widens the window and raises its minimum size");
            Program.Check(window.OpenWorkspaceView is not null && window.SelectedWorkspaceId == shop.Id,
                "The wide window shows the clicked workspace");
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window),
                0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "stack", "Escape from a workspace returns to the stack");
            window.ShowWide(shop.Id);
            window.ShowSettings();
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "settings", "The gear opens Settings in the wide window");
            var settingsSlot = (System.Windows.Controls.ContentControl)window.FindName("SettingsSlot")!;
            var settingsView = (SettingsView)settingsSlot.Content;
            settingsView.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window),
                0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "wide" && window.SelectedWorkspaceId == shop.Id,
                "Settings' back action returns to the workspace the owner had open");
            ModuleEntry.Selected = asleepOne.Id;
            bool raised = false;
            void OnOpen() => raised = true;
            ModuleEntry.DashboardOpenRequested += OnOpen;
            ModuleEntry.RequestDashboardOpen();
            await Task.Delay(300);
            ModuleEntry.DashboardOpenRequested -= OnOpen;
            Program.Check(raised && window.SelectedWorkspaceId == asleepOne.Id,
                "The corner window's open-in-hub opens that workspace in the wide window");
            window.Dispose();
            WorkspaceStore.Delete(shop.Id);
            WorkspaceStore.Delete(asleepOne.Id);
        }
        finally { window.Close(); }
    }
}
