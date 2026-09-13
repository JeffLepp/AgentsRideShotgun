using System.IO;
using System.Reflection;
using System.Windows;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Corner window states: references 02 and 08-13. Every
/// scene fixtures a <see cref="WorkspacePeekWindow"/> directly through its internal methods, off
/// screen - no running desktop, no real workspace.</summary>
static class CornerScenes
{
    [Scene("corner-working", "08-corner-working", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Working(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(true);
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-done", "09-corner-done", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Done(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Done", PeekTone.Quiet);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(false);
        window.ShowResultChip("checkout.png", "checkout.png");
        window.ForceHoverForTests(true);
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-two", "10-corner-two", 60, 28, 344, 230)]
    static async Task<FrameworkElement> Two(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: true, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(true);
        window.DescribeBack("blog", "Codex", PeekTone.Working);
        window.ShowBackFrame(scene.Site("blog"));
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-drop", "11-corner-drop", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Drop(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(false);
        window.ForceDropOverlayForTests(true);
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-needs-you", "12-corner-needs-you", 60, 43, 344, 215)]
    static async Task<FrameworkElement> NeedsYou(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("blog", "Codex wants you", PeekTone.Attention);
        window.ShowFrame(scene.Site("blog"));
        window.SetActive(false);
        window.ShowNeedsYou("Open report.pdf on your desktop?");
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-you-use-it", "13-corner-you-use-it", 60, 43, 344, 215)]
    static async Task<FrameworkElement> YouUseIt(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(true);
        window.ShowToast("You're using it · Claude waits");
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-grown", "02-corner-grown", 656, 361, 760, 475)]
    static async Task<FrameworkElement> Grown(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(760, 475), hasBack: false, grown: true, canGrow: false);
        window.Describe("shop", "Claude Code · working alongside you", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(true);
        window.SetPinned(true);
        window.ForceHoverForTests(true);
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    /// <summary>Behavior checks for the corner window, run at the end of the UI gate. Pure math
    /// (<see cref="WorkspacePeekPolicy"/>, <see cref="WorkspacePeekPlacement"/>) is checked directly;
    /// position-survives-a-restart and the drop are checked against one real fixture workspace, its
    /// folder under the gate's own redirected <see cref="WorkspaceStore"/> root.</summary>
    internal static async Task Gate()
    {
        WantedChecks();
        SizeChecks();
        await IntegrationChecks();
    }

    static void WantedChecks()
    {
        TimeSpan fade = TimeSpan.FromSeconds(5);
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, false, false, TimeSpan.FromSeconds(1), fade),
            "Comes and goes stays up before FadeAfterSeconds has passed");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, false, false, TimeSpan.FromSeconds(9), fade),
            "Comes and goes fades once quiet time passes FadeAfterSeconds");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, true, false, TimeSpan.FromSeconds(9), fade),
            "Hovering holds it up past FadeAfterSeconds");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, true, false, false, TimeSpan.FromMinutes(5), fade),
            "Pinned stays up no matter how long the workspace has been quiet");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.Off, true, false, false, false, true, false, true, TimeSpan.Zero, fade),
            "Off stays off even pinned and busy");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.Off, true, false, false, true, false, false, false, TimeSpan.Zero, fade),
            "The hotkey still summons it while Off");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.Always, true, true, false, false, false, false, false, TimeSpan.Zero, fade),
            "Hidden while ModuleEntry.HubShowing, even set to Always");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.Always, false, false, false, false, false, false, false, TimeSpan.Zero, fade),
            "Nothing running leaves nothing to show, even set to Always");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.Always, true, false, true, false, false, false, false, TimeSpan.Zero, fade),
            "Hide dismisses it until the next activity");
    }

    static void SizeChecks()
    {
        Program.Check(WorkspacePeekPlacement.Card(CornerSize.Small) is { Width: 344, Height: 215 }, "Small is 344 by 215");
        Program.Check(WorkspacePeekPlacement.Card(CornerSize.Medium) is { Width: 480, Height: 300 }, "Medium is 480 by 300");
        Program.Check(WorkspacePeekPlacement.Card(CornerSize.Large) is { Width: 640, Height: 400 }, "Large is 640 by 400");
        Size min = WorkspacePeekPlacement.Card(10);
        Program.Check(min.Width == WorkspacePeekPlacement.MinWidth && Close(min.Height, min.Width * 10 / 16),
            "A width under the minimum clamps to 220 wide and keeps 16:10");
        Size max = WorkspacePeekPlacement.Card(5000);
        Program.Check(max.Width == WorkspacePeekPlacement.MaxWidth && Close(max.Height, max.Width * 10 / 16),
            "A width over the maximum clamps to 1600 wide and keeps 16:10");
        AppSettings dragged = new() { CornerWidth = 500 };
        Program.Check(Close(WorkspacePeekPlacement.Card(dragged).Width, 500),
            "A dragged width overrides the Size setting until Size changes again");
        Program.Check(!WorkspacePeekPlacement.Grown(new AppSettings()) && WorkspacePeekPlacement.Grown(dragged),
            "Grown is only true once the card is wider than Small");
    }

    static async Task IntegrationChecks()
    {
        StoredWorkspace stored = WorkspaceStore.Create("Corner gate fixture");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        try
        {
            Rect work = SystemParameters.WorkArea;
            double left = work.Left + 40, top = work.Top + 40;
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.Always, CornerPosition = CornerPosition.WhereILeaveIt, CornerLeft = left, CornerTop = top,
            });
            WorkspacePeekHost.Start();
            await Task.Delay(60);
            WorkspacePeekWindow? window = GateWindow();
            Program.Check(window is not null
                && Close(window.Left + WorkspacePeekWindow.ShadowMargin, left) && Close(window.Top + WorkspacePeekWindow.ShadowMargin, top),
                "A saved place opens the corner window there");

            WorkspacePeekHost.Stop();
            WorkspacePeekHost.Start();
            await Task.Delay(60);
            window = GateWindow();
            Program.Check(window is not null
                && Close(window.Left + WorkspacePeekWindow.ShadowMargin, left) && Close(window.Top + WorkspacePeekWindow.ShadowMargin, top),
                "The saved place survives a restart");
            Size visible = window!.VisibleSize;

            WorkspacePeekHost.Stop();
            AppSettingsStore.Update(s => s with { CornerLeft = 500_000, CornerTop = 500_000 });
            WorkspacePeekHost.Start();
            await Task.Delay(60);
            window = GateWindow();
            Rect corner = WorkspacePeekPlacement.Corner(SystemParameters.WorkArea, CornerPosition.WhereILeaveIt, visible);
            Program.Check(window is not null
                && Close(window.Left + WorkspacePeekWindow.ShadowMargin, corner.Left) && Close(window.Top + WorkspacePeekWindow.ShadowMargin, corner.Top),
                "A saved place off every connected monitor falls back to the ordinary corner");

            StoredWorkspace sourceHolder = WorkspaceStore.Create("Corner gate drop source");
            string sourceDir = WorkspaceStore.FolderOf(sourceHolder.Id);
            Directory.CreateDirectory(sourceDir);
            string sourcePath = Path.Combine(sourceDir, "notes.txt");
            File.WriteAllText(sourcePath, "drag me");
            string folder = runtime.Plane!.Folder;
            WorkspacePeekHost.DropFiles([sourcePath]);
            string destination = Path.Combine(folder, "notes.txt");
            Program.Check(File.Exists(destination) && File.ReadAllText(destination) == "drag me" && File.Exists(sourcePath),
                "Dropping a file copies it into the workspace folder and leaves the source in place");
            WorkspacePeekHost.DropFiles([sourcePath]);
            Program.Check(File.Exists(Path.Combine(folder, "notes (2).txt")),
                "A clash on drop is renamed \" (2)\" rather than overwriting the first copy");
        }
        finally
        {
            WorkspacePeekHost.Stop();
            runtime.Dispose();
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.ComesAndGoes, CornerPosition = CornerPosition.BottomRight,
                CornerLeft = null, CornerTop = null, CornerPinned = false,
            });
        }
    }

    static WorkspacePeekWindow? GateWindow() =>
        (WorkspacePeekWindow?)typeof(WorkspacePeekHost).GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    static bool Close(double a, double b) => Math.Abs(a - b) < 1.0;
}
