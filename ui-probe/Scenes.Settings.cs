using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Harmless data shared by the body and whole-window Settings photographs.</summary>
static class SettingsFixtures
{
    const long Mb = 1024 * 1024;

    internal static IDisposable Use(SceneContext scene, string category)
    {
        IDisposable flags = SettingsFeatures.AllOnForScenes();
        var agents = SettingsActions.ReadAgent;
        var accounts = SettingsActions.Accounts;
        var history = SettingsActions.HistoryBytes;
        var browser = SettingsActions.BrowserDataBytes;
        var scratch = SettingsActions.ScratchBytes;
        var logs = SettingsActions.LogsBytes;
        var projects = SettingsActions.Projects;
        var running = SettingsActions.ScratchRunning;
        SettingsActions.ReadAgent = _ => AgentState.Connected;
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
            SettingsActions.ReadAgent = agents;
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
        view.Show(category);
        return view;
    }

    static async Task<FrameworkElement> Page(SceneContext scene, string category)
    {
        using IDisposable fixture = SettingsFixtures.Use(scene, category);
        SettingsView view = Host(scene, category);
        await scene.Settle(550);
        return view;
    }

    [Scene("settings-agents", "05-settings-agents", 121, 61, 1198, 784)]
    static Task<FrameworkElement> Agents(SceneContext scene) => Page(scene, "agents");

    [Scene("settings-accounts", "06-settings-accounts", 121, 61, 1198, 784)]
    static Task<FrameworkElement> Accounts(SceneContext scene) => Page(scene, "accounts");

    [Scene("settings-storage", "15-settings-storage", 121, 61, 1198, 784)]
    static Task<FrameworkElement> Storage(SceneContext scene) => Page(scene, "history");

    [Scene("settings-general", "")]
    static Task<FrameworkElement> General(SceneContext scene) => Page(scene, "general");

    internal static async Task Gate()
    {
        var sync = SettingsActions.SyncStartup;
        var readAgent = SettingsActions.ReadAgent;
        var connect = SettingsActions.Connect;
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
        var shortcutsBefore = ModuleEntry.ShortcutsTaken;
        bool opened = false, cleared = false, deleted = false, copied = false, startupSynced = false;
        bool scratchIsRunning = false;
        SettingsActions.SyncStartup = _ => { startupSynced = true; return true; };
        SettingsActions.ReadAgent = _ => AgentState.Connected;
        SettingsActions.Connect = (_, _) => Task.FromResult<string?>(null);
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
        var window = new Window
        {
            Content = view, Width = 1198, Height = 784, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = at.X, Top = at.Y,
        };
        window.Show();
        try
        {
            Program.Check(view.AvailableCategories.SequenceEqual(["general", "agents", "history"]),
                "Only active Settings categories appear before Accounts is wired");
            SettingsFeatures.Accounts = true;
            view.Show("general");
            Program.Check(view.AvailableCategories.SequenceEqual(["general", "agents", "accounts", "history"]),
                "Settings categories appear in the four-page order");
            foreach (var (oldId, expected) in new[]
                { ("control", "agents"), ("corner", "general"), ("privacy", "history"), ("about", "general"), ("browser", "accounts") })
            {
                view.Show(oldId);
                Program.Check(view.Category == expected, oldId + " opens " + expected);
            }

            string[] retired = ["Where agents go", "Running at once", "Sleep when quiet", "Remind agents",
                "Carry on after you stop for", "Agents open things on your desktop", "Open the browser early",
                "Share sign-ins across workspaces", "Continuous every", "Keep history for", "Logs folder"];
            foreach (string category in new[] { "general", "agents", "accounts", "history" })
            {
                view.Show(category);
                view.UpdateLayout();
                string visibleText = string.Join("|", Descendants<TextBlock>(view).Where(x => x.IsVisible).Select(x => x.Text));
                Program.Check(retired.All(label => !visibleText.Contains(label, StringComparison.Ordinal)),
                    category + " has no retired rows");
            }

            view.Show("general");
            Bound corner = view.BoundControls.Single(b => b.Label == "Show the corner window");
            corner.Choose(false);
            Program.Check(AppSettingsStore.Current.CornerShow == CornerShow.Off, "Corner switch turns automatic showing off");
            corner.Choose(true);
            Program.Check(AppSettingsStore.Current.CornerShow == CornerShow.ComesAndGoes, "Corner switch restores automatic showing");
            Bound startup = view.BoundControls.Single(b => b.Label == "Start with Windows");
            startup.Choose(true);
            startupSynced = false;
            startup.Choose(false);
            Program.Check(startupSynced, "Start with Windows reaches its startup seam");
            FindButton(view, "Open Deskweave data").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(opened, "Open Deskweave data reaches Explorer seam");
            Bound theme = view.BoundControls.Single(b => b.Label == "Theme");
            theme.Choose(ThemeChoice.Dark); Pump();
            Color dark = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            theme.Choose(ThemeChoice.Light); Pump();
            Color light = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            Program.Check(dark != light && !AppearanceManager.Dark, "Theme repaints Settings live");

            view.Show("agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => x.Text == "Claude Code")
                && Descendants<TextBlock>(view).Any(x => x.Text == "Codex"), "Installed agents appear");
            SettingsActions.ReadAgent = app => app == WorkspaceConnections.AgentApp.ClaudeCode ? AgentState.Found : AgentState.NotInstalled;
            view.Show("agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => x.Text == "Found on this PC")
                && !Descendants<TextBlock>(view).Any(x => x.Text == "Codex"), "Agents without an installation have no row");
            SettingsActions.ReadAgent = _ => AgentState.NotInstalled;
            view.Show("agents");
            Program.Check(Descendants<TextBlock>(view).Any(x => x.Text == "No supported agent found on this PC"),
                "Agents explains when neither supported app is installed");
            SettingsActions.ReadAgent = _ => AgentState.Connected;
            view.Show("agents");
            FindButton(view, "Copy setup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(copied, "Copy setup reaches clipboard seam");
            Bound pause = view.BoundControls.Single(b => b.Label == "Pause every agent");
            AppSettingsStore.Update(s => s with { PauseHotkey = "Ctrl+Alt+F12" });
            Pump();
            Program.Check(Equals(pause.Shown(), AppSettingsStore.Current.PauseHotkey),
                "Pause every agent follows the shortcut stored by the host");
            static int Subscribers() =>
                (typeof(ModuleEntry).GetField(nameof(ModuleEntry.ShortcutsTakenChanged), BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
            int subscribers = Subscribers();
            ModuleEntry.ReportShortcuts(cornerTaken: false, pauseTaken: false);
            view.UpdateLayout();
            Program.Check(!Descendants<TextBlock>(view).Any(x => x.Text == "Another app is using this shortcut." && x.IsVisible),
                "Pause shortcut starts without a taken warning");
            ModuleEntry.ReportShortcuts(cornerTaken: false, pauseTaken: true);
            view.UpdateLayout();
            Program.Check(Descendants<TextBlock>(view).Any(x => x.Text == "Another app is using this shortcut." && x.IsVisible),
                "Pause shortcut shows Windows refusal");
            view.Show("general");
            Program.Check(Subscribers() < subscribers, "Leaving Agents removes the shortcut follower");
            view.Show("agents");
            Program.Check(Subscribers() == subscribers, "Returning to Agents adds one shortcut follower");

            SettingsFeatures.History = true;
            SettingsFeatures.BrowserData = true;
            scratchIsRunning = true;
            view.Show("history");
            Program.Check(view.BoundControls.Any(b => b.Label == "Save screenshots"),
                "Screenshots control appears once history is wired");
            Program.Check(SettingsActions.FormatStorageBytes(1023 * 1024) == "1023 KB"
                && SettingsActions.FormatStorageBytes(1024 * 1024) == "1 MB"
                && SettingsActions.FormatStorageBytes(1024L * 1024 * 1024) == "1.0 GB",
                "Storage sizes use KB, MB and GB at their boundaries");
            await Task.Delay(250);
            Pump();
            Program.Check(Descendants<TextBlock>(view).Any(x => x.Text == "850 MB used"),
                "Storage adds only the four displayed kind sizes");
            Border scratchRow = FindRow(view, "Scratch files");
            Button scratchClear = Descendants<Button>(scratchRow).First(x => Equals(x.Content, "Clear"));
            Program.Check(!scratchClear.IsEnabled && Equals(scratchClear.ToolTip, "Scratch is in use"),
                "Scratch Clear is disabled while Scratch runs");
            Border historyRow = FindRow(view, "Screenshots and history");
            Button historyClear = Descendants<Button>(historyRow).First(x => Equals(x.Content, "Clear"));
            historyClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(Descendants<TextBlock>(historyRow).Any(x => x.Text == "Clear 412 MB of screenshots and history?"),
                "Clear names the data and asks first");
            Program.Check(!cleared, "Clear has not run before confirmation");
            Descendants<Button>(historyRow).First(x => Equals(x.Content, "Clear"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(cleared, "Confirmed Clear reaches its seam");
            Program.Check(!Descendants<TextBlock>(historyRow).Any(x => x.Text.StartsWith("Clear 412 MB", StringComparison.Ordinal)),
                "Confirmed Clear returns to its normal control");
            FindButton(view, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(!deleted, "Delete all data asks first");
            FindButton(view, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            ModuleEntry.ReportShortcuts(shortcutsBefore.Corner, shortcutsBefore.Pause);
            SettingsActions.SyncStartup = sync;
            SettingsActions.ReadAgent = readAgent;
            SettingsActions.Connect = connect;
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
    }

    static Border FindRow(DependencyObject root, string label)
    {
        TextBlock text = Descendants<TextBlock>(root).First(x => x.Text == label);
        for (DependencyObject? node = VisualTreeHelper.GetParent(text); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Border border && Descendants<Button>(border).Any()) return border;
        throw new InvalidOperationException("No Settings row for " + label);
    }

    static Button FindButton(DependencyObject root, string label) =>
        Descendants<Button>(root).First(button => Equals(button.Content, label));

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T wanted) yield return wanted;
            foreach (T deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
