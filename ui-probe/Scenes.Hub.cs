using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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

    static IReadOnlyList<HubEntry> Recent(SceneContext scene) =>
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
        window.Hub.LoadFixture(Working(scene), Recent(scene));
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
        window.Hub.LoadFixture(Working(scene), Recent(scene));
        window.ShowWide("shop");
        window.Width = 1200;
        window.Height = 826;
        window.OpenWorkspaceView!.LoadFixture("shop", @"C:\code\shop",
            [("Claude Code", true), ("Codex waiting", false)],
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
            Program.Check(window.FindName("NewButton") is null,
                "The title bar has no new-workspace button");
            Program.Check(!VisibleWords(window).Contains("asleep", StringComparison.OrdinalIgnoreCase),
                "No visible word, tooltip or accessible name in the stack says asleep");

            // --- fix list item 9: the relative-age refresh runs only while the window is visible ---
            Program.Check(window.Hub.AgingActive, "The relative-age refresh timer runs while the hub window is visible");
            window.Hide();
            await Task.Delay(100);
            Program.Check(!window.Hub.AgingActive, "The relative-age refresh timer stops once the hub window is hidden");
            window.Show();
            await Task.Delay(300);
            Program.Check(window.Hub.AgingActive, "The relative-age refresh timer restarts when the hub window is shown again");
            var closing = new System.ComponentModel.CancelEventArgs();
            App.HideOnClose(window, closing);
            Program.Check(closing.Cancel && !window.IsVisible, "Closing the hub hides it to the tray");
            window.Show();
            await Task.Delay(100);

            StoredWorkspace shop = WorkspaceStore.Create("shop");
            StoredWorkspace recentOne = WorkspaceStore.Create("recent-one");
            await Task.Delay(300);
            Program.Check(window.Hub.Asleep.Any(e => e.Id == shop.Id) && window.Hub.Asleep.Any(e => e.Id == recentOne.Id),
                "A stored workspace with no computer shows under Recent");
            using WorkspaceRuntime runtime = WorkspaceRuntime.Start(shop);
            await Task.Delay(300);
            Program.Check(window.Hub.Working.Any(e => e.Id == shop.Id) && !window.Hub.Asleep.Any(e => e.Id == shop.Id),
                "Starting a workspace's computer moves it from Recent to Working");
            window.ShowWide(shop.Id);
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "wide" && window.MinWidth == 960 && window.MinHeight == 600,
                "Opening a workspace widens the window and raises its minimum size");
            Program.Check(window.OpenWorkspaceView is not null && window.SelectedWorkspaceId == shop.Id,
                "The wide window shows the clicked workspace");
            Program.Check(!VisibleWords(window).Contains("asleep", StringComparison.OrdinalIgnoreCase),
                "No visible word, tooltip or accessible name on the workspace page says asleep");
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
            ModuleEntry.Selected = recentOne.Id;
            bool raised = false;
            void OnOpen() => raised = true;
            ModuleEntry.DashboardOpenRequested += OnOpen;
            ModuleEntry.RequestDashboardOpen();
            await Task.Delay(300);
            ModuleEntry.DashboardOpenRequested -= OnOpen;
            Program.Check(raised && window.SelectedWorkspaceId == recentOne.Id,
                "The corner window's open-in-hub opens that workspace in the wide window");

            // --- brief A.3: opening a recent workspace starts nothing; its More menu is exactly
            // Rename, Delete (Stop/Start computer and Who can use it are gone) -------------------
            WorkspaceFullView moreView = window.OpenWorkspaceView!;
            Program.Check(WorkspaceRuntime.Of(recentOne.Id) is null,
                "Opening a recent workspace starts nothing");
            moreView.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var moreItems = moreView.LastMoreMenu?.Items.Cast<object>().ToList() ?? [];
            Program.Check(moreItems.Count == 3 && moreItems[0] is MenuItem { Header: "Rename" }
                && moreItems[1] is Separator && moreItems[2] is MenuItem { Header: "Delete" },
                "The More menu is exactly Rename, Delete, with a separator before Delete");
            moreView.LastMoreMenu!.IsOpen = false;
            Program.Check(WorkspaceRuntime.Of(recentOne.Id) is null,
                "...and the More menu itself never starts that workspace's computer");

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
            WorkspaceStore.Delete(recentOne.Id);
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

            Program.Check(previewWindow.PreviewLoopInterval == TimeSpan.FromSeconds(1),
                "The stack picks its fixed Balanced preview interval");
        }
        finally { previewWindow.Dispose(); previewWindow.Close(); }

        TrayChecks();
    }

    static string VisibleWords(DependencyObject root)
    {
        var words = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock text) words.Add(text.Text ?? "");
            if (node is ContentControl { Content: string label }) words.Add(label);
            if (node is FrameworkElement element)
            {
                if (element.ToolTip is string tip) words.Add(tip);
                words.Add(AutomationProperties.GetName(element) ?? "");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return string.Join(' ', words);
    }

    static void TrayChecks()
    {
        ModuleEntry.AllPaused = false;
        int corner = 0, pause = 0, opened = 0, settings = 0, quit = 0;
        void Corner() => corner++;
        void Pause() => pause++;
        ModuleEntry.ShowCornerRequested += Corner;
        ModuleEntry.PauseAllRequested += Pause;
        try
        {
            using var menu = TrayMenu.Build(() => opened++, ModuleEntry.RequestShowCorner,
                ModuleEntry.RequestPauseAll, () => settings++, () => quit++);
            Program.Check(menu.Items.Count == 6 && menu.Items[0].Text == "Open Deskweave"
                && menu.Items[1].Text == "Show the corner window"
                && menu.Items[2].Text == "Pause every agent" && menu.Items[3].Text == "Settings"
                && menu.Items[4] is System.Windows.Forms.ToolStripSeparator
                && menu.Items[5].Text == "Quit Deskweave",
                "The tray menu has the five actions in order, with one separator before Quit");
            foreach (int index in new[] { 0, 1, 2, 3, 5 })
                ((System.Windows.Forms.ToolStripMenuItem)menu.Items[index]).PerformClick();
            Program.Check(opened == 1 && corner == 1 && pause == 1 && settings == 1 && quit == 1,
                "Tray actions reach the hub, corner, pause, Settings and quit paths");
            ModuleEntry.AllPaused = true;
            TrayMenu.RefreshPause(menu);
            Program.Check(menu.Items[2].Text == "Resume every agent",
                "The tray pause label follows the engine's paused state");
            ModuleEntry.AllPaused = false;
            TrayMenu.RefreshPause(menu);
            Program.Check(menu.Items[2].Text == "Pause every agent",
                "The tray pause label returns after resuming");

            Program.Check(QuitQuestion.Title == "Quit Deskweave?"
                && QuitQuestion.Body.StartsWith("Agents working now will stop. Your files stay.", StringComparison.Ordinal)
                && QuitQuestion.QuitLabel == "Quit" && QuitQuestion.CancelLabel == "Cancel",
                "The quit question uses plain words and Quit/Cancel buttons");
            QuitQuestion.ConfirmForTests = () => false;
            Program.Check(!QuitQuestion.Ask(null), "Cancel keeps Deskweave running");
            QuitQuestion.ConfirmForTests = () => true;
            Program.Check(QuitQuestion.Ask(null), "Quit accepts the confirmation");
        }
        finally
        {
            QuitQuestion.ConfirmForTests = null;
            ModuleEntry.AllPaused = false;
            ModuleEntry.ShowCornerRequested -= Corner;
            ModuleEntry.PauseAllRequested -= Pause;
        }
    }
}
