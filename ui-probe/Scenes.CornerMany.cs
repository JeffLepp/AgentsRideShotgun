using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// The corner window with more than one workspace. Three real workspaces, the real
/// host and the real window: every one of them has a tab, the one on the card is marked, the mouse
/// and the keyboard both move between them, the tabs stay where they were, and nothing is drawn off
/// the monitor at any corner the card can be placed in.
/// </summary>
static class CornerManyScenes
{
    internal static async Task Gate()
    {
        QuestionChecks();
        StoredWorkspace[] made = [.. Enumerable.Range(1, 3).Select(n => WorkspaceStore.Create("Corner tab fixture " + n))];
        // Three real desktops is enough to ask of this check; three cold Chromes is not.
        foreach (StoredWorkspace w in made) WorkspaceAccessStore.Write(w.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        WorkspaceRuntime[] running = [.. made.Select(WorkspaceRuntime.Start)];
        try { await Checks(made); }
        finally
        {
            WorkspacePeekHost.Stop();
            foreach (WorkspaceRuntime runtime in running) runtime.Dispose();
            foreach (StoredWorkspace w in made) WorkspaceStore.Delete(w.Id);
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.ComesAndGoes, CornerLeft = null, CornerTop = null, CornerPinned = false,
            });
        }
    }

    static async Task Checks(StoredWorkspace[] made)
    {
        AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Always, CornerLeft = null, CornerTop = null });
        WorkspacePeekHost.Start();
        InvokeHost("Rethink");
        await Task.Delay(120);
        WorkspacePeekWindow window = GateWindow() ?? throw new InvalidOperationException("The corner window never came up.");
        window.UpdateLayout();

        Button[] tabs = Tabs(window);
        Program.Check(Strip(window).Visibility == Visibility.Visible && tabs.Length == 3
            && made.All(w => tabs.Any(tab => (string)tab.Tag == w.Id)),
            "Three running workspaces put three tabs on the corner window, one for each");
        Program.Check(tabs.All(tab => Name(tab).Length > 0)
            && made.All(w => tabs.Any(tab => Name(tab) == w.Name)),
            "Each tab carries its own workspace's name");

        string front = FrontId();
        Program.Check(tabs.Count(Marked) == 1 && Marked(tabs.Single(tab => (string)tab.Tag == front)),
            "Exactly one tab is marked, and it is the workspace the card is showing");
        Program.Check(tabs.All(tab => tab.IsHitTestVisible && tab.ActualWidth > 0 && tab.ActualHeight > 0
                && System.Windows.Automation.AutomationProperties.GetName(tab).Length > 0),
            "Every tab is a real reachable control with a name a screen reader can read, not a card hidden behind another");

        // Rule 5: a picture nobody is looking at is not taken. The tabs are names and status dots, so
        // the only workspace this window photographs is still the one on the card.
        Program.Check(window.FindName("BackScreen") is null
            && !tabs.Any(tab => ((StackPanel)tab.Content).Children.OfType<Image>().Any()),
            "The tabs carry names and status, never a second picture: only the workspace on the card is captured");

        // Both kinds of program request name the same program now that the question tells them
        // apart, so being pending at once must not make one of them answer for the other.
        WorkspaceHandoffs asking = WorkspaceRuntime.Of(made[0].Id)!.Access!.Handoffs;
        WorkspaceHandoff open = asking.Request("program", "Notepad", "he asked for it");
        WorkspaceHandoff take = asking.Request("takeover", "Notepad", "it is already running outside");
        Program.Check(open.Id != take.Id && open.Kind == "program" && take.Kind == "takeover",
            "Opening a program on the owner's desktop and taking his copy into the workspace stay two separate questions");
        asking.CancelPending();

        string[] order = [.. tabs.Select(tab => (string)tab.Tag)];

        // The mouse.
        Button other = tabs.First(tab => (string)tab.Tag != front);
        string wanted = (string)other.Tag;
        other.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(120);
        Program.Check(FrontId() == wanted, "Clicking another workspace's tab brings that workspace to the card");
        Program.Check(Marked(Tabs(window).Single(tab => (string)tab.Tag == wanted)),
            "The mark moves with it, so which workspace is on screen is never in doubt");

        // With the pointer on the card: the hover hold stops other agents from swapping the screen,
        // never the owner's own click on a tab.
        window.ForceHoverForTests(true);
        Button third = Tabs(window).First(tab => (string)tab.Tag != wanted && (string)tab.Tag != front);
        string picked = (string)third.Tag;
        third.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(120);
        Program.Check(FrontId() == picked, "Clicking a tab while the pointer is on the card brings that workspace to the card");
        window.ForceHoverForTests(false);

        // The keyboard, on the real window: the same next/previous a row of tabs has anywhere else.
        int at = Array.IndexOf(order, FrontId());
        RaiseKey(window, Key.Right);
        await Task.Delay(120);
        Program.Check(FrontId() == order[(at + 1) % order.Length],
            "Right on the corner window moves to the next workspace");
        RaiseKey(window, Key.Left);
        await Task.Delay(120);
        Program.Check(FrontId() == order[at], "Left moves back to the one before it");
        Program.Check(Tabs(window).Select(tab => (string)tab.Tag).SequenceEqual(order),
            "Switching workspaces leaves the tabs in the order they started in");

        // Nothing off screen, at any corner. The strip rises above the saved front-card position, so
        // a card saved hard against the top of the work area used to draw its tabs off the top edge.
        Rect work = TestScreen.Work;
        Size card = WorkspacePeekPlacement.Card(AppSettingsStore.Current);
        bool inside = true;
        var places = new List<string>();
        foreach ((double left, double top) in new[]
        {
            (work.Left, work.Top), (work.Right - card.Width, work.Top),
            (work.Left, work.Bottom - card.Height), (work.Right - card.Width, work.Bottom - card.Height),
        })
        {
            AppSettingsStore.Update(s => s with { CornerLeft = left, CornerTop = top });
            InvokeHost("Rethink");
            await Task.Delay(120);
            WorkspacePeekWindow shown = GateWindow()!;
            Rect visible = new(shown.Left + WorkspacePeekWindow.ShadowMargin, shown.Top + WorkspacePeekWindow.ShadowMargin,
                shown.VisibleSize.Width, shown.VisibleSize.Height);
            places.Add($"{left:F0},{top:F0} -> {visible}");
            inside &= visible.Left >= work.Left - 0.5 && visible.Top >= work.Top - 0.5
                && visible.Right <= work.Right + 0.5 && visible.Bottom <= work.Bottom + 0.5;
        }
        System.IO.File.WriteAllLines(System.IO.Path.Combine(Program.Output, "corner-tabs-placement.txt"),
            [$"work area {work}", $"card {card}", .. places]);
        Program.Check(inside,
            "With three workspaces the whole corner window, tab strip included, stays on the monitor at every corner");

        // A picture of the real three-workspace corner, for a person to judge.
        if (((Image)GateWindow()!.PhotographCard()).Source is System.Windows.Media.Imaging.BitmapSource shot)
        {
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using var file = System.IO.File.Create(System.IO.Path.Combine(Program.Output, "corner-tabs.png"));
            png.Save(file);
        }
    }

    /// <summary>The one line the owner answers, per kind of request. A takeover runs the other way
    /// round from the rest - it closes his own copy and starts the program in the workspace - and
    /// used to borrow the "open it on your desktop" sentence with the difference written into the
    /// program's name to keep it true.</summary>
    static void QuestionChecks()
    {
        static string Ask(string kind, string target, string? arguments = null) => (string)InvokeHost("Question",
            new WorkspaceHandoff("id", kind, target, "why", "pending", "", DateTimeOffset.UtcNow, Arguments: arguments))!;
        Program.Check(Ask("takeover", "Notepad") == "Close your copy of Notepad and start it in the workspace?"
            && Ask("program", "Notepad") == "Open this on your desktop?\nNotepad\nWhy: why"
            && Ask("file", @"C:\somewhere\report.pdf") == "Open report.pdf on your desktop?"
            && Ask("url", "http://localhost:5173/") == "Open http://localhost:5173/ on your desktop?",
            "A takeover asks to close the owner's own copy and start it in the workspace; every other request still asks to open something on his desktop");
        // The owner approves what will actually run: a program's whole command line and the agent's
        // reason are on the card, not just its name.
        Program.Check(Ask("program", @"C:\Program Files\Tool\tool.exe", "--wipe \"C:\\data\"")
                == "Open this on your desktop?\n\"C:\\Program Files\\Tool\\tool.exe\" --wipe \"C:\\data\"\nWhy: why",
            "A program request's approval card shows the full command line, arguments included, and the agent's reason");
    }

    static StackPanel Strip(WorkspacePeekWindow window) => (StackPanel)window.FindName("TabStrip")!;

    static Button[] Tabs(WorkspacePeekWindow window) => [.. Strip(window).Children.OfType<Button>()];

    static string Name(Button tab) =>
        ((StackPanel)tab.Content).Children.OfType<TextBlock>().Select(t => t.Text).FirstOrDefault() ?? "";

    /// <summary>Whether a tab is the one on screen: the card's own material, outlined in the accent.</summary>
    static bool Marked(Button tab) => ReferenceEquals(tab.BorderBrush, tab.TryFindResource("AccentBrush"))
        && ReferenceEquals(tab.Background, tab.TryFindResource("CardBrush"));

    static void RaiseKey(WorkspacePeekWindow window, Key key) => window.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });

    static WorkspacePeekWindow? GateWindow() =>
        (WorkspacePeekWindow?)typeof(WorkspacePeekHost).GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    static string FrontId() =>
        (string?)typeof(WorkspacePeekHost).GetField("_frontId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) ?? "";

    static object? InvokeHost(string method, params object?[] args) =>
        typeof(WorkspacePeekHost).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
}
