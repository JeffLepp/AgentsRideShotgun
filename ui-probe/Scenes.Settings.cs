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
        SettingsView view = Host(scene, "control");
        await scene.Settle();
        return view;
    }

    [Scene("settings-browser", "06-settings-browser", 121, 61, 1198, 784)]
    static async Task<FrameworkElement> BrowserPage(SceneContext scene)
    {
        StoredWorkspace shop = scene.Workspace("shop");
        var fixtureAccounts = new[]
        {
            new SettingsAccount("Google", "you@gmail.com", Color.FromRgb(0x4A, 0x7B, 0xF7)),
            new SettingsAccount("GitHub", "yourname", Color.FromRgb(0x24, 0x29, 0x2F)),
            new SettingsAccount("Stripe", "Test mode", Color.FromRgb(0x63, 0x5B, 0xFF)),
        };
        var previousAccounts = SettingsActions.Accounts;
        bool previousFlag = SettingsFeatures.Accounts;
        SettingsActions.Accounts = () => fixtureAccounts;
        SettingsFeatures.Accounts = true;
        // Naming a concrete theme (rather than leaving FollowWindows) keeps this write from making
        // AppearanceManager re-follow Windows and undo the theme Mvp.Run just forced for this pass.
        AppSettingsStore.Update(s => s with
        {
            Theme = AppearanceManager.Choice,
            AccountScopes = new Dictionary<string, string> { ["Stripe|Test mode"] = shop.Id },
        });
        SettingsView view = Host(scene, "browser");
        // The tree is already built; restoring these before the screenshot keeps every later scene honest.
        SettingsFeatures.Accounts = previousFlag;
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
        }
        finally
        {
            SettingsActions.SyncStartup = previousSync;
            SettingsActions.Connect = previousConnect;
            SettingsActions.DeleteAllData = previousDelete;
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
