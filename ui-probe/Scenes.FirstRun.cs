using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// First launch: reference 07. Every check here goes through the
/// same seams Settings uses (<see cref="SettingsActions.ReadAgent"/>, <see cref="SettingsActions.Connect"/>),
/// so the gate never reaches a real agent's command or the owner's own configuration.
/// </summary>
static class FirstRunScenes
{
    [Scene("first-launch", "07-first-launch", 240, 44, 520, 560)]
    static async Task<FrameworkElement> FirstLaunch(SceneContext scene)
    {
        using IDisposable found = Detect(_ => AgentState.Found);
        var window = scene.Own(new FirstRunWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SceneContext.OffScreen.X,
            Top = SceneContext.OffScreen.Y,
            ShowActivated = false,
        });
        window.Show();
        await scene.Settle();
        return window;
    }

    internal static async Task Gate()
    {
        var readAgent = SettingsActions.ReadAgent;
        var connect = SettingsActions.Connect;
        AppSettings before = AppSettingsStore.Current;
        try
        {
            List<(WorkspaceConnections.AgentApp App, bool On)> asked = [];
            // A fresh PC for each check: nothing answered, no consent, and no connection asked for yet.
            void Reset()
            {
                asked.Clear();
                AppSettingsStore.Update(s => s with { FirstRunDone = false, ConnectAgents = false });
            }
            SettingsActions.Connect = (app, on) => { asked.Add((app, on)); return Task.FromResult<string?>(null); };

            // Both agents here: a row each, switch already on, and nothing written yet.
            Reset();
            SettingsActions.ReadAgent = _ => AgentState.Found;
            using (Shown open = Open())
            {
                Program.Check(Labels(open.Window).Contains("Claude Code") && Labels(open.Window).Contains("Codex"),
                    "First launch lists each supported agent found on this PC");
                Program.Check(Switches(open.Window).Count == 2 && Switches(open.Window).All(s => s.IsChecked == true),
                    "Detected agents come switched on, so the owner only has to press Start");
                Program.Check(asked.Count == 0 && !AppSettingsStore.Current.ConnectAgents,
                    "Nothing reaches an agent's configuration before Start is pressed");
                Program.Check(Buttons(open.Window).Count == 1 && Buttons(open.Window)[0].Content as string == FirstRunWindow.StartLabel,
                    "Start is the only button: no Skip, no advanced setup, no demo run");
                Program.Check(Descendants<TextBox>(open.Window).Count() == 0 && Descendants<ComboBox>(open.Window).Count() == 0,
                    "First launch asks for no workspace, folder, account or sign-in");
                Program.Check(Words(open.Window).Contains(FirstRunWindow.LocalLine),
                    "First launch says Deskweave runs only on this PC");
            }

            // Closing it changes nothing at all, and it is not asked again.
            Reset();
            using (Shown open = Open()) { }
            Program.Check(asked.Count == 0 && !AppSettingsStore.Current.ConnectAgents,
                "Closing first launch connects nothing and writes to no agent configuration");
            Program.Check(AppSettingsStore.Current.FirstRunDone && !FirstRunWindow.Needed,
                "First launch is answered by closing it too, and never asks a second time");

            // Start: consent and both connections, in one press.
            Reset();
            using (Shown open = Open()) await PressStart(open.Window);
            Program.Check(asked.Count == 2 && asked.All(a => a.On)
                && asked.Select(a => a.App).Order().SequenceEqual(WorkspaceConnections.Supported.Order()),
                "Start connects every switched-on agent, once each");
            Program.Check(AppSettingsStore.Current.ConnectAgents && AppSettingsStore.Current.FirstRunDone,
                "Start records the consent that connects agents installed later, without asking again");

            // A switch turned off is left alone; nothing else on the screen changes.
            Reset();
            using (Shown open = Open())
            {
                Switches(open.Window)[1].IsChecked = false;
                await PressStart(open.Window);
            }
            Program.Check(asked is [(WorkspaceConnections.AgentApp.ClaudeCode, true)],
                "An agent switched off on first launch is not connected");

            // One agent refuses: its own line, the window stays, and a second press retries only it.
            Reset();
            SettingsActions.Connect = (app, on) =>
            {
                asked.Add((app, on));
                return Task.FromResult(app == WorkspaceConnections.AgentApp.Codex ? "Codex did not accept the connection. Nothing else was changed." : null);
            };
            using (Shown open = Open())
            {
                await PressStart(open.Window);
                Program.Check(open.Window.IsVisible, "A connection that fails leaves first launch open instead of vanishing");
                Program.Check(Words(open.Window).Any(w => w.StartsWith("Codex did not accept", StringComparison.Ordinal)),
                    "A connection that fails says why, in one line, on that agent's own row");
                Program.Check(Buttons(open.Window)[0].Content as string == FirstRunWindow.TryAgainLabel,
                    "Start becomes Try again so the owner is not left at a dead end");
                asked.Clear();
                await PressStart(open.Window);
                Program.Check(asked is [(WorkspaceConnections.AgentApp.Codex, true)],
                    "Trying again retries only the agent that refused; the one that connected is left alone");
                Program.Check(Switches(open.Window)[1].IsEnabled && !Switches(open.Window)[0].IsEnabled,
                    "The switch comes back on the row that refused, so the owner can leave that agent out instead");
                open.Window.Close();
            }
            Program.Check(AppSettingsStore.Current.ConnectAgents,
                "Consent holds even when an agent refused, so it is connected on its own later");

            // With nothing supported installed there is nothing to set up.
            Reset();
            SettingsActions.Connect = (app, on) => { asked.Add((app, on)); return Task.FromResult<string?>(null); };
            SettingsActions.ReadAgent = _ => AgentState.NotInstalled;
            using (Shown open = Open())
            {
                Program.Check(Switches(open.Window).Count == 0 && Words(open.Window).Contains("No supported agent found on this PC"),
                    "With no supported agent installed, first launch says so and offers no switches");
                await PressStart(open.Window);
            }
            Program.Check(asked.Count == 0 && AppSettingsStore.Current.ConnectAgents,
                "Start with nothing installed still records consent, so an agent installed later connects itself");

            // One agent here, one not: the missing one has no row at all.
            Reset();
            SettingsActions.ReadAgent = app => app == WorkspaceConnections.AgentApp.ClaudeCode ? AgentState.Found : AgentState.NotInstalled;
            using (Shown open = Open())
                Program.Check(Labels(open.Window).Contains("Claude Code") && !Labels(open.Window).Contains("Codex"),
                    "An agent that is not on this PC has no row on first launch");

            Program.Check(WorkspaceMcp.Scope.Contains("Use Deskweave whenever your work needs a window", StringComparison.Ordinal)
                && WorkspaceMcp.Scope.Contains("Do not use Deskweave for anything else", StringComparison.Ordinal)
                && WorkspaceMcp.RouterInstructions.Contains(WorkspaceMcp.Scope, StringComparison.Ordinal),
                "Connecting carries the scoped instruction: windows yes, code and builds no");
            await Task.Delay(50);
        }
        finally
        {
            SettingsActions.ReadAgent = readAgent;
            SettingsActions.Connect = connect;
            AppSettingsStore.Update(_ => before);
        }
    }

    /// <summary>A first-launch window on screen, closed when the check that opened it is done.</summary>
    readonly record struct Shown(FirstRunWindow Window) : IDisposable
    {
        public void Dispose() { if (Window.IsVisible) Window.Close(); }
    }

    static Shown Open()
    {
        var window = new FirstRunWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SceneContext.OffScreen.X,
            Top = SceneContext.OffScreen.Y,
            ShowActivated = false,
        };
        window.Show();
        Pump();
        window.UpdateLayout();
        return new Shown(window);
    }

    /// <summary>Start is async void, as a click handler is; wait for the connections it started.</summary>
    static async Task PressStart(FirstRunWindow window)
    {
        Buttons(window)[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (int i = 0; i < 40 && Buttons(window).ElementAtOrDefault(0)?.IsEnabled == false; i++) await Task.Delay(25);
        Pump();
        if (window.IsVisible) window.UpdateLayout();
    }

    static IDisposable Detect(Func<WorkspaceConnections.AgentApp, AgentState> state)
    {
        var was = SettingsActions.ReadAgent;
        SettingsActions.ReadAgent = state;
        return new Restore(() => SettingsActions.ReadAgent = was);
    }

    sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    // The card's own rows, told apart from the title bar's close button by their style.
    static List<CheckBox> Switches(DependencyObject root) => [.. Descendants<CheckBox>(root)];
    static List<Button> Buttons(DependencyObject root) =>
        [.. Descendants<Button>(root).Where(b => b.Content is string)];
    static List<string> Labels(DependencyObject root) => Words(root);

    static List<string> Words(DependencyObject root) => [.. Descendants<TextBlock>(root)
        .Where(block => block.IsVisible)
        .Select(block => new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n'))];

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
