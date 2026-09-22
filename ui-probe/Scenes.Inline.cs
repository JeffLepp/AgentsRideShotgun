using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

internal static class InlineScenes
{
    internal static async Task Gate()
    {
        ShellPreferences preferences = ShellPreferences.Read();
        ThemeChoice theme = AppSettingsStore.Current.Theme;
        var shell = ProbeWindow.OffScreen(new MainWindow());
        StoredWorkspace first = WorkspaceStore.Create("Inline checkout review");
        StoredWorkspace second = WorkspaceStore.Create("Inline a-rather-long-workspace-name-for-release-testing");
        WorkspaceAccessStore.Write(first.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
        WorkspaceAccessStore.Write(second.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
        Mvp.Save(Mvp.Load(Path.Combine(Mvp.References(), "sites", "shop.png")), WorkspaceStore.LastFrameOf(first.Id));
        int initialRunning = WorkspaceRuntime.Running.Count;
        try
        {
            shell.Show();
            shell.ShowStack();
            shell.Width = 340;
            shell.Height = 560;
            // Existing gate workspaces stay in the store, but this fixture shows only its own rows.
            shell.FilterBox.Text = "Inline ";
            await Until(() => shell.Hub.Find(first.Id)?.Preview is not null, "the saved inline picture loads");
            ClickRow(shell, first.Id);
            await Until(() => Inline(shell, first.Id) is not null, "the first inline row expands");
            WorkspaceInlineView view = Inline(shell, first.Id)!;
            Program.Check(shell.DisplayMode == "stack" && Math.Abs(shell.ActualWidth - 340) < 2
                && WorkspaceRuntime.Running.Count == initialRunning && !view.Interactive
                && ((TextBlock)view.FindName("StateText")).Text.Contains("Last picture"),
                "A stopped workspace expands its labelled last picture inside the strip without starting a screen");
            ClickRow(shell, second.Id);
            await Until(() => Inline(shell, second.Id) is not null, "the second inline row expands");
            Program.Check(shell.Hub.Asleep.Count(e => e.Expanded) == 1 && !shell.Hub.Find(first.Id)!.Expanded
                && Inline(shell, second.Id)!.ScreenInput is null,
                "Expanding another recent row collapses the first and an empty picture cannot accept input");
            ClickRow(shell, second.Id);
            Program.Check(!shell.Hub.Find(second.Id)!.Expanded, "Clicking an expanded row collapses its preview");

            ClickRow(shell, first.Id);
            await Until(() => Inline(shell, first.Id) is not null, "the saved picture reopens");
            Click((Button)Inline(shell, first.Id)!.FindName("StartButton"));
            await Until(() => WorkspaceRuntime.Of(first.Id) is not null && Inline(shell, first.Id)?.Interactive == true,
                "Start screen produces a live inline frame", 12000);
            WorkspaceRuntime runtime = WorkspaceRuntime.Of(first.Id)!;
            view = Inline(shell, first.Id)!;
            Program.Check(ReferenceEquals(shell.Hub.Find(first.Id)!.PreviewPlane, runtime.Plane)
                && ((Image)view.FindName("Screen")).Source is not null && !((Button)view.FindName("StartButton")).IsVisible,
                "Start screen uses the real runtime and only enables input after a frame from that exact runtime arrives");

            // The same input adapter used by the page sends a real click to a window on this desktop.
            // A workspace starts empty now, so the check brings its own.
            runtime.Computer!.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            await Until(() => runtime.Computer!.Windows().Count > 0, "the owned fixture window is ready");
            AgentWindow target = runtime.Computer!.Windows()[0];
            var picture = (Image)view.FindName("Screen");
            double scale = Math.Min(picture.ActualWidth / AgentDesktop.ScreenWidth, picture.ActualHeight / AgentDesktop.ScreenHeight);
            var point = new Point((picture.ActualWidth - AgentDesktop.ScreenWidth * scale) / 2 + (target.X + target.Width / 2.0) * scale,
                (picture.ActualHeight - AgentDesktop.ScreenHeight * scale) / 2 + (target.Y + target.Height / 2.0) * scale);
            Program.Check(view.ScreenInput!.Press(point, false), "A click in the inline picture is accepted by its real input adapter");
            await Until(() => Program.RootOf(runtime.Computer.LastClickedForTests) == target.Handle, "the inline click reaches its own desktop window");
            await Until(() => ((Button)view.FindName("ReturnButton")).IsVisible, "the owner hand-back action appears");
            Program.Check(runtime.Plane!.Driving == Driver.Owner && view.ScreenInput!.OwnsControl,
                "Clicking the inline screen gives this view the owner lease");
            Click((Button)view.FindName("ReturnButton"));
            Program.Check(runtime.Plane.Driving != Driver.Owner, "Give back releases the inline view's owner lease");

            runtime.Plane.OwnerTakes(); // An existing owner lease belongs to another surface or global pause.
            await Task.Delay(80);
            Program.Check(!((Button)view.FindName("ReturnButton")).IsVisible,
                "The inline view does not offer to release another surface's owner lease");
            ClickRow(shell, first.Id);
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Collapsing an unused inline view preserves a pre-existing owner lease");
            runtime.Plane.Release();
            ClickRow(shell, first.Id);
            await Until(() => Inline(shell, first.Id)?.Interactive == true, "the live picture reopens");
            view = Inline(shell, first.Id)!;
            view.ScreenInput!.SimulateClickForTests();
            ClickRow(shell, first.Id);
            Program.Check(runtime.Plane.Driving != Driver.Owner && !view.Interactive,
                "Collapsing a driven inline picture releases its input and owner lease immediately");
            ClickRow(shell, first.Id);
            await Until(() => Inline(shell, first.Id)?.Interactive == true, "the live picture opens before hiding");
            view = Inline(shell, first.Id)!;
            view.ScreenInput!.SimulateClickForTests();
            shell.WindowState = WindowState.Minimized;
            await Task.Delay(80);
            Program.Check(runtime.Plane.Driving != Driver.Owner && !view.Interactive,
                "Minimizing the strip releases inline input and its owner lease");
            shell.WindowState = WindowState.Normal;
            await Until(() => Inline(shell, first.Id)?.Interactive == true, "the live picture returns after restore");
            view = Inline(shell, first.Id)!;
            view.ScreenInput!.SimulateClickForTests();
            shell.Hide();
            Program.Check(runtime.Plane.Driving != Driver.Owner && !view.Interactive,
                "Hiding the hub releases inline input and its owner lease");
            shell.RestoreWorkspaceWindow();
            await Until(() => Inline(shell, first.Id)?.Interactive == true, "the inline picture returns after explicit reopen");

            view = Inline(shell, first.Id)!;
            Click((Button)view.FindName("MoreButton"));
            await Task.Delay(150);
            Program.Check(shell.DisplayMode == "wide" && shell.SelectedWorkspaceId == first.Id && !view.Interactive,
                "Show more opens the same workspace's full page and releases the hidden inline adapter");
            Click(shell.BackButton);
            await Task.Delay(100);
            Program.Check(shell.DisplayMode == "stack" && Math.Abs(shell.ActualWidth - 340) < 2,
                "The visible title-bar back action restores the narrow strip");
            Click(shell.SettingsButton);
            await Task.Delay(150);
            Program.Check(shell.DisplayMode == "cardsettings" && ((ContentControl)shell.FindName("CardSettingsSlot")).ActualWidth > 300
                && Math.Abs(shell.ActualWidth - 340) < 2,
                "The gear opens a laid-out Settings surface inside the strip");
            Click(shell.SettingsButton);
            await Task.Delay(100);
            Program.Check(shell.DisplayMode == "stack" && Math.Abs(shell.ActualWidth - 340) < 2,
                "Clicking the gear again returns to the compact strip");

            string captures = Path.Combine(Program.Output, "inline");
            Directory.CreateDirectory(captures);
            foreach (ThemeChoice look in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            {
                AppSettingsStore.Update(s => s with { Theme = look });
                foreach (int width in new[] { 320, 340 })
                {
                    shell.Width = width;
                    shell.Height = 560;
                    shell.UpdateLayout();
                    await Task.Delay(150);
                    view = Inline(shell, first.Id)!;
                    var more = (Button)view.FindName("MoreButton");
                    var screen = (Image)view.FindName("Screen");
                    Rect button = more.TransformToAncestor(shell).TransformBounds(new Rect(more.RenderSize));
                    Program.Check(screen.ActualWidth >= 270 && button.Left >= 0 && button.Right <= shell.ActualWidth
                        && ProbeWindow.IsOffScreen(shell),
                        $"The {width} DIP {look} inline preview and Show more action fit the strip off the owner's monitors");
                    Mvp.Save(Mvp.Photograph(shell), Path.Combine(captures, $"live-{look.ToString().ToLowerInvariant()}-{width}.png"));
                }
            }

            // An old image must never become interactive merely because another runtime now owns the ID.
            view = Inline(shell, first.Id)!;
            view.ScreenInput!.SimulateClickForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Fixture: the inline view owns control immediately before its runtime stops");
            runtime.Dispose();
            await Until(() => Inline(shell, first.Id) is { Interactive: false }, "the stopped inline view detaches");
            Program.Check(WorkspaceRuntime.Of(first.Id) is null && Inline(shell, first.Id)!.ScreenInput is null,
                "Stopping a runtime while its inline view owns control safely leaves the last picture read-only");
            WorkspaceRuntime replacement = WorkspaceRuntime.Start(WorkspaceStore.Find(first.Id)!);
            Program.Check(!Inline(shell, first.Id)!.Interactive
                && !ReferenceEquals(shell.Hub.Find(first.Id)!.PreviewPlane, replacement.Plane),
                "Restarting the same workspace does not make its retained old picture interactive");
            await Until(() => Inline(shell, first.Id)?.Interactive == true
                && ReferenceEquals(shell.Hub.Find(first.Id)!.PreviewPlane, replacement.Plane),
                "the replacement runtime provides its own frame", 12000);
            Program.Check(Inline(shell, first.Id)!.ScreenInput is not null,
                "The replacement runtime accepts inline input only after its own frame has arrived");
        }
        finally
        {
            shell.Dispose();
            shell.Close();
            WorkspaceRuntime.Of(first.Id)?.Dispose();
            WorkspaceRuntime.Of(second.Id)?.Dispose();
            WorkspaceStore.Delete(first.Id);
            WorkspaceStore.Delete(second.Id);
            AppSettingsStore.Update(s => s with { Theme = theme });
            preferences.Save();
        }
    }

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void ClickRow(MainWindow shell, string id) => Click(Descendants<Button>(shell.StackRoot)
        .Single(button => Equals(button.Tag, id) && AutomationProperties.GetName(button).Contains("preview for ")));
    static WorkspaceInlineView? Inline(MainWindow shell, string id) => Descendants<WorkspaceInlineView>(shell.StackRoot)
        .FirstOrDefault(view => view.IsVisible && view.DataContext is HubEntry entry && entry.Id == id);
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    static async Task Until(Func<bool> condition, string reason, int milliseconds = 6000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(60);
        if (!condition()) throw new InvalidOperationException("Inline fixture timed out waiting for " + reason + ".");
    }
}
