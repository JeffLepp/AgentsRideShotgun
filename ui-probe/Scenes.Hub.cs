using System.Windows;
using System.Windows.Controls;
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
        // Reference 04's sidebar shows api and Scratch as dimmed terminal frames, not blank.
        new HubEntry("api") { Name = "api", Age = "yesterday", SidebarAge = "Yesterday", Preview = scene.Site("term") },
        new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday", Preview = scene.Site("term") },
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
                // Reference 04's "What it did" rows each carry a 44-wide thumbnail (a live screenshot
                // in production, when one exists); the scene stands in with the workspace's own site.
                ("10:41:52", "Opened ", "localhost:5173", scene.Site("shop")),
                ("10:42:03", "Clicked Add to cart", "", scene.Site("shop")),
                ("10:42:09", "Clicked Checkout", "", scene.Site("shop")),
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

            // --- fix list item 9: the relative-age refresh runs only while the window is visible ---
            Program.Check(window.Hub.AgingActive, "The relative-age refresh timer runs while the hub window is visible");
            window.Hide();
            await Task.Delay(100);
            Program.Check(!window.Hub.AgingActive, "The relative-age refresh timer stops once the hub window is hidden");
            window.Show();
            await Task.Delay(300);
            Program.Check(window.Hub.AgingActive, "The relative-age refresh timer restarts when the hub window is shown again");

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
            // Settings takes Escape on the bubbling KeyDown, after any open dropdown or shortcut
            // recorder inside it has had the key.
            settingsView.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window),
                0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
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

            // --- fix list item 7: an asleep workspace's More menu offers "Start computer" ---------
            WorkspaceFullView moreView = window.OpenWorkspaceView!;
            Program.Check(WorkspaceRuntime.Of(asleepOne.Id) is null,
                "The workspace this check opens has no computer running yet (asleep)");
            moreView.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            MenuItem? startItem = moreView.LastMoreMenu?.Items.OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Header, "Start computer"));
            Program.Check(startItem is not null,
                "An asleep workspace's More menu offers \"Start computer\" in place of \"Stop computer\"");
            startItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(500);
            Program.Check(WorkspaceRuntime.Of(asleepOne.Id) is not null,
                "Clicking \"Start computer\" on the real menu item actually starts that workspace's computer");
            WorkspaceRuntime.Of(asleepOne.Id)?.Dispose();

            // --- fix list item 6: store/attention events off the UI thread; dispose vs a queued refresh
            Exception? crossThreadFailure = null;
            StoredWorkspace? bgWorkspace = null;
            await Task.Run(() =>
            {
                try { bgWorkspace = WorkspaceStore.Create("bg-thread-test"); }
                catch (Exception ex) { crossThreadFailure = ex; }
            });
            await Task.Delay(300);
            Program.Check(crossThreadFailure is null,
                "A workspace-store change raised off the UI thread does not throw mutating the hub's collections");
            Program.Check(bgWorkspace is not null && window.Hub.Asleep.Any(e => e.Id == bgWorkspace.Id),
                "...and still reaches the hub's Asleep list once dispatched to the UI thread");
            if (bgWorkspace is not null) WorkspaceStore.Delete(bgWorkspace.Id);

            var raceModel = new HubViewModel();
            bool raceThrew = false;
            StoredWorkspace? raceWorkspace = null;
            try { await Task.Run(() => raceWorkspace = WorkspaceStore.Create("dispose-race-test")); raceModel.Dispose(); }
            catch { raceThrew = true; }
            await Task.Delay(300);
            Program.Check(!raceThrew,
                "Disposing a hub view model right after a queued store-change refresh does not throw");
            Program.Check(raceWorkspace is not null && window.Hub.Asleep.Any(e => e.Id == raceWorkspace.Id),
                "...and the still-live hub window's own view model keeps refreshing normally afterward");
            if (raceWorkspace is not null) WorkspaceStore.Delete(raceWorkspace.Id);

            window.Dispose();
            WorkspaceStore.Delete(shop.Id);
            WorkspaceStore.Delete(asleepOne.Id);
        }
        finally { window.Close(); }

        // --- fix list item 4: preview loop rules, on a fresh window with fixture working cards -----
        var previewWindow = new MainWindow { ShowActivated = false, Left = SceneContext.OffScreen.X, Top = SceneContext.OffScreen.Y };
        try
        {
            previewWindow.Show();
            await Task.Delay(200);
            // Many more cards than any viewport could show at once, so the stack needs to scroll
            // regardless of the exact per-card height this build renders at.
            var pvCards = Enumerable.Range(0, 24).Select(i => new HubEntry("pv-" + i) { Name = "pv-" + i, Working = true }).ToList();
            previewWindow.Hub.LoadFixture(pvCards, []);
            previewWindow.ShowStack();
            previewWindow.Width = 340;
            previewWindow.Height = 480;
            await Task.Delay(300);
            Program.Check(previewWindow.PreviewLoopRunning, "The preview loop runs while the stack shows");
            Program.Check(previewWindow.ShouldCaptureForTest("pv-0"),
                "A working card inside the visible stack viewport is captured");

            ScrollViewer stackScroll = previewWindow.StackScroll;
            previewWindow.UpdateLayout();
            Program.Check(stackScroll.ScrollableHeight > 0, "The fixture has enough working cards to make the stack scroll");
            stackScroll.ScrollToVerticalOffset(stackScroll.ScrollableHeight);
            previewWindow.UpdateLayout();
            await Task.Delay(200);
            Program.Check(!previewWindow.ShouldCaptureForTest("pv-0"),
                "A working card scrolled out of the stack viewport is skipped by the capture loop");
            Program.Check(previewWindow.ShouldCaptureForTest("pv-23"),
                "...while the card now on screen at the bottom of the same scroll is still captured");

            previewWindow.ShowSettings();
            await Task.Delay(300);
            Program.Check(!previewWindow.PreviewLoopRunning, "Opening Settings stops the working-card preview loop");
            previewWindow.ShowStack();
            await Task.Delay(300);
            Program.Check(previewWindow.PreviewLoopRunning, "...and returning from Settings to the stack starts it again");

            AppSettingsStore.Update(s => s with { Smoothness = PreviewSmoothness.Smooth });
            await Task.Delay(200);
            Program.Check(previewWindow.PreviewLoopInterval == TimeSpan.FromMilliseconds(500),
                "A live Preview smoothness change re-times the already-running preview loop");
            AppSettingsStore.Update(s => s with { Smoothness = PreviewSmoothness.Balanced });
        }
        finally { previewWindow.Dispose(); previewWindow.Close(); }
    }
}
