using System.Reflection;
using System.Windows;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

internal static class PresentationScenes
{
    internal static async Task Gate()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        Program.Check(WorkspacePresentation.CoversMonitor(monitor, monitor)
            && WorkspacePresentation.CoversMonitor(new Rect(-1920, 0, 1920, 1080), new Rect(-1920, 0, 1920, 1080)),
            "Fullscreen clients are recognised on primary and negative-coordinate secondary monitors");
        Program.Check(!WorkspacePresentation.CoversMonitor(new Rect(0, 0, 1920, 1040), monitor)
            && !WorkspacePresentation.CoversMonitor(new Rect(0, 32, 1920, 1048), monitor)
            && !WorkspacePresentation.CoversMonitor(new Rect(80, 80, 1000, 700), monitor),
            "A maximized work-area window, ordinary browser client and smaller window are not fullscreen");
        Program.Check(WorkspacePresentation.QuietState(2) && WorkspacePresentation.QuietState(3)
            && WorkspacePresentation.QuietState(4) && !WorkspacePresentation.QuietState(5) && !WorkspacePresentation.QuietState(7),
            "Windows busy, exclusive D3D and presentation modes suppress automatic surfaces; ordinary apps remain allowed");
        var work = new Rect(0, 0, 1920, 1040);
        var secondary = new Rect(-1920, 0, 1920, 1080);
        var secondaryWork = new Rect(-1920, 0, 1920, 1040);
        var game = new WorkspacePresentation.PresentationWindow(monitor, monitor, monitor, work);
        var browser = new WorkspacePresentation.PresentationWindow(work, new Rect(0, 32, 1920, 1008), monitor, work,
            NormalMaximized: true);
        var discord = new WorkspacePresentation.PresentationWindow(secondaryWork, new Rect(-1920, 32, 1920, 1008),
            secondary, secondaryWork, NormalMaximized: true);
        Program.Check(WorkspacePresentation.VisibleFullscreenMonitors([discord, game]).SequenceEqual([monitor]),
            "A normal foreground app on another monitor does not uncover automatic overlays above a visible fullscreen game");
        Program.Check(WorkspacePresentation.VisibleFullscreenMonitors([browser, game]).Length == 0
            && WorkspacePresentation.VisibleFullscreenMonitors([game, browser]).SequenceEqual([monitor]),
            "A normal maximized app covering the game permits automatic UI; the same app behind the game does not");
        Program.Check(WorkspacePresentation.VisibleFullscreenMonitors([game with { Minimized = true }]).Length == 0
            && WorkspacePresentation.VisibleFullscreenMonitors([game with { Cloaked = true }]).Length == 0
            && WorkspacePresentation.VisibleFullscreenMonitors([game with { Visible = false }]).Length == 0,
            "Minimized, cloaked and hidden fullscreen windows do not suppress normal desktop use");
        Program.Check(WorkspacePresentation.VisibleFullscreenMonitors([browser with { Opaque = false }, game]).Length == 1
            && WorkspacePresentation.VisibleFullscreenMonitors([browser with { Bounds = new Rect(80, 80, 1000, 700) }, game]).Length == 1,
            "A transparent overlay or partially covering window does not remove fullscreen protection");
        Program.Check(WorkspacePresentation.VisibleFullscreenMonitors([game, game with { Bounds = secondary, Client = secondary,
            Monitor = secondary, WorkArea = secondaryWork }]).Length == 2
            && WorkspacePresentation.VisibleFullscreenMonitors([game with { NormalMaximized = true }]).Length == 0
            && WorkspacePresentation.VisibleFullscreenMonitors([game with { OwnProcess = true }]).Length == 0,
            "Both fullscreen monitors are protected, while normal maximized and Deskweave windows do not create suppression");
        bool Wanted(bool explicitRequest, bool dismissed = false) => WorkspacePeekPolicy.Wanted(CornerShow.Always,
            true, false, dismissed, explicitRequest, true, true, true, TimeSpan.Zero, TimeSpan.FromSeconds(5),
            presentation: true, explicitlyRequested: explicitRequest);
        Program.Check(!Wanted(false) && Wanted(true) && !Wanted(true, dismissed: true),
            "Fullscreen wins over Always, pinning, hovering and activity; an explicit owner request still respects dismissal");

        AppSettings saved = AppSettingsStore.Current;
        Func<bool>? oldQuiet = WorkspacePresentation.SuppressedForTests;
        bool hub = ModuleEntry.HubShowing;
        bool quiet = false;
        int notifications = 0;
        void Attention(string title, string text, string workspace, string request) => notifications++;
        StoredWorkspace stored = WorkspaceStore.Create("Fullscreen corner fixture");
        WorkspaceAccessStore.Write(stored.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        try
        {
            WorkspacePeekHost.Stop();
            WorkspacePresentation.SuppressedForTests = () => quiet;
            ModuleEntry.HubShowing = false;
            ModuleEntry.AttentionNeeded += Attention;
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Always, CornerPinned = true,
                CornerLeft = TestScreen.Work.Left + 30, CornerTop = TestScreen.Work.Top + 30 });
            WorkspacePeekHost.Start();
            Invoke("StirFrom", stored.Id);
            var window = (WorkspacePeekWindow)Field("_window")!;
            Program.Check(window.Watching, "Fixture: a pinned corner is visible before fullscreen begins");
            quiet = true;
            await Task.Delay(700);
            Program.Check(!window.IsVisible && !window.Watching && Field("_beat") is null,
                "Entering fullscreen removes an already visible pinned corner and stops its capture timer");
            quiet = false;
            await Task.Delay(700);
            Program.Check(window.Watching, "Leaving fullscreen restores a pinned corner through its normal visibility policy");
            Set("_dismissed", true);
            Invoke("Rethink");
            quiet = true;
            await Task.Delay(700);
            quiet = false;
            await Task.Delay(700);
            Program.Check(!window.Watching, "Leaving fullscreen does not resurrect a manually dismissed corner");

            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Off, CornerPinned = false });
            quiet = true;
            runtime.Access!.Handoffs.Request("program", "fixture app", "fullscreen notification check");
            await Task.Delay(700);
            Program.Check(notifications == 0 && !window.IsVisible,
                "An agent request during fullscreen shows neither the corner nor an automatic notification");
            ModuleEntry.RequestShowCorner();
            Program.Check(window.Watching, "An explicit tray request can show the corner during fullscreen");
            Set("_stirred", DateTimeOffset.Now - TimeSpan.FromMinutes(1));
            window.ForceHoverForTests(false);
            Invoke("Rethink");
            Program.Check(!window.IsVisible, "An explicit glance expires without leaving an automatic overlay over fullscreen");
            quiet = false;
            await Task.Delay(700);
            Program.Check(!window.Watching && notifications == 1,
                "After fullscreen ends, Off stays off and its pending attention notification is delivered once");
        }
        finally
        {
            ModuleEntry.AttentionNeeded -= Attention;
            WorkspacePeekHost.Stop();
            runtime.Dispose();
            WorkspaceStore.Delete(stored.Id);
            WorkspacePresentation.SuppressedForTests = oldQuiet;
            ModuleEntry.HubShowing = hub;
            AppSettingsStore.Update(_ => saved);
        }
    }

    static object? Field(string name) => typeof(WorkspacePeekHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    static void Set(string name, object value) => typeof(WorkspacePeekHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
    static void Invoke(string name, params object[] args) => typeof(WorkspacePeekHost).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
}
