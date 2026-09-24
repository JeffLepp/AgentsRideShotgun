using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Harmless data shared by the body and whole-window Settings photographs.</summary>
static class SettingsFixtures
{
    const long Mb = 1024 * 1024;

    internal static IDisposable Use(SceneContext scene, string category)
    {
        IDisposable flags = SettingsFeatures.AllOnForScenes();
        IDisposable agents = Program.AgentSeams();
        var accounts = SettingsActions.Accounts;
        var history = SettingsActions.HistoryBytes;
        var browser = SettingsActions.BrowserDataBytes;
        var scratch = SettingsActions.ScratchBytes;
        var logs = SettingsActions.LogsBytes;
        var projects = SettingsActions.Projects;
        var running = SettingsActions.ScratchRunning;
        Program.Agents(_ => AgentState.Connected);
        SettingsActions.Accounts = () =>
        [
            new SettingsAccount("Google", "you@gmail.com", Color.FromRgb(0x4A, 0x7B, 0xF7)),
            new SettingsAccount("GitHub", "yourname", Color.FromRgb(0x24, 0x29, 0x2F)),
            new SettingsAccount("Stripe", "Test mode", Color.FromRgb(0x63, 0x5B, 0xFF)),
        ];
        SettingsActions.HistoryBytes = () => 412 * Mb;
        SettingsActions.BrowserDataBytes = () => 268 * Mb;
        SettingsActions.ScratchBytes = () => 136 * Mb;
        SettingsActions.LogsBytes = () => 34 * Mb;
        SettingsActions.ScratchRunning = () => false;
        SettingsActions.Projects = () =>
        [
            new ProjectStorageEntry("shop", @"C:\code\shop", 38 * Mb),
            new ProjectStorageEntry("blog", @"C:\code\blog", 12 * Mb),
        ];
        if (category == "accounts")
        {
            StoredWorkspace shop = scene.Workspace("shop");
            AppSettingsStore.Update(s => s with
            {
                Theme = AppearanceManager.Choice,
                AccountScopes = new Dictionary<string, string> { ["Stripe|Test mode"] = shop.Id },
            });
        }
        return new Restore(() =>
        {
            agents.Dispose();
            SettingsActions.Accounts = accounts;
            SettingsActions.HistoryBytes = history;
            SettingsActions.BrowserDataBytes = browser;
            SettingsActions.ScratchBytes = scratch;
            SettingsActions.LogsBytes = logs;
            SettingsActions.Projects = projects;
            SettingsActions.ScratchRunning = running;
            flags.Dispose();
        });
    }

    sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}

static class SettingsScenes
{
    // The 1198 x 784 body below the hub's title bar.
    static SettingsView Host(SceneContext scene, string category)
    {
        var view = new SettingsView { Width = 1198, Height = 784 };
        Point at = SceneContext.OffScreen;
        Window window = scene.Own(new Window
        {
            Content = view, Width = 1198, Height = 784, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = at.X, Top = at.Y,
        });
        window.Show();
        ShowSettled(view, category);
        return view;
    }

    static async Task<FrameworkElement> Page(SceneContext scene, string category)
    {
        using IDisposable fixture = SettingsFixtures.Use(scene, category);
        SettingsView view = Host(scene, category);
        await scene.Settle(550);
        return view;
    }

    [Scene("settings-agents")]
    static Task<FrameworkElement> Agents(SceneContext scene) => Page(scene, "agents");

    [Scene("settings-accounts")]
    static Task<FrameworkElement> Accounts(SceneContext scene) => Page(scene, "accounts");

    [Scene("settings-storage")]
    static Task<FrameworkElement> Storage(SceneContext scene) => Page(scene, "history");

    [Scene("settings-general")]
    static Task<FrameworkElement> General(SceneContext scene) => Page(scene, "general");

    internal static async Task Gate()
    {
        var sync = SettingsActions.SyncStartup;
        IDisposable agentSeams = Program.AgentSeams();
        var copy = SettingsActions.CopyText;
        var open = SettingsActions.OpenFolder;
        var accounts = SettingsActions.Accounts;
        var historyBytes = SettingsActions.HistoryBytes;
        var browserBytes = SettingsActions.BrowserDataBytes;
        var scratchBytes = SettingsActions.ScratchBytes;
        var logsBytes = SettingsActions.LogsBytes;
        var projects = SettingsActions.Projects;
        var scratchRunning = SettingsActions.ScratchRunning;
        var clearHistory = SettingsActions.ClearHistory;
        var clearBrowser = SettingsActions.ClearBrowserData;
        var clearScratch = SettingsActions.ClearScratch;
        var clearLogs = SettingsActions.ClearLogs;
        var delete = SettingsActions.DeleteAllData;
        ThemeChoice themeBefore = AppSettingsStore.Current.Theme;
        AppSettings agentsBefore = AppSettingsStore.Current;
        bool shortcutBefore = ModuleEntry.PauseShortcutTaken;
        bool opened = false, cleared = false, deleted = false, copied = false, startupSynced = false;
        bool scratchIsRunning = false;
        SettingsActions.SyncStartup = _ => { startupSynced = true; return true; };
        List<(WorkspaceConnections.AgentApp App, bool On)> asked = [];
        Program.Agents(_ => AgentState.Connected);
        WorkspaceConnections.SetConnected = (app, on, _) => { asked.Add((app, on)); return Task.FromResult<string?>(null); };
        SettingsActions.CopyText = _ => copied = true;
        SettingsActions.OpenFolder = _ => opened = true;
        SettingsActions.Accounts = () => [];
        SettingsActions.HistoryBytes = () => 412 * 1024 * 1024;
        SettingsActions.BrowserDataBytes = () => 268 * 1024 * 1024;
        SettingsActions.ScratchBytes = () => 136 * 1024 * 1024;
        SettingsActions.LogsBytes = () => 34 * 1024 * 1024;
        SettingsActions.Projects = () => [];
        SettingsActions.ScratchRunning = () => scratchIsRunning;
        SettingsActions.ClearHistory = () => cleared = true;
        SettingsActions.ClearBrowserData = () => { };
        SettingsActions.ClearScratch = () => { };
        SettingsActions.ClearLogs = () => { };
        SettingsActions.DeleteAllData = _ => deleted = true;
        SettingsFeatures.Accounts = false;
        SettingsFeatures.History = false;
        SettingsFeatures.BrowserData = false;
        SettingsFeatures.ProjectFiles = false;

        var view = new SettingsView();
        Point at = SceneContext.OffScreen;
        var window = ProbeWindow.OffScreen(new Window
        {
            Content = view, Width = 1198, Height = 784, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = at.X, Top = at.Y,
        });
        window.Show();
        try
        {
            string cancelledData = Path.Combine(Program.Output, "cancelled-uninstall-data");
            Directory.CreateDirectory(cancelledData);
            string marker = Path.Combine(cancelledData, "keep.txt");
            File.WriteAllText(marker, "keep");
            QuitQuestion.ConfirmForTests = () => false;
            try { delete([cancelledData]); }
            finally { QuitQuestion.ConfirmForTests = null; }
            Program.Check(File.Exists(marker) && !startupSynced && window.IsVisible,
                "Canceling the quit question leaves data, startup registration and Settings untouched");

            Program.Check(view.AvailableCategories.SequenceEqual(["general", "agents", "history"]),
                "Only active Settings categories appear before Accounts is wired");
            SettingsFeatures.Accounts = true;
            ShowSettled(view, "general");
            Program.Check(view.AvailableCategories.SequenceEqual(["general", "agents", "accounts", "history"]),
                "Settings categories appear in the four-page order");
            foreach (var (oldId, expected) in new[]
                { ("control", "agents"), ("corner", "general"), ("privacy", "history"), ("about", "general"), ("browser", "accounts") })
            {
                ShowSettled(view, oldId);
                Program.Check(view.Category == expected, oldId + " opens " + expected);
            }

            string[] retired = ["Where agents go", "Running at once", "Sleep when quiet", "Remind agents",
                "Carry on after you stop for", "Agents open things on your desktop", "Open the browser early",
                "Share sign-ins across workspaces", "Continuous every", "Keep history for", "Logs folder"];
            foreach (string category in new[] { "general", "agents", "accounts", "history" })
            {
                ShowSettled(view, category);
                string visibleText = string.Join("|", Descendants<TextBlock>(view).Where(x => x.IsVisible).Select(TextOf));
                Program.Check(retired.All(label => !visibleText.Contains(label, StringComparison.Ordinal)),
                    category + " has no retired rows");
            }

            ShowSettled(view, "general");
            Bound corner = view.BoundControls.Single(b => b.Label == "Show the corner window");
            corner.Choose(false);
            SettleVisual(view);
            Program.Check(AppSettingsStore.Current.CornerShow != CornerShow.Off && Equals(corner.Shown(), true)
                && Visible(view, "Turn off"), "Corner switch asks before turning automatic showing off");
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Off });
            SettleVisual(view);
            Program.Check(!Visible(view, "Turn off"), "Turning the corner window off elsewhere hides the question");
            corner.Choose(true);
            corner.Choose(false);
            SettleVisual(view);
            FindButton(view, "Turn off").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettleVisual(view);
            Program.Check(AppSettingsStore.Current.CornerShow == CornerShow.Off && Equals(corner.Shown(), false)
                && !Visible(view, "Turn off"), "Confirming the question turns automatic showing off");
            corner.Choose(true);

            // The card's corner tile asks the same question rather than turning it off in one click.
            var card = new SettingsView(card: true);
            var cardWindow = ProbeWindow.OffScreen(new Window
            {
                Content = card, Width = 340, Height = 560, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = at.X, Top = at.Y,
            });
            cardWindow.Show();
            try
            {
                SettleVisual(card);
                var tile = Descendants<System.Windows.Controls.Primitives.ToggleButton>(card)
                    .Single(t => AutomationProperties.GetName(t) == "Show the corner window");
                tile.IsChecked = false;
                tile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                SettleVisual(card);
                Program.Check(AppSettingsStore.Current.CornerShow != CornerShow.Off && tile.IsChecked == true && Visible(card, "Turn off"),
                    "The card's corner tile asks before turning the corner window off");
                Descendants<Button>(card).First(b => Equals(b.Content, "Turn off") && b.IsVisible)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SettleVisual(card);
                Program.Check(AppSettingsStore.Current.CornerShow == CornerShow.Off && tile.IsChecked == false && !Visible(card, "Turn off"),
                    "Confirming on the card turns the corner window off");
            }
            finally { cardWindow.Close(); }
            corner.Choose(true);
            Program.Check(AppSettingsStore.Current.CornerShow == CornerShow.ComesAndGoes, "Corner switch restores automatic showing");
            Bound startup = view.BoundControls.Single(b => b.Label == "Start with Windows");
            startup.Choose(true);
            startupSynced = false;
            startup.Choose(false);
            Program.Check(startupSynced, "Start with Windows reaches its startup seam");
            FindButton(view, "Open ARS data").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(opened, "Open ARS data reaches Explorer seam");
            Bound theme = view.BoundControls.Single(b => b.Label == "Theme");
            theme.Choose(ThemeChoice.Dark); Pump();
            Color dark = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            theme.Choose(ThemeChoice.Light); Pump();
            Color light = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            Program.Check(dark != light && !AppearanceManager.Dark, "Theme repaints Settings live");

            ShowSettled(view, "agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "Claude Code")
                && Descendants<TextBlock>(view).Any(x => TextOf(x) == "Codex"), "Installed agents appear");
            var profileCounts = SettingsActions.ReadProfileCounts;
            SettingsActions.ReadProfileCounts = _ => (1, 3);
            Program.Agents(_ => AgentState.Found);
            ShowSettled(view, "agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "1 of 3 profiles connected"),
                "Settings distinguishes a partially connected set of configuration profiles");
            CheckBox partial = Descendants<CheckBox>(view)
                .First(box => AutomationProperties.GetName(box) == "Claude Code connected");
            Program.Check(partial.IsChecked == true, "A partially connected agent draws its switch on");
            var setConnected = WorkspaceConnections.SetConnected;
            List<(WorkspaceConnections.AgentApp, bool)> partialAsked = [];
            WorkspaceConnections.SetConnected = (app, on, _) => { partialAsked.Add((app, on)); return Task.FromResult<string?>(null); };
            partial.IsChecked = false;
            Pump();
            Program.Check(partialAsked is [(WorkspaceConnections.AgentApp.ClaudeCode, false)],
                "Turning off a partially connected agent disconnects it");
            WorkspaceConnections.SetConnected = setConnected;
            WorkspaceConnections.Remember(WorkspaceConnections.AgentApp.ClaudeCode, true);
            SettingsActions.ReadConnectionFailure = _ => "Profile 2 could not be connected. Try again.";
            view.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            SettleVisual(view);
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "Profile 2 could not be connected. Try again." && x.IsVisible),
                "Reopening cached Settings surfaces a background profile connection failure");
            SettingsActions.ReadConnectionFailure = _ => null;
            SettingsActions.ReadProfileCounts = _ => (3, 3);
            Program.Agents(_ => AgentState.Connected);
            ShowSettled(view, "agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "Connected \u00b7 3 profiles"),
                "Settings names all connected configuration profiles without implying signed-in accounts");
            SettingsActions.ReadProfileCounts = profileCounts;
            Program.Agents(app => app == WorkspaceConnections.AgentApp.ClaudeCode ? AgentState.Found : AgentState.NotInstalled);
            ShowSettled(view, "agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "Found on this PC")
                && !Descendants<TextBlock>(view).Any(x => TextOf(x) == "Codex"), "Agents without an installation have no row");
            Program.Agents(_ => AgentState.NotInstalled);
            ShowSettled(view, "agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == FirstRunWindow.NoAgentLine),
                "Agents explains when neither supported app is installed");
            Program.Agents(_ => AgentState.Connected);
            ShowSettled(view, "agents");
            FindButton(view, "Copy setup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(copied, "Copy setup reaches clipboard seam");

            // A switch here is the owner's answer, the same as one on first launch: an agent they
            // turn off stays off, and the loop that connects agents installed later leaves it.
            HashSet<WorkspaceConnections.AgentApp> connected = [.. WorkspaceConnections.Supported];
            Program.Agents(app => connected.Contains(app) ? AgentState.Connected : AgentState.Found);
            WorkspaceConnections.SetConnected = (app, on, _) =>
            {
                asked.Add((app, on));
                if (on) connected.Add(app); else connected.Remove(app);
                return Task.FromResult<string?>(null);
            };
            AppSettingsStore.Update(s => s with { AgentsOff = [], ConnectAgents = true });
            ShowSettled(view, "agents");
            CheckBox claude = Descendants<CheckBox>(view)
                .First(box => AutomationProperties.GetName(box) == "Claude Code connected");
            asked.Clear();
            claude.IsChecked = false;
            Pump();
            Program.Check(asked is [(WorkspaceConnections.AgentApp.ClaudeCode, false)]
                && WorkspaceConnections.TurnedOff(WorkspaceConnections.AgentApp.ClaudeCode),
                "An agent turned off in Settings is remembered off, not only disconnected");
            Program.Check(!WorkspaceConnections.Missing(WorkspaceConnections.AgentApp.ClaudeCode),
                "Nothing reconnects an agent turned off in Settings, however long ARS runs");
            claude.IsChecked = true;
            Pump();
            Program.Check(connected.Contains(WorkspaceConnections.AgentApp.ClaudeCode)
                && !WorkspaceConnections.TurnedOff(WorkspaceConnections.AgentApp.ClaudeCode),
                "Turning it back on in Settings connects it and lets ARS keep it up again");
            Bound pause = view.BoundControls.Single(b => b.Label == "Pause every agent");
            AppSettingsStore.Update(s => s with { PauseHotkey = "Ctrl+Alt+F12" });
            Pump();
            Program.Check(Equals(pause.Shown(), AppSettingsStore.Current.PauseHotkey),
                "Pause every agent follows the shortcut stored by the host");
            static int Subscribers() =>
                (typeof(ModuleEntry).GetField(nameof(ModuleEntry.PauseShortcutTakenChanged), BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
            int subscribers = Subscribers();
            ModuleEntry.ReportPauseShortcut(false);
            view.UpdateLayout();
            Program.Check(!Descendants<TextBlock>(view).Any(x => TextOf(x) == "Another app is using this shortcut." && x.IsVisible),
                "Pause shortcut starts without a taken warning");
            ModuleEntry.ReportPauseShortcut(true);
            view.UpdateLayout();
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "Another app is using this shortcut." && x.IsVisible),
                "Pause shortcut shows Windows refusal");
            ShowSettled(view, "general");
            Program.Check(Subscribers() < subscribers, "Leaving Agents removes the shortcut follower");
            ShowSettled(view, "agents");
            Program.Check(Subscribers() == subscribers, "Returning to Agents adds one shortcut follower");

            SettingsFeatures.History = true;
            SettingsFeatures.BrowserData = true;
            scratchIsRunning = true;
            ShowSettled(view, "history");
            Program.Check(view.BoundControls.Any(b => b.Label == "Save screenshots"),
                "Screenshots control appears once history is wired");
            Program.Check(SettingsActions.FormatStorageBytes(1023 * 1024) == "1023 KB"
                && SettingsActions.FormatStorageBytes(1024 * 1024) == "1 MB"
                && SettingsActions.FormatStorageBytes(1024L * 1024 * 1024) == "1.0 GB",
                "Storage sizes use KB, MB and GB at their boundaries");
            Program.Check(SettingsView.StoragePercentages([412, 268, 136, 34]).SequenceEqual([48, 32, 16, 4]),
                "Storage meter rounds the reference kinds to whole percentages");
            Program.Check(SettingsView.StoragePercentages([1, 1, 1]).SequenceEqual([34, 33, 33])
                && SettingsView.StoragePercentages([null, 0, -1]).SequenceEqual([0, 0, 0])
                && SettingsView.StoragePercentages([long.MaxValue, long.MaxValue]).SequenceEqual([50, 50]),
                "Storage meter preserves its total and stable ties for empty and large sizes");
            await Task.Delay(250);
            Pump();
            Program.Check(Descendants<TextBlock>(view).Any(x => TextOf(x) == "850 MB used"),
                "Storage adds only the four displayed kind sizes");
            Border scratchRow = FindRow(view, "Scratch files");
            Button scratchClear = Descendants<Button>(scratchRow).First(x => Equals(x.Content, "Clear"));
            Program.Check(!scratchClear.IsEnabled && Equals(scratchClear.ToolTip, "Scratch is in use"),
                "Scratch Clear is disabled while Scratch runs");
            Border historyRow = FindRow(view, "Screenshots and history");
            Button historyClear = Descendants<Button>(historyRow).First(x => Equals(x.Content, "Clear"));
            historyClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettleVisual(historyRow);
            Program.Check(Descendants<TextBlock>(historyRow).Any(x => TextOf(x) == "Clear 412 MB of screenshots and history?"),
                "Clear names the data and asks first");
            Program.Check(!cleared, "Clear has not run before confirmation");
            Descendants<Button>(historyRow).First(x => Equals(x.Content, "Clear"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettleVisual(historyRow);
            Program.Check(cleared, "Confirmed Clear reaches its seam");
            Program.Check(!Descendants<TextBlock>(historyRow).Any(x => TextOf(x).StartsWith("Clear 412 MB", StringComparison.Ordinal)),
                "Confirmed Clear returns to its normal control");
            FindButton(view, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettleVisual(view);
            Program.Check(!deleted, "Delete all data asks first");
            FindButton(view, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettleVisual(view);
            Program.Check(deleted, "Confirmed Delete all data reaches its seam");

            bool back = false;
            view.BackRequested += () => back = true;
            PresentationSource source = PresentationSource.FromVisual(view)
                ?? throw new InvalidOperationException("Settings has no presentation source.");
            view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
            Program.Check(back, "Escape leaves Settings");
        }
        finally
        {
            ModuleEntry.ReportPauseShortcut(shortcutBefore);
            SettingsActions.SyncStartup = sync;
            agentSeams.Dispose();
            AppSettingsStore.Update(s => s with
                { AgentsOff = agentsBefore.AgentsOff, ConnectAgents = agentsBefore.ConnectAgents });
            SettingsActions.CopyText = copy;
            SettingsActions.OpenFolder = open;
            SettingsActions.Accounts = accounts;
            SettingsActions.HistoryBytes = historyBytes;
            SettingsActions.BrowserDataBytes = browserBytes;
            SettingsActions.ScratchBytes = scratchBytes;
            SettingsActions.LogsBytes = logsBytes;
            SettingsActions.Projects = projects;
            SettingsActions.ScratchRunning = scratchRunning;
            SettingsActions.ClearHistory = clearHistory;
            SettingsActions.ClearBrowserData = clearBrowser;
            SettingsActions.ClearScratch = clearScratch;
            SettingsActions.ClearLogs = clearLogs;
            SettingsActions.DeleteAllData = delete;
            SettingsFeatures.Accounts = SettingsFeatures.History = SettingsFeatures.BrowserData = SettingsFeatures.ProjectFiles = false;
            AppSettingsStore.Update(s => s with { Theme = themeBefore });
            window.Close();
        }
        await HostReopen();
    }

    static async Task HostReopen()
    {
        ShellPreferences saved = ShellPreferences.Read();
        var shell = ProbeWindow.OffScreen(new MainWindow());
        try
        {
            shell.Show();
            shell.ShowStack();
            shell.Width = 340;
            shell.Height = 560;
            shell.UpdateLayout();
            // Settings opens inside the card: its home, then a page that
            // slides in, then back twice, the window never changing size.
            SettingsView? cached = null;
            var cardBack = (Button)shell.FindName("CardBackButton");
            foreach (string category in new[] { "general", "agents", "history", "general" })
            {
                shell.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                var settings = shell.CardSettings!;
                bool sameView = cached is null || ReferenceEquals(cached, settings);
                cached = settings;
                Program.Check(settings.AtCardHome && cardBack.IsVisible && shell.ThemeButton.Visibility != Visibility.Visible,
                    "Settings opens on the card's home with a back arrow in the title bar");
                settings.Show(category);
                await Task.Delay(350);
                shell.UpdateLayout();
                var page = (ContentControl)settings.FindName("Page");
                var body = (FrameworkElement)page.Content;
                Program.Check(sameView && shell.DisplayMode == "cardsettings" && settings.IsVisible && !settings.AtCardHome
                    && settings.ActualWidth >= 300 && settings.ActualHeight >= 300
                    && body.ActualWidth >= 200 && body.ActualHeight >= 40
                    && body.TranslatePoint(new Point(0, 0), settings).X is >= 0 and < 40
                    && Descendants<Control>(body).Any(c => c.IsVisible && c.IsEnabled && c.ActualWidth > 20 && c.ActualHeight > 10),
                    "Settings in the card slides in visible, usable " + category + " content in its cached view");
                cardBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Program.Check(shell.DisplayMode == "cardsettings" && settings.AtCardHome,
                    "Back from " + category + " returns to the card's settings home");
                cardBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Program.Check(shell.DisplayMode == "stack" && Math.Abs(shell.ActualWidth - 340) < 2
                    && Math.Abs(shell.ActualHeight - 560) < 2 && shell.ThemeButton.Visibility == Visibility.Visible,
                    "Back again leaves Settings on the same compact strip");
                shell.Hide();
                shell.RestoreWorkspaceWindow();
                await Task.Delay(100);
            }

            // The sun and moon: one click picks the other theme and the glyph follows.
            bool wasDark = AppearanceManager.Dark;
            ThemeChoice chosen = AppSettingsStore.Current.Theme;
            shell.ThemeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Program.Check(AppearanceManager.Dark != wasDark && shell.ThemeIcon.Dark == AppearanceManager.Dark
                && AppSettingsStore.Current.Theme == (wasDark ? ThemeChoice.Light : ThemeChoice.Dark),
                "The title bar's sun and moon switches day and night in one click");
            shell.ThemeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Program.Check(AppearanceManager.Dark == wasDark && shell.ThemeIcon.Dark == wasDark,
                "Clicking it again switches back");
            AppSettingsStore.Update(s => s with { Theme = chosen });
        }
        finally
        {
            shell.Dispose();
            shell.Close();
            saved.Save();
        }
    }

    static Border FindRow(DependencyObject root, string label)
    {
        TextBlock text = Descendants<TextBlock>(root).First(x => TextOf(x) == label);
        for (DependencyObject? node = VisualTreeHelper.GetParent(text); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Border border && Descendants<Button>(border).Any()) return border;
        throw new InvalidOperationException("No Settings row for " + label);
    }

    static Button FindButton(DependencyObject root, string label) =>
        Descendants<Button>(root).First(button => Equals(button.Content, label)
            || button.Content is not string && System.Windows.Automation.AutomationProperties.GetName(button) == label);

    static bool Visible(DependencyObject root, string label) =>
        Descendants<Button>(root).Any(button => Equals(button.Content, label) && button.IsVisible);

    // Row labels are authored as Run inlines so TextBlock.Text is empty even while the words are
    // visibly rendered. Read the document range the same way a text automation client does.
    static string TextOf(TextBlock block) =>
        new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n');

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T wanted) yield return wanted;
            foreach (T deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    static void ShowSettled(SettingsView view, string category)
    {
        view.Show(category);
        SettleVisual(view);
    }

    static void SettleVisual(FrameworkElement element)
    {
        Pump();
        element.UpdateLayout();
        Pump();
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
