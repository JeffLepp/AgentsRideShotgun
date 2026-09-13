using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Settings pages: references 05, 06, and the eight pages no reference draws.</summary>
static class SettingsScenes
{
    // The window carries no chrome of its own: what is photographed is the 1198 x 784 body the hub
    // hosts below its 40 DIP title bar (reference crop 121, 61, 1198, 784).
    static SettingsView Host(SceneContext scene, string category)
    {
        var view = new SettingsView { Width = 1198, Height = 784 };
        Point at = SceneContext.OffScreen;
        Window window = scene.Own(new Window
        {
            Content = view,
            Width = 1198,
            Height = 784,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = at.X,
            Top = at.Y,
        });
        window.Show();
        view.Show(category);
        return view;
    }

    [Scene("settings-control", "05-settings-control", 121, 61, 1198, 784)]
    static async Task<FrameworkElement> ControlPage(SceneContext scene)
    {
        using IDisposable flags = SettingsFeatures.AllOnForScenes();
        SettingsView view = Host(scene, "control");
        await scene.Settle();
        return view;
    }

    [Scene("settings-browser", "06-settings-browser", 121, 61, 1198, 784)]
    static async Task<FrameworkElement> BrowserPage(SceneContext scene)
    {
        using IDisposable flags = SettingsFeatures.AllOnForScenes();
        StoredWorkspace shop = scene.Workspace("shop");
        var fixtureAccounts = new[]
        {
            new SettingsAccount("Google", "you@gmail.com", Color.FromRgb(0x4A, 0x7B, 0xF7)),
            new SettingsAccount("GitHub", "yourname", Color.FromRgb(0x24, 0x29, 0x2F)),
            new SettingsAccount("Stripe", "Test mode", Color.FromRgb(0x63, 0x5B, 0xFF)),
        };
        var previousAccounts = SettingsActions.Accounts;
        SettingsActions.Accounts = () => fixtureAccounts;
        // Naming a concrete theme (rather than leaving FollowWindows) keeps this write from making
        // AppearanceManager re-follow Windows and undo the theme Mvp.Run just forced for this pass.
        AppSettingsStore.Update(s => s with
        {
            Theme = AppearanceManager.Choice,
            AccountScopes = new Dictionary<string, string> { ["Stripe|Test mode"] = shop.Id },
        });
        SettingsView view = Host(scene, "browser");
        // The tree is already built; restoring this before the screenshot keeps every later scene honest.
        SettingsActions.Accounts = previousAccounts;
        await scene.Settle();
        return view;
    }

    // The eight pages no reference draws (MVP_SPEC Surfaces 4): captured for review, not compared.
    [Scene("settings-general", "")]
    static async Task<FrameworkElement> GeneralPage(SceneContext scene) { var view = Host(scene, "general"); await scene.Settle(); return view; }

    [Scene("settings-agents", "")]
    static async Task<FrameworkElement> AgentsPage(SceneContext scene) { var view = Host(scene, "agents"); await scene.Settle(); return view; }

    [Scene("settings-corner", "")]
    static async Task<FrameworkElement> CornerPage(SceneContext scene) { var view = Host(scene, "corner"); await scene.Settle(); return view; }

    [Scene("settings-alerts", "")]
    static async Task<FrameworkElement> AlertsPage(SceneContext scene) { var view = Host(scene, "alerts"); await scene.Settle(); return view; }

    [Scene("settings-history", "")]
    static async Task<FrameworkElement> HistoryPage(SceneContext scene) { var view = Host(scene, "history"); await scene.Settle(); return view; }

    [Scene("settings-perf", "")]
    static async Task<FrameworkElement> PerfPage(SceneContext scene) { var view = Host(scene, "perf"); await scene.Settle(); return view; }

    [Scene("settings-privacy", "")]
    static async Task<FrameworkElement> PrivacyPage(SceneContext scene) { var view = Host(scene, "privacy"); await scene.Settle(); return view; }

    [Scene("settings-about", "")]
    static async Task<FrameworkElement> AboutPage(SceneContext scene) { var view = Host(scene, "about"); await scene.Settle(); return view; }

    /// <summary>Behavior checks for Settings, run at the end of the UI gate. Never touches the owner's
    /// registry, agent configs or files: SyncStartup, Connect and DeleteAllData are all swapped for
    /// harmless stand-ins first.</summary>
    internal static Task Gate()
    {
        var previousSync = SettingsActions.SyncStartup;
        var previousConnect = SettingsActions.Connect;
        var previousDelete = SettingsActions.DeleteAllData;
        ThemeChoice previousTheme = AppSettingsStore.Current.Theme;
        SettingsActions.SyncStartup = _ => true;
        SettingsActions.Connect = (_, _) => Task.FromResult<string?>(null);
        SettingsActions.DeleteAllData = _ => { };
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
            foreach (string category in new[]
                     { "general", "agents", "control", "browser", "corner", "alerts", "history", "perf", "privacy", "about" })
            {
                view.Show(category);
                Program.Check(view.Category == category, "Show(\"" + category + "\") opens that category");
                foreach (Bound bound in view.BoundControls)
                {
                    if (bound.Values.Count == 0) continue;
                    foreach (object value in bound.Values)
                    {
                        bound.Choose(value);
                        Program.Check(Equals(bound.Shown(), value),
                            category + " > " + bound.Label + " shows " + value + " once chosen");
                        Program.Check(Equals(bound.Read(AppSettingsStore.Current), value),
                            category + " > " + bound.Label + " writes " + value + " to the store");
                    }
                }
            }

            // Dropdown choices never exceed what AppSettings.Sane allows for that field.
            view.Show("history");
            Bound sleep = view.BoundControls.First(b => b.Label == "Keep history for");
            Program.Check(sleep.Values.All(v => new AppSettings { KeepHistoryDays = (int)v }.Sane().KeepHistoryDays == (int)v),
                "Keep history for offers only what Sane keeps");
            view.Show("control");
            Bound carryOn = view.BoundControls.First(b => b.Label == "Carry on after you stop for");
            Program.Check(carryOn.Values.All(v => new AppSettings { CarryOnSeconds = (int)v }.Sane().CarryOnSeconds == (int)v),
                "Carry on after you stop for offers only what Sane keeps");

            // History > Continuous every is enabled only once Save screenshots is Continuous.
            view.Show("history");
            Bound save = view.BoundControls.First(b => b.Label == "Save screenshots");
            Bound continuous = view.BoundControls.First(b => b.Label == "Continuous every");
            save.Choose(ScreenshotMode.KeySteps);
            Pump();
            Program.Check(!((ComboBox)continuous.Control).IsEnabled, "Continuous every is disabled while Save screenshots is Key steps");
            save.Choose(ScreenshotMode.Continuous);
            Pump();
            Program.Check(((ComboBox)continuous.Control).IsEnabled, "Continuous every enables once Save screenshots is Continuous");

            // Escape raises BackRequested, same as the "Workspaces" row.
            bool back = false;
            view.BackRequested += () => back = true;
            PresentationSource source = PresentationSource.FromVisual(view)
                ?? throw new InvalidOperationException("Settings has no presentation source to raise Escape on.");
            view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
            Program.Check(back, "Escape raises BackRequested");

            // Hidden-until-wired: Remind agents and the account list stay out of a merged build. The
            // control is still built and wired (SettingsFeatures.RemindAgents flips it on for review),
            // just not shown, so the check is on-screen visibility, not whether it exists.
            view.Show("agents");
            Bound remind = view.BoundControls.First(b => b.Label == "Remind agents to test in Deskweave");
            Program.Check(!((FrameworkElement)remind.Control).IsVisible,
                "Remind agents to test in Deskweave stays collapsed until Wave 2 turns it on");
            view.Show("browser");
            Program.Check(SettingsActions.Accounts().Count == 0 && !SettingsFeatures.Accounts,
                "The signed-in account list stays hidden and empty until Wave 2 turns it on");

            // Theme is the one page 05/06 draws that is already wired past the store: choosing it
            // repaints the app while Settings stays open, on the same SettingsView instance used
            // throughout this gate, not just a value AppSettingsStore remembers.
            view.Show("general");
            Bound theme = view.BoundControls.First(b => b.Label == "Theme");
            theme.Choose(ThemeChoice.Dark);
            Pump();
            Program.Check(AppearanceManager.Dark, "Theme > Dark turns AppearanceManager.Dark on while Settings stays open");
            Color dark = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            theme.Choose(ThemeChoice.Light);
            Pump();
            Program.Check(!AppearanceManager.Dark, "Theme > Light turns AppearanceManager.Dark back off while Settings stays open");
            Color light = ((SolidColorBrush)Application.Current!.Resources["WindowBrush"]).Color;
            Program.Check(dark != light, "Theme repaints the WindowBrush resource live between Dark and Light");

            // Every other row WAVE1.md C.3 gates behind a SettingsFeatures flag: built and wired
            // (the sweep above already proved each saves its value), but collapsed until its flag is
            // on, and visible once it is.
            void ChecksFlag(string category, string label, Func<bool> read, Action<bool> write)
            {
                bool was = read();
                write(false);
                view.Show(category);
                view.UpdateLayout(); // IsVisible only reflects a Visibility change after a layout pass.
                var off = (FrameworkElement)view.BoundControls.First(b => b.Label == label).Control;
                Program.Check(!off.IsVisible, category + " > " + label + " is hidden by default");
                write(true);
                view.Show(category);
                view.UpdateLayout();
                var on = (FrameworkElement)view.BoundControls.First(b => b.Label == label).Control;
                Program.Check(on.IsVisible, category + " > " + label + " shows once its flag turns on");
                write(was);
            }
            ChecksFlag("agents", "Where agents go", () => SettingsFeatures.AgentScheduling, v => SettingsFeatures.AgentScheduling = v);
            ChecksFlag("control", "Agents open things on your desktop", () => SettingsFeatures.DesktopRequests, v => SettingsFeatures.DesktopRequests = v);
            ChecksFlag("browser", "Share sign-ins across workspaces", () => SettingsFeatures.AgentBrowser, v => SettingsFeatures.AgentBrowser = v);
            ChecksFlag("privacy", "Pause commands after reading a web page", () => SettingsFeatures.PauseAfterWebPage, v => SettingsFeatures.PauseAfterWebPage = v);
            ChecksFlag("alerts", "When an agent needs you", () => SettingsFeatures.Notifications, v => SettingsFeatures.Notifications = v);
            ChecksFlag("history", "Save screenshots", () => SettingsFeatures.History, v => SettingsFeatures.History = v);

            // A category left with nothing wired on leaves the nav; it rejoins once a flag that
            // shows something on it turns on.
            view.Show("general");
            Program.Check(!view.AvailableCategories.Contains("alerts"), "Notifications leaves the nav while its flag is off");
            Program.Check(!view.AvailableCategories.Contains("browser"), "Browser & accounts leaves the nav while both its flags are off");
            SettingsFeatures.Notifications = true;
            SettingsFeatures.AgentBrowser = true;
            view.Show("general");
            Program.Check(view.AvailableCategories.Contains("alerts"), "Notifications rejoins the nav once its flag turns on");
            Program.Check(view.AvailableCategories.Contains("browser"), "Browser & accounts rejoins the nav once a flag turns on");
            SettingsFeatures.Notifications = false;
            SettingsFeatures.AgentBrowser = false;

            // Each Show() rebuilds the page from scratch (_bound.Clear(), Page.Content = Build(id)),
            // but Page and this view both stay connected to the same PresentationSource throughout -
            // a row Show() replaces is never actually disconnected from a live tree, so its Unloaded
            // does not fire (checked here first; it does not). SettingsView._cleanup is what actually
            // unsubscribes a shortcut row's error text from ModuleEntry.ShortcutsTakenChanged when
            // Show() moves on. Counting the event's own invocation list (reflection: the accessors
            // are internal) proves the follower is really gone, not just that the row stopped
            // updating because nothing tells it to any more.
            static int TakenChangedSubscribers() =>
                (typeof(ModuleEntry).GetField(nameof(ModuleEntry.ShortcutsTakenChanged), BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
            view.Show("control");
            view.UpdateLayout();
            int subscribedOnControl = TakenChangedSubscribers();
            view.Show("general");
            view.UpdateLayout();
            Program.Check(TakenChangedSubscribers() < subscribedOnControl,
                "Leaving the Control page unsubscribes its shortcut rows' followers (SettingsView._cleanup, not Unloaded, which does not fire for a page Show() replaces)");
            view.Show("control");
            view.UpdateLayout();
            Program.Check(TakenChangedSubscribers() == subscribedOnControl,
                "Returning to Control resubscribes once per row, not stacking a follower left over from an earlier visit");

            // A shortcut Windows refuses shows "Another app is using this shortcut." under that row,
            // following ModuleEntry.ShortcutsTakenChanged, and clears the same way.
            view.Show("control");
            var pauseControl = (FrameworkElement)view.BoundControls.First(b => b.Label == "Pause every agent").Control;
            var cornerControl = (FrameworkElement)view.BoundControls.First(b => b.Label == "Show the corner window").Control;
            TextBlock ErrorUnder(FrameworkElement shortcut)
            {
                var grid = (Grid)VisualTreeHelper.GetParent(shortcut)!;
                var text = (StackPanel)grid.Children[0]!;
                return (TextBlock)text.Children[1];
            }
            // The corner host reports what Windows really refused when it registered at startup (a
            // running Deskweave or the corner gate may hold a combination), so start this check from
            // a known "nothing taken" and put the real state back at the end.
            var realTaken = ModuleEntry.ShortcutsTaken;
            ModuleEntry.ReportShortcuts(cornerTaken: false, pauseTaken: false);
            view.UpdateLayout();
            TextBlock pauseError = ErrorUnder(pauseControl);
            TextBlock cornerError = ErrorUnder(cornerControl);
            Program.Check(!pauseError.IsVisible && !cornerError.IsVisible,
                "Neither shortcut shows a taken error before ModuleEntry.ReportShortcuts says so");
            ModuleEntry.ReportShortcuts(cornerTaken: true, pauseTaken: false);
            view.UpdateLayout();
            Program.Check(cornerError.IsVisible && !pauseError.IsVisible,
                "Show the corner window shows \"Another app is using this shortcut.\" once ReportShortcuts reports it taken");
            ModuleEntry.ReportShortcuts(cornerTaken: false, pauseTaken: true);
            view.UpdateLayout();
            Program.Check(pauseError.IsVisible && !cornerError.IsVisible,
                "The taken error moves to Pause every agent and clears off Show the corner window as ReportShortcuts changes");
            ModuleEntry.ReportShortcuts(cornerTaken: false, pauseTaken: false);
            view.UpdateLayout();
            Program.Check(!pauseError.IsVisible && !cornerError.IsVisible,
                "Both shortcut-taken errors clear once ReportShortcuts reports neither taken");
            ModuleEntry.ReportShortcuts(realTaken.Corner, realTaken.Pause);
        }
        finally
        {
            SettingsActions.SyncStartup = previousSync;
            SettingsActions.Connect = previousConnect;
            SettingsActions.DeleteAllData = previousDelete;
            AppSettingsStore.Update(s => s with { Theme = previousTheme });
            window.Close();
        }
        return Task.CompletedTask;
    }

    // AppSettingsStore.Changed is announced synchronously, but SettingsView defensively marshals its
    // own Follow() through BeginInvoke (Changed can be raised off the UI thread). Draining the
    // dispatcher's queue makes a cross-control follower (Continuous every watching Save screenshots)
    // visible to the gate the way it would be after the next real frame.
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
