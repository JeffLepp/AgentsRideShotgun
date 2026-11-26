using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>Spec (owner, 2026-11-20, field report on 0.3.10: an agent stopped 15 minutes ago read
    /// "1m" under Recent): Recent's time is the last time an agent or the owner used the workspace,
    /// and when it went to sleep, if later, is in the tooltip.</summary>
    static void RecentTimeChecks()
    {
        const long now = 10_000_000, min = 60_000;
        Program.Check(WorkspaceRuntime.UnusedFor(now, false, now - 20 * min, now - 15 * min, now - 16 * min) == TimeSpan.FromMinutes(15),
            "Recent counts from the agent's last call, not from when the workspace went to sleep");
        Program.Check(WorkspaceRuntime.UnusedFor(now, false, now - 20 * min, now - 15 * min, now - 3 * min) == TimeSpan.FromMinutes(3),
            "...or from the last command an agent started, if later");
        Program.Check(WorkspaceRuntime.UnusedFor(now, false, now - 2 * min, now - 15 * min, 0) == TimeSpan.FromMinutes(2),
            "...or from the owner's last hands on its screen, if later");
        Program.Check(WorkspaceRuntime.UnusedFor(now, true, now - 20 * min, now - 15 * min, 0) == TimeSpan.Zero,
            "A workspace the owner has in hand as it stops was used just now");
        Program.Check(WorkspaceRuntime.UnusedFor(now, false, now + min, 0, 0) == TimeSpan.Zero,
            "Recent's age never goes negative");

        var at = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
        Program.Check(HubFormat.AgeTip(at.AddMinutes(-15), at.AddMinutes(-1), at) == "Last used 15m ago · went to sleep 1m ago",
            "The tooltip says when it was last used and, when later, when it went to sleep");
        Program.Check(HubFormat.AgeTip(at.AddMinutes(-15), at.AddMinutes(-15).AddSeconds(20), at) == "Last used 15m ago"
            && HubFormat.AgeTip(at.AddMinutes(-15), null, at) == "Last used 15m ago",
            "...and only the last use when it slept in the same minute, or has no sleep on record");
        Program.Check(HubFormat.AgeTip(at.AddDays(-3), at.AddDays(-3), at) == "Last used on " + at.AddDays(-3).ToString("dddd"),
            "An older last use reads as a day");
    }

    /// <summary>Expected behavior: the stack's scroll bar shows exactly when the cards overflow, and
    /// showing or hiding it never resizes a card. Card screens take their height from their width,
    /// so a bar that took width shortened every screen, the list fit again, the bar hid, and the
    /// stack flickered between the two near the overflow edge. Sweeps the window height across that
    /// edge and holds each height long enough for a flicker to show.</summary>
    static async Task ScrollBarNeverReflows()
    {
        var window = new MainWindow { ShowActivated = false, Left = TestScreen.Work.Left + 20, Top = TestScreen.Work.Top + 20 };
        try
        {
            window.Show();
            await Task.Delay(200);
            // Three cards: their screens put the overflow edge well above the window's minimum height.
            window.Hub.LoadFixture([.. Enumerable.Range(0, 3).Select(i => new HubEntry("edge-" + i) { Name = "edge-" + i, Working = true })], []);
            window.ShowStack();
            window.Width = 340;
            window.Height = 1600;
            ScrollViewer scroll = window.StackScroll;
            // The screens size themselves from their width after they load; measure only once the
            // list's height has stopped changing.
            double settled = -1;
            for (int pass = 0; pass < 40 && Math.Abs(scroll.ExtentHeight - settled) > 0.5; pass++)
            {
                settled = scroll.ExtentHeight;
                await Task.Delay(100);
                window.UpdateLayout();
            }
            Program.Check(scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible,
                $"Three working cards in a tall stack show no scroll bar [extent {scroll.ExtentHeight:0.#}, viewport {scroll.ViewportHeight:0.#}]");
            // A card with no picture yet still gets its full screen height when it loads, not only
            // after something first changes its width (which used to be the scroll bar appearing).
            bool sizedAtLoad = Descendants<Border>(window.StackWorkingList).Where(b => b.Tag is "0.625").ToList() is { Count: 3 } loaded
                && loaded.All(b => b.ActualWidth > 0 && Math.Abs(b.ActualHeight - Math.Round(b.ActualWidth * 0.625)) <= 1);
            if (!sizedAtLoad)
            {
                // Measure the flicker anyway (checked below): give the screens their height the
                // only way the old code did, a width change, then settle again.
                window.Width = 341; window.UpdateLayout(); window.Width = 340;
                for (int pass = 0; pass < 40 && Math.Abs(scroll.ExtentHeight - settled) > 0.5; pass++)
                {
                    settled = scroll.ExtentHeight;
                    await Task.Delay(100);
                    window.UpdateLayout();
                }
            }
            double chrome = window.ActualHeight - scroll.ViewportHeight;
            double fitHeight = chrome + scroll.ExtentHeight;
            Program.Check(Math.Round(fitHeight) - 24 >= window.MinHeight,
                $"The stack's overflow edge ({fitHeight:0.#}) sits above the window's minimum height ({window.MinHeight:0.#}), so a height sweep can cross it");

            var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ScrollViewer.ComputedVerticalScrollBarVisibilityProperty, typeof(ScrollViewer));
            int flips = 0;
            EventHandler onFlip = (_, _) => flips++;
            var widths = new HashSet<double>();
            bool sawBar = false, sawNoBar = false, flickered = false, mismatched = false;
            for (double height = Math.Round(fitHeight) - 24; height <= Math.Round(fitHeight) + 24; height += 2)
            {
                window.Height = height;
                window.UpdateLayout();
                await Task.Delay(60);
                int before = flips;
                visibility.AddValueChanged(scroll, onFlip);
                await Task.Delay(250);
                visibility.RemoveValueChanged(scroll, onFlip);
                window.UpdateLayout();
                if (flips != before) flickered = true;
                bool bar = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible;
                if (bar != scroll.ExtentHeight > scroll.ViewportHeight + 0.5) mismatched = true;
                (bar ? ref sawBar : ref sawNoBar) = true;
                foreach (var screen in Descendants<Border>(window.StackWorkingList).Where(b => b.Tag is "0.625"))
                    widths.Add(Math.Round(screen.ActualWidth, 1));
            }
            Program.Check(sawBar && sawNoBar, "The height sweep crosses the stack's overflow edge (bar both shown and hidden)"
                + (sawBar && sawNoBar ? "" : $" [fit {fitHeight:0.#}, chrome {chrome:0.#}, last window {window.ActualHeight:0.#}, viewport {scroll.ViewportHeight:0.#}, extent {scroll.ExtentHeight:0.#}, min {window.MinHeight:0.#}, max {window.MaxHeight:0.#}]"));
            Program.Check(!flickered, "At every height near the overflow edge the stack's scroll bar holds still instead of flickering");
            Program.Check(!mismatched, "...and it shows exactly when the cards overflow the stack");
            Program.Check(widths.Count == 1, "Showing or hiding the stack's scroll bar never resizes a card's screen (widths seen: "
                + string.Join(", ", widths) + ")");
            Program.Check(sizedAtLoad, "A working card with no picture yet gets its full screen height (0.625 x width) when it loads");
        }
        finally { window.Dispose(); window.Close(); }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

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
        WorkspaceFullView view = window.OpenWorkspaceView!;
        view.LoadFixture("shop", @"C:\code\shop",
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
        view.ApplyMemorySample(620UL * 1024 * 1024);
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
            Program.Check(window.FindName("NewButton") is Button { IsVisible: true },
                "The title bar's new-workspace button is back after Settings");
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
            window.UpdateLayout();
            var stackCards = (ItemsControl)window.FindName("StackWorkingList");
            Button workingCard = CardButtons(stackCards).Single(b => b.IsVisible && b.Tag as string == shop.Id
                && b.Style == (Style)window.FindResource("StackItemButton"));
            workingCard.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            await Task.Delay(200);
            Program.Check(window.DisplayMode == "wide" && window.SelectedWorkspaceId == shop.Id
                && window.WindowState == WindowState.Normal,
                "Double-clicking the actual working card opens its workspace page at normal size, not maximized");
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await Task.Delay(200);
            Program.Check(window.DisplayMode == "stack" && window.WindowState == WindowState.Normal
                && Math.Abs(window.ActualWidth - 350) < 2 && Math.Abs(window.ActualHeight - 510) < 2,
                "Escape from the workspace page returns to the compact strip at its saved size");
            var recentRows = (ItemsControl)window.FindName("StackAsleepList");
            Button recentCard = CardButtons(recentRows).Single(b => b.IsVisible && b.Tag as string == recentOne.Id
                && b.Style == (Style)window.FindResource("StackItemButton"));
            recentCard.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            await Task.Delay(200);
            Program.Check(window.DisplayMode == "wide" && window.SelectedWorkspaceId == recentOne.Id
                && window.WindowState == WindowState.Normal && WorkspaceRuntime.Of(recentOne.Id) is null,
                "Double-clicking a recent workspace opens its page without restarting its apps");
            window.ShowStack();
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

            // Opening a recent workspace starts nothing, even in its own window.
            WorkspaceFullView moreView = window.OpenWorkspaceView!;
            Program.Check(WorkspaceRuntime.Of(recentOne.Id) is null,
                "Opening a recent workspace starts nothing");
            moreView.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var moreItems = moreView.LastMoreMenu?.Items.Cast<object>().ToList() ?? [];
            Program.Check(moreItems.Count == 4 && moreItems[0] is MenuItem { Header: "Pop out" }
                && moreItems[1] is MenuItem { Header: "Rename" } && moreItems[2] is Separator && moreItems[3] is MenuItem { Header: "Delete" },
                "The More menu starts with Pop out, then Rename and a separator before Delete");
            moreView.LastMoreMenu!.IsOpen = false;
            Program.Check(WorkspaceRuntime.Of(recentOne.Id) is null,
                "...and the More menu itself never starts that workspace's computer");
            Program.Check(!moreView.ScreenRightDown(new Point(5, 5)) && !moreView.ScreenRightUp(),
                "A sleeping screen neither opens a desktop menu nor consumes a pending release");
            var popOut = (MenuItem)moreItems[0];
            popOut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(200);
            WorkspaceWindow? popped = WorkspaceWindow.Find(recentOne.Id);
            Program.Check(popped is { WindowState: WindowState.Normal, Width: >= 960 and <= 1600, Height: >= 600 and <= 1000 }
                && popped.View.WorkspaceId == recentOne.Id && popped.Title == recentOne.Name + " · ARS"
                && window.DisplayMode == "stack", "Pop out opens a normal workspace window within its size bounds and returns the hub to the stack");
            popOut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Program.Check(ReferenceEquals(WorkspaceWindow.Find(recentOne.Id), popped)
                && Application.Current.Windows.OfType<WorkspaceWindow>().Count(w => w.View.WorkspaceId == recentOne.Id) == 1,
                "Popping out again activates the one existing window");
            popped!.View.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(!popped.View.LastMoreMenu!.Items.OfType<MenuItem>().Any(item => item.Header as string == "Pop out"),
                "A pop-out's own More menu does not offer another pop-out");
            popped.View.LastMoreMenu.IsOpen = false;
            popped.View.LastMoreMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Rename")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            popped.View.RenameBox.Text = "Renamed pop-out";
            popped.View.RenameBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Program.Check(popped.Title == "Renamed pop-out · ARS" && window.Hub.Find(recentOne.Id)?.Name == "Renamed pop-out",
                "Renaming in the pop-out updates its title and the hub entry");
            window.ShowWide(recentOne.Id);
            Program.Check(window.DisplayMode == "stack", "Reopening a popped-out workspace keeps only its separate page live");
            popped.Close();
            Program.Check(WorkspaceWindow.Find(recentOne.Id) is null
                && !Application.Current.Windows.OfType<WorkspaceWindow>().Any(w => w.View.WorkspaceId == recentOne.Id),
                "Closing the pop-out removes its window and lookup entry");
            StoredWorkspace disposable = WorkspaceStore.Create("delete-pop-out");
            window.PopOutWorkspace(disposable.Id);
            WorkspaceWindow deleting = WorkspaceWindow.Find(disposable.Id)!;
            deleting.View.MoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deleting.View.LastMoreMenu!.IsOpen = false;
            WorkspaceFullView.ConfirmDeleteForTests = () => true;
            try
            {
                deleting.View.LastMoreMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Delete")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally { WorkspaceFullView.ConfirmDeleteForTests = null; }
            Program.Check(WorkspaceWindow.Find(disposable.Id) is null && WorkspaceStore.Find(disposable.Id) is null,
                "Deleting from a pop-out closes its window");

            // --- store/attention events off the UI thread; dispose vs a queued refresh
            StoredWorkspace bgWorkspace = await Task.Run(() => WorkspaceStore.Create("bg-thread-test"));
            await Task.Delay(300);
            Program.Check(window.Hub.Asleep.Any(e => e.Id == bgWorkspace.Id && e.Name == bgWorkspace.Name)
                && WorkspaceStore.Find(bgWorkspace.Id)?.Name == bgWorkspace.Name,
                "A workspace created off the UI thread keeps its name on disk and in Recent");
            WorkspaceStore.Delete(bgWorkspace.Id);

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

            window.PopOutWorkspace(recentOne.Id);
            window.Dispose();
            Program.Check(WorkspaceWindow.Find(recentOne.Id) is null, "Disposing the main window closes its pop-outs");
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
            // Closing Find re-creates the cards, and each screen takes its height when it loads, after
            // layout. Scroll to the bottom only once the list has stopped growing.
            double stackExtent = -1;
            for (int pass = 0; pass < 40 && Math.Abs(stackScroll.ExtentHeight - stackExtent) > 0.5; pass++)
            {
                stackExtent = stackScroll.ExtentHeight;
                await Task.Delay(100);
                previewWindow.UpdateLayout();
            }
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

        await ScrollBarNeverReflows();
        RecentTimeChecks();

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
            string memoryPattern = @"^Workspace RAM (\d+ MB|\d+"
                + Regex.Escape(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) + @"\d GB)$";
            Program.Check(idleView.SleepButton.Visibility == Visibility.Visible,
                "The workspace page offers a Sleep control while its computer is running");
            for (DateTime until = DateTime.UtcNow.AddSeconds(8);
                idleView.MemoryText.Visibility != Visibility.Visible && DateTime.UtcNow < until;)
            {
                idleView.RefreshLive();
                await Task.Delay(50);
            }
            Program.Check(idleView.MemoryText.Visibility == Visibility.Visible
                && Regex.IsMatch(idleView.MemoryText.Text, memoryPattern),
                "The running page shows this workspace's memory as whole MB or one-decimal GB");
            Program.Check(idleView.FindName("PcMemoryText") is null
                && !VisibleWords(window).Contains("% in use", StringComparison.Ordinal),
                "The workspace page shows no PC-wide memory metric");
            idleView.ApplyMemorySample(null);
            Program.Check(idleView.MemoryText.Visibility == Visibility.Collapsed && idleView.MemoryText.Text == "",
                "A failed workspace sample clears the previous measurement and hides the memory line");
            idleView.ApplyMemorySample(0);
            Program.Check(idleView.MemoryText.Visibility == Visibility.Visible
                && idleView.MemoryText.Text == "Workspace RAM 0 MB",
                "A real zero-byte workspace sample shows 0 MB and restores the memory line");
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            try
            {
                const ulong mb = 1024 * 1024;
                foreach (var (culture, oneGb, fractionalGb) in new[]
                {
                    ("en-US", "Workspace RAM 1.0 GB", "Workspace RAM 1.4 GB"),
                    ("de-DE", "Workspace RAM 1,0 GB", "Workspace RAM 1,4 GB"),
                })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    Program.Check(WorkspaceFullView.FormatWorkspaceMemory(0) == "Workspace RAM 0 MB"
                        && WorkspaceFullView.FormatWorkspaceMemory(620 * mb) == "Workspace RAM 620 MB"
                        && WorkspaceFullView.FormatWorkspaceMemory(1024 * mb - 1) == "Workspace RAM 1023 MB"
                        && WorkspaceFullView.FormatWorkspaceMemory(1024 * mb) == oneGb
                        && WorkspaceFullView.FormatWorkspaceMemory(1434 * mb) == fractionalGb,
                        $"Memory units switch at 1024 MB and use the {culture} decimal separator");
                }
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }
            // Checked here, with the Sleep control and the memory line both actually on screen: once
            // it sleeps they collapse, and VisibleWords skips whatever is not visible.
            Program.Check(!VisibleWords(window).Contains("asleep", StringComparison.OrdinalIgnoreCase),
                "Sleep control's wording never uses the reserved word asleep");

            await ScreenMenuChecks(idleView, idlingRuntime);

            BitmapSource? beforeSleep = (BitmapSource?)idleView.ScreenPicture.Source;
            idleView.RefreshLive();
            idleView.SleepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Program.Check(WorkspaceRuntime.Of(idling.Id) is null, "The Sleep control stops the workspace in one click");
            Program.Check(window.Hub.Asleep.Any(e => e.Id == idling.Id) && !window.Hub.Working.Any(e => e.Id == idling.Id),
                "...and the hub moves it from Working to Recent");
            Program.Check(idleView.SleepButton.Visibility == Visibility.Collapsed,
                "...and the now-sleeping page hides its own Sleep control");
            Program.Check(idleView.MemoryText.Visibility == Visibility.Collapsed,
                "A sleeping workspace hides the memory line even when a sample is pending");
            Program.Check(!idleView.ScreenRightDown(new Point(5, 5)) && !idleView.ScreenInput!.Press(new Point(5, 5), true),
                "A sleeping screen neither opens the desktop menu nor forwards right-clicks");

            // --- a sleeping workspace looks asleep instead of broken ---------------------------------
            Program.Check(idleView.ScreenPicture.Opacity < 1.0 && idleView.LastSeenPill.Visibility == Visibility.Visible,
                "A sleeping workspace shows its last picture dimmed, built on the existing Last seen pill");
            await Task.Delay(1300);
            Program.Check(ReferenceEquals(idleView.ScreenPicture.Source, beforeSleep),
                "...and takes no further captures: the picture is still the exact frame it went to sleep with");

            // --- a restart under the open page: its old picture takes no clicks ---------------------
            using (WorkspaceRuntime woken = WorkspaceRuntime.Start(idling))
            {
                // Synchronous with the start, so no tick can have shown the new screen's frame yet.
                Program.Check(!idleView.ScreenRightDown(new Point(5, 5))
                    && !idleView.ScreenInput!.Press(new Point(5, 5), false) && woken.Plane?.Driving != Driver.Owner,
                    "A workspace that restarts under its open page takes no click on the old picture");
                for (DateTime until = DateTime.UtcNow.AddSeconds(12); !idleView.ScreenInput!.Press(new Point(5, 5), false) && DateTime.UtcNow < until;)
                    await Task.Delay(100);
                Program.Check(woken.Plane?.Driving == Driver.Owner, "...and does once its own frame is on screen");
                woken.Plane!.Release();
            }

            // --- "What it did" follows the log while the page is open ---------------------------------
            int rowsBefore = idleView.DidList.Items.Count;
            string actionsLog = ActivityLog.LogPath(idling.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(actionsLog)!);
            File.AppendAllText(actionsLog, $"{DateTime.UtcNow:O}\tmission\tDone\tpage refresh check\n");
            for (DateTime until = DateTime.UtcNow.AddSeconds(8); idleView.DidList.Items.Count == rowsBefore && DateTime.UtcNow < until;)
                await Task.Delay(150);
            Program.Check(idleView.DidList.Items.Count > rowsBefore, "The open page shows a new step without reselecting the workspace");

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

    static async Task ScreenMenuChecks(WorkspaceFullView view, WorkspaceRuntime runtime)
    {
        Point empty = new(view.ScreenPicture.ActualWidth / 2, view.ScreenPicture.ActualHeight / 2);
        bool intercepted = false;
        for (DateTime until = DateTime.UtcNow.AddSeconds(12); !intercepted && DateTime.UtcNow < until;)
        {
            intercepted = view.ScreenRightDown(empty);
            if (!intercepted) await Task.Delay(100);
        }
        Program.Check(intercepted && view.ScreenRightUp() && view.LastScreenMenu?.IsOpen == true,
            "An empty live desktop consumes both halves of a right-click and opens its menu");
        ContextMenu menu = view.LastScreenMenu!;
        var labels = menu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
        Program.Check(labels.Contains("Pop out") && !labels.Contains("Give back control")
            && labels.Contains("Open an app...") == view.Taskbar!.IsVisible && runtime.Plane!.Driving != Driver.Owner,
            "The desktop menu offers Pop out and the visible taskbar's launcher without taking control");
        Program.Check(menu.Items.OfType<MenuItem>().All(item => AutomationProperties.GetName(item) == item.Header as string),
            "Every desktop menu item has an accessible name");
        menu.IsOpen = false;
        Visibility taskbarVisibility = view.Taskbar!.Visibility;
        view.ScreenInput!.SimulateClickForTests();
        view.Taskbar.Visibility = Visibility.Collapsed;
        Program.Check(view.ScreenRightDown(empty) && view.ScreenRightUp(), "The owner can open the menu while holding control");
        menu = view.LastScreenMenu!;
        Program.Check(!menu.Items.OfType<MenuItem>().Any(item => item.Header as string == "Open an app..."),
            "The desktop menu hides Open an app when the taskbar is hidden");
        menu.IsOpen = false;
        menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Give back control")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Program.Check(!view.ScreenInput.OwnsControl && runtime.Plane!.Driving != Driver.Owner,
            "Give back control releases the view's owner lease");
        view.Taskbar.Visibility = taskbarVisibility;

        runtime.Computer!.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        for (DateTime until = DateTime.UtcNow.AddSeconds(12); runtime.Computer.Windows().Count == 0 && DateTime.UtcNow < until;)
            await Task.Delay(100);
        AgentWindow target = runtime.Computer.Windows().First();
        Image picture = view.ScreenPicture;
        double scale = Math.Max(picture.ActualWidth / AgentDesktop.ScreenWidth, picture.ActualHeight / AgentDesktop.ScreenHeight);
        var point = new Point((picture.ActualWidth - AgentDesktop.ScreenWidth * scale) / 2 + (target.X + target.Width / 2.0) * scale,
            (picture.ActualHeight - AgentDesktop.ScreenHeight * scale) / 2 + (target.Y + target.Height / 2.0) * scale);
        Program.Check(!view.ScreenRightDown(point) && !view.ScreenRightUp() && view.LastScreenMenu?.IsOpen != true,
            "A right-click over a workspace window does not open or consume the desktop menu");
        Program.Check(view.ScreenInput.Press(point, true), "That right-click is accepted by the existing workspace input adapter");
        for (DateTime until = DateTime.UtcNow.AddSeconds(5);
            Program.RootOf(runtime.Computer.LastClickedForTests) != target.Handle && DateTime.UtcNow < until;)
            await Task.Delay(100);
        Program.Check(Program.RootOf(runtime.Computer.LastClickedForTests) == target.Handle,
            "The right-click reaches the workspace application window");
        view.ScreenInput.Release();
    }

    static IEnumerable<Button> CardButtons(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (Button descendant in CardButtons(child)) yield return descendant;
        }
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
                && QuitQuestion.Body.StartsWith("Workspace apps and agents will close.", StringComparison.Ordinal)
                && QuitQuestion.Body.Contains("Unsaved work will be lost", StringComparison.Ordinal)
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
