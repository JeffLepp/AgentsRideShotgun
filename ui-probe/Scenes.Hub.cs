using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Hub (Stack) and a workspace in full.</summary>
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
        // The sidebar shows api and Scratch as dimmed terminal frames, not blank.
        new HubEntry("api") { Name = "api", Age = "yesterday", SidebarAge = "Yesterday", Preview = scene.Site("term") },
        new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday", Preview = scene.Site("term") },
    ];

    [Scene("hub")]
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
        window.Height = 560;
        await scene.Settle();
        return window;
    }

    /// <summary>The app's own question before something that cannot be undone, as deleting a
    /// workspace asks it. Its content is what is photographed; Windows draws the frame.</summary>
    [Scene("question-delete")]
    static async Task<FrameworkElement> DeleteQuestion(SceneContext scene)
    {
        Window dialog = scene.Own(Question.Build(null, "Delete shop?",
            "Its screen, the files on its page, its history and its browser sign-ins are deleted, and anything "
            + "running on it stops. Your project folder is not touched. This can't be undone.", "Delete", danger: true));
        dialog.Show();
        await scene.Settle();
        return (FrameworkElement)dialog.Content;
    }

    /// <summary>The tray menu, drawn by its own renderer off-screen and shown as a picture.</summary>
    [Scene("tray-menu")]
    static async Task<FrameworkElement> TrayMenuScene(SceneContext scene)
    {
        await scene.Settle(50);
        using System.Windows.Forms.ContextMenuStrip menu = TrayMenu.Build(() => { }, () => { }, () => { }, () => { }, () => { });
        menu.PerformLayout();
        System.Drawing.Size size = menu.GetPreferredSize(System.Drawing.Size.Empty);
        menu.Size = size;
        using var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, size.Width, size.Height));
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var picture = new System.Windows.Media.Imaging.BitmapImage();
        picture.BeginInit();
        picture.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        picture.StreamSource = stream;
        picture.EndInit();
        var image = new System.Windows.Controls.Image { Source = picture, Width = size.Width, Height = size.Height, Stretch = System.Windows.Media.Stretch.None };
        var window = scene.Own(new Window { Content = image, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None });
        window.Show();
        await scene.Settle();
        return image;
    }

    [Scene("hub-long-names")]
    static async Task<FrameworkElement> LongNames(SceneContext scene)
    {
        var window = (MainWindow)await Hub(scene);
        window.Hub.LoadFixture(
        [
            new HubEntry("long-project") { Name = "a-rather-long-workspace-name", Working = true,
                AgentText = "Claude Code · Sleeps in 4m", Preview = scene.Site("shop") },
            new HubEntry("needs-you") { Name = "another-long-project-name", Working = true, NeedsYou = true,
                AgentText = "Codex wants you", Preview = scene.Site("blog") },
        ], Recent(scene));
        window.Width = 320;
        window.Height = 480;
        await scene.Settle();
        return window;
    }

    [Scene("workspace-full")]
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
                // The "What it did" rows each carry a 44-wide thumbnail (a live screenshot
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
        var window = new MainWindow { ShowActivated = false, Left = TestScreen.Work.Left + 20, Top = TestScreen.Work.Top + 20 };
        try
        {
            window.Show();
            await Task.Delay(300);
            Program.Check(window.DisplayMode == "stack", "The hub opens on the stack");
            Program.Check(window.MinWidth == 320 && window.MinHeight == 480, "The stack has its own minimum size");
            window.Width = 350;
            window.Height = 510;
            window.UpdateLayout();
            window.ShowSettings();
            await Task.Delay(800); // Include the debounced preference save while Settings is open.
            Program.Check(Math.Abs(ShellPreferences.Read().Stack!.Width - 350) < 2,
                "Saving Settings preserves the narrow strip's width");
            window.ShowStack();
            await Task.Delay(100);
            Program.Check(Math.Abs(window.ActualWidth - 350) < 2 && Math.Abs(window.ActualHeight - 510) < 2,
                "Returning from Settings restores the strip's size even after an immediate resize");
            Program.Check(window.FindName("NewButton") is null,
                "The title bar has no new-workspace button");
            Program.Check(!VisibleWords(window).Contains("asleep", StringComparison.OrdinalIgnoreCase),
                "No visible word, tooltip or accessible name in the stack says asleep");

            // --- the relative-age refresh runs only while the window is visible ---
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

            // --- opening a recent workspace starts nothing; its More menu is exactly
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

            // --- store/attention events off the UI thread; dispose vs a queued refresh
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

        // --- preview loop rules, on a fresh window with fixture working cards -----
        var previewWindow = new MainWindow { ShowActivated = false, Left = TestScreen.Work.Left + 20, Top = TestScreen.Work.Top + 20 };
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
            previewWindow.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            previewWindow.FilterBox.Text = "pv-23";
            previewWindow.UpdateLayout();
            Program.Check(previewWindow.StackWorkingList.Items.Count == 1 && previewWindow.ShouldCaptureForTest("pv-23"),
                "The visible Find action filters workspaces and keeps the matching preview active");
            previewWindow.FilterBox.Text = "no-such-workspace";
            Program.Check(previewWindow.FilterEmptyText.Visibility == Visibility.Visible,
                "A filter with no matches explains the empty result");
            previewWindow.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            previewWindow.UpdateLayout();
            Program.Check(previewWindow.StackWorkingList.Items.Count == 24 && previewWindow.FilterEmptyText.Visibility == Visibility.Collapsed,
                "Closing Find restores every workspace and clears the empty-result message");

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
            Program.Check(ModuleEntry.HubShowing, "Visible Settings keeps the automatic corner hidden while preview capture is stopped");
            previewWindow.ShowStack();
            await Task.Delay(300);
            Program.Check(previewWindow.PreviewLoopRunning, "...and returning from Settings to the stack starts it again");

            // Not a fixed second: the loop slows to 2.5x on battery rather than stopping,
            // so a probe run on an unplugged laptop reads 2.5s and is just as right.
            Program.Check(previewWindow.PreviewLoopInterval == HubPreview.Interval(),
                "The stack picks the preview interval the power state calls for");
            Program.Check(HubPreview.Interval() == TimeSpan.FromSeconds(
                    WorkspacePeekCapture.OnBattery() ? 2.5 : 1),
                "...one second plugged in, two and a half on battery");
        }
        finally { previewWindow.Dispose(); previewWindow.Close(); }

        // Recent keeps the last eight; an older one is still one search away.
        var longWindow = new MainWindow { ShowActivated = false, Left = SceneContext.OffScreen.X, Top = SceneContext.OffScreen.Y };
        try
        {
            longWindow.Show();
            longWindow.Hub.LoadFixture([], [.. Enumerable.Range(1, 12).Select(i => new HubEntry("old-" + i) { Name = "project-" + i, Age = i + "d" })]);
            await Task.Delay(150);
            Program.Check(longWindow.StackAsleepList.Items.Count == 8
                    && longWindow.StackAsleepList.Items.Cast<HubEntry>().Select(e => e.Name).SequenceEqual(Enumerable.Range(1, 8).Select(i => "project-" + i)),
                "Recent shows the eight most recent workspaces, newest first");
            Program.Check(longWindow.SidebarAsleepList.Items.Count == 8, "The wide window's Recent keeps the same eight");
            longWindow.FilterBox.Text = "project-12";
            await Task.Delay(100);
            Program.Check(longWindow.StackAsleepList.Items.Count == 1
                    && ((HubEntry)longWindow.StackAsleepList.Items[0]).Name == "project-12",
                "Search still finds a workspace older than the last eight");
            longWindow.FilterBox.Text = "";
            longWindow.SelectWorkspace("old-11");
            await Task.Delay(100);
            Program.Check(longWindow.SidebarAsleepList.Items.Cast<HubEntry>().Any(e => e.Id == "old-11"),
                "An older workspace stays listed in the sidebar while it is the one open");
        }
        finally { longWindow.Close(); }

        await SleepAndControlGate();
        TrayChecks();
    }

    /// <summary>
    /// A running workspace can be seen, stopped and understood: a running
    /// workspace nobody is using reads as idle rather than the same dot as one mid-task; the Sleep
    /// control stops it in one click and the hub moves it to Recent; a sleeping workspace shows its
    /// last picture dimmed, built on the existing Last seen pill, and takes no further captures; and
    /// pressing the live picture makes "you have control" legible, clearing again once the lease
    /// lets go.
    /// </summary>
    static async Task SleepAndControlGate()
    {
        var window = new MainWindow { ShowActivated = false, Left = TestScreen.Work.Left + 20, Top = TestScreen.Work.Top + 20 };
        try
        {
            window.Show();
            await Task.Delay(300);

            // --- idle reads as idle, the Sleep control, and the one honest memory line --------------
            StoredWorkspace idling = WorkspaceStore.Create("idling");
            using WorkspaceRuntime idlingRuntime = WorkspaceRuntime.Start(idling);
            await Task.Delay(300);
            // The stack card and the wide sidebar row (WorkingCardTemplate, SidebarWorkingTemplate in
            // MainWindow.xaml) both bind Idle and OpenLabel from this same HubEntry through the same
            // StatusDot style, so checking the model here is checking both surfaces at once.
            HubEntry idleEntry = window.Hub.Find(idling.Id)!;
            Program.Check(idleEntry.Idle && idlingRuntime.IsIdle,
                "A running workspace nobody is using reads as idle, in the hub entry the stack and the sidebar both draw from");
            Program.Check(idleEntry.OpenLabel.Contains("idle", StringComparison.OrdinalIgnoreCase),
                "...and its accessible name says so too");
            Program.Check(idleEntry.AgentText.Contains("Sleeps in"),
                "...with a real countdown from WorkspaceRuntime.SleepsIn, not an invented one");

            window.ShowWide(idling.Id);
            await Task.Delay(300);
            WorkspaceFullView idleView = window.OpenWorkspaceView!;
            Program.Check(idleView.SleepButton.Visibility == Visibility.Visible,
                "The workspace page offers a Sleep control while its computer is running");
            Program.Check(idleView.MemoryText.Visibility == Visibility.Visible && idleView.MemoryText.Text.StartsWith("Memory ")
                && idleView.MemoryText.Text.EndsWith("% in use")
                && uint.TryParse(idleView.MemoryText.Text["Memory ".Length..^"% in use".Length], out uint shownLoad) && shownLoad <= 100,
                "The page shows one honest line for what running costs, from WorkspaceRuntime.MemoryLoad");
            // Checked here, with the Sleep control and the memory line both actually on screen: once
            // it sleeps they collapse, and VisibleWords skips whatever is not visible.
            Program.Check(!VisibleWords(window).Contains("asleep", StringComparison.OrdinalIgnoreCase),
                "Sleep control's wording never uses the reserved word asleep");

            BitmapSource? beforeSleep = (BitmapSource?)idleView.ScreenPicture.Source;
            idleView.SleepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Program.Check(WorkspaceRuntime.Of(idling.Id) is null, "The Sleep control stops the workspace in one click");
            Program.Check(window.Hub.Asleep.Any(e => e.Id == idling.Id) && !window.Hub.Working.Any(e => e.Id == idling.Id),
                "...and the hub moves it from Working to Recent");
            Program.Check(idleView.SleepButton.Visibility == Visibility.Collapsed,
                "...and the now-sleeping page hides its own Sleep control");

            // --- a sleeping workspace looks asleep instead of broken ---------------------------------
            Program.Check(idleView.ScreenPicture.Opacity < 1.0 && idleView.LastSeenPill.Visibility == Visibility.Visible,
                "A sleeping workspace shows its last picture dimmed, built on the existing Last seen pill");
            await Task.Delay(1300);
            Program.Check(ReferenceEquals(idleView.ScreenPicture.Source, beforeSleep),
                "...and takes no further captures: the picture is still the exact frame it went to sleep with");

            // --- taking over is legible ---------------------------------------------------------------
            StoredWorkspace driven = WorkspaceStore.Create("driven");
            using WorkspaceRuntime drivenRuntime = WorkspaceRuntime.Start(driven);
            window.ShowWide(driven.Id);
            await Task.Delay(300);
            WorkspaceFullView drivenView = window.OpenWorkspaceView!;
            Program.Check(drivenView.ControlToast.Visibility == Visibility.Collapsed,
                "Nothing claims the owner has control before they have taken it");
            drivenView.ScreenInput!.SimulateClickForTests();
            Program.Check(drivenView.ControlToast.Visibility == Visibility.Visible && drivenRuntime.Plane!.Driving == Driver.Owner,
                "Pressing the live picture shows they have control the moment they take it");
            Program.Check(
                drivenView.ControlToastText.Text.Contains(((int)WorkspaceScreenInput.LeaveDelay.TotalSeconds).ToString())
                && drivenView.ControlToastText.Text.Contains(((int)WorkspaceScreenInput.StayDelay.TotalSeconds).ToString()),
                "...naming the real LeaveDelay and StayDelay the engine actually waits, not a hardcoded copy");
            drivenRuntime.Plane!.Release();
            await Task.Delay(300);
            Program.Check(drivenView.ControlToast.Visibility == Visibility.Collapsed,
                "...and it clears again once the lease lets go and the agent resumes");

            window.Dispose();
            WorkspaceStore.Delete(idling.Id);
            WorkspaceStore.Delete(driven.Id);
        }
        finally { window.Close(); }
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
            Program.Check(menu.Items.Count == 6 && menu.Items[0].Text == "Open ARS"
                && menu.Items[1].Text == "Show the corner window"
                && menu.Items[2].Text == "Pause every agent" && menu.Items[3].Text == "Settings"
                && menu.Items[4] is System.Windows.Forms.ToolStripSeparator
                && menu.Items[5].Text == "Quit ARS",
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

            Program.Check(QuitQuestion.Title == "Quit ARS?"
                && QuitQuestion.Body.StartsWith("Agents working now will stop. Your files stay.", StringComparison.Ordinal)
                && QuitQuestion.QuitLabel == "Quit" && QuitQuestion.CancelLabel == "Cancel",
                "The quit question uses plain words and Quit/Cancel buttons");
            QuitQuestion.ConfirmForTests = () => false;
            Program.Check(!QuitQuestion.Ask(null), "Cancel keeps ARS running");
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
