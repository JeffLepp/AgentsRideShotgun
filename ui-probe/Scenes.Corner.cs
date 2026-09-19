using System.IO;
using System.Reflection;
using System.Windows;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Corner window states: references 02 and 08-14. Every
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

    [Scene("corner-finished", "09-corner-finished", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Finished(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Quiet);
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
        window.ShowUsingToast("Claude");
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
        window.Describe("shop", "Claude Code", PeekTone.Working);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(true);
        window.SetPinned(true);
        window.ForceHoverForTests(true);
        window.Place(new Rect(SceneContext.OffScreen, window.VisibleSize));
        window.Arrive();
        await scene.Settle();
        return window.PhotographCard();
    }

    [Scene("corner-paused", "14-corner-paused", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Paused(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        window.Configure(new Size(344, 215), hasBack: false, grown: false, canGrow: false);
        window.Describe("shop", "Claude Code", PeekTone.Quiet);
        window.ShowFrame(scene.Site("shop"));
        window.SetActive(false);
        window.ShowPausedToast();
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
        DropChromeChecks();
        await IntegrationChecks();
        // Wave 1's fix round (design/WAVE1.md item 14): these drive the real WorkspacePeekHost and a
        // real WorkspacePeekWindow through internal seams - reflection into the host's own private
        // methods, and a real second global-hotkey registration to force a real conflict - rather
        // than only the pure WorkspacePeekPolicy.Wanted function WantedChecks above already covers.
        await HostBehaviorChecks();
        PauseReleaseChecks();
        ResultChipChecks();
        CarryOnChecks();
        await ReportShortcutsChecks();
    }

    static void WantedChecks()
    {
        TimeSpan fade = TimeSpan.FromSeconds(5);
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, false, false, TimeSpan.FromSeconds(1), fade),
            "Comes and goes stays up before five quiet seconds have passed");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, false, false, TimeSpan.FromSeconds(9), fade),
            "Comes and goes fades after five quiet seconds");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, false, true, false, TimeSpan.FromSeconds(9), fade),
            "Hovering holds it up past five quiet seconds");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.ComesAndGoes, true, false, false, false, true, false, false, TimeSpan.FromMinutes(5), fade),
            "Pinned stays up no matter how long the workspace has been quiet");
        Program.Check(
            !WorkspacePeekPolicy.Wanted(CornerShow.Off, true, false, false, false, true, false, true, TimeSpan.Zero, fade),
            "Off stays off even pinned and busy");
        Program.Check(
            WorkspacePeekPolicy.Wanted(CornerShow.Off, true, false, false, true, false, false, false, TimeSpan.Zero, fade),
            "The tray can summon it while Off");
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
        Program.Check(WorkspacePeekPlacement.Card(new AppSettings()) is { Width: 344, Height: 215 },
            "The corner starts Small at 344 by 215");
        Size min = WorkspacePeekPlacement.Card(10);
        Program.Check(min.Width == WorkspacePeekPlacement.MinWidth && Close(min.Height, min.Width * 10 / 16),
            "A width under the minimum clamps to 220 wide and keeps 16:10");
        Size max = WorkspacePeekPlacement.Card(5000);
        Program.Check(max.Width == WorkspacePeekPlacement.MaxWidth && Close(max.Height, max.Width * 10 / 16),
            "A width over the maximum clamps to 1600 wide and keeps 16:10");
        AppSettings dragged = new() { CornerWidth = 500 };
        Program.Check(Close(WorkspacePeekPlacement.Card(dragged).Width, 500),
            "A dragged width survives as the corner size");
        Program.Check(!WorkspacePeekPlacement.Grown(new AppSettings()) && WorkspacePeekPlacement.Grown(dragged),
            "Grown is only true once the card is wider than Small");
    }

    static void DropChromeChecks()
    {
        var window = new WorkspacePeekWindow();
        try
        {
            window.Describe("shop", "Claude Code", PeekTone.Working);
            window.ForceHoverForTests(true);
            window.ForceDropOverlayForTests(true);
            Program.Check(window.DropHidesChrome, "A file over the card hides its pill, actions and grip");
        }
        finally { window.Close(); }
    }

    static void CarryOnChecks()
    {
        StoredWorkspace stored = WorkspaceStore.Create("Corner carry-on fixture");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        DateTimeOffset now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        using var input = new WorkspaceScreenInput(new System.Windows.Controls.Image(), () => runtime);
        input.UseClockForTests(() => now);
        try
        {
            input.SimulateClickForTests();
            Program.Check(runtime.Plane!.Driving == Driver.Owner, "Clicking a workspace screen takes its wheel");
            input.SimulateEnterForTests();
            now += TimeSpan.FromSeconds(59);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Hovering keeps the wheel before 60 quiet seconds");
            input.SimulateInputForTests();
            now += TimeSpan.FromSeconds(59);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Input restarts the 60-second quiet countdown");
            input.SimulateLeaveForTests();
            now += TimeSpan.FromSeconds(2);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Leaving keeps the wheel before three seconds");
            input.SimulateEnterForTests();
            now += TimeSpan.FromSeconds(4);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Re-entering cancels the three-second countdown");
            input.SimulateLeaveForTests();
            now += TimeSpan.FromSeconds(3);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Nobody, "The agent carries on three seconds after the pointer leaves");
            input.SimulateEnterForTests();
            input.SimulateClickForTests();
            ModuleEntry.AllPaused = true;
            now += TimeSpan.FromSeconds(60);
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Owner, "Pause keeps an existing screen lease past its quiet deadline");
            ModuleEntry.AllPaused = false;
            input.CheckForTests();
            Program.Check(runtime.Plane.Driving == Driver.Nobody, "The agent carries on after 60 quiet seconds under the pointer");
        }
        finally { ModuleEntry.AllPaused = false; runtime.Dispose(); }
    }

    static async Task IntegrationChecks()
    {
        StoredWorkspace stored = WorkspaceStore.Create("Corner gate fixture");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        try
        {
            Rect work = TestScreen.Work;
            double left = work.Left + 40, top = work.Top + 40;
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.Always, CornerLeft = left, CornerTop = top,
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
            Rect corner = WorkspacePeekPlacement.Corner(TestScreen.Work, visible);
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

            // Alerts (MVP_SPEC): with the corner off, an agent's question becomes one Windows notification.
            var heard = new List<(string Title, string Text, string Workspace, string Request)>();
            Action<string, string, string, string> listen = (title, text, workspace, request) => heard.Add((title, text, workspace, request));
            ModuleEntry.AttentionNeeded += listen;
            try
            {
                AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Off });
                await Task.Delay(60);
                runtime.Access!.Handoffs.Request("url", "http://localhost:5173/", "the page is ready");
                await Task.Delay(200);
                Program.Check(heard.Count == 1 && heard[0].Title.EndsWith(" wants you", StringComparison.Ordinal)
                    && heard[0].Text == "Open http://localhost:5173/ on your desktop?" && heard[0].Workspace == stored.Id,
                    "With the corner off, an agent's question raises one Windows notification in the corner's own words");
                InvokeHost("Rethink");
                Program.Check(heard.Count == 1, "The same question is announced once, however often the corner rethinks");
                WorkspaceHandoff second = runtime.Access.Handoffs.Request("url", "http://localhost:5174/", "the second page");
                await Task.Delay(200);
                WorkspacePeekHost.ShowFor(stored.Id, second.Id);
                await Task.Delay(200);
                Program.Check(heard.Count == 2 && typeof(WorkspacePeekHost).GetField("_pendingId", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetValue(null) as string == second.Id,
                    "Clicking a notification brings up its own question, not an older one waiting in the same workspace");
                // The hub can't answer a desktop request, so the question shows over it.
                AppSettingsStore.Update(s => s with { CornerShow = CornerShow.ComesAndGoes });
                ModuleEntry.HubShowing = true;
                InvokeHost("Rethink");
                await Task.Delay(300);
                Program.Check(GateWindow() is { Watching: true }, "A question for the owner shows in the corner even while the hub is open");
                ModuleEntry.HubShowing = false;
                runtime.Access.Handoffs.CancelPending();
            }
            finally { ModuleEntry.AttentionNeeded -= listen; }

            // Results out (MVP_SPEC): once an agent's run ends, the chip offers the file that run made.
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Always });
            InvokeHost("Driven", stored.Id, Driver.Agent);
            await Task.Delay(100);
            string made = Path.Combine(runtime.Plane!.Folder, "result-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
            File.WriteAllText(made, "made by the run");
            string? offered = null;
            for (int i = 0; i < 15 && offered != made; i++)
            {
                InvokeHost("StirFrom", stored.Id);
                await Task.Delay(200);
                offered = typeof(WorkspacePeekHost).GetField("_resultPath", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as string;
            }
            Program.Check(offered == made,
                "When an agent's run ends, the corner offers the file that run made");

            // Full desktop's drawn taskbar, on this real workspace (its desktop is hidden: nothing
            // opens on the owner's screen).
            AppSettingsStore.Update(s => s with { AgentScreen = AgentScreenLook.Full });
            var strip = new WorkspaceTaskbar(() => runtime, () => { }, 40);
            await strip.Refresh();
            Program.Check(strip.Titles.Count >= 1 && AgentDesktop.TaskbarBand == (int)Math.Round(48.0 * AgentDesktop.ScreenWidth / 1440),
                "The taskbar lists the workspace's open windows, and a filled window leaves its band free");
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
            int started = await strip.Launch(new WorkspacePrograms.Shortcut("Notepad", notepad, ""));
            bool opened = false;
            for (int i = 0; i < 40 && !opened; i++)
            {
                await Task.Delay(250);
                await strip.Refresh();
                opened = strip.Titles.Any(t => t.Contains("Notepad", StringComparison.OrdinalIgnoreCase));
            }
            Program.Check(started > 0 && opened && runtime.Computer!.OwnsProcess(started),
                "The taskbar's launcher opens an app inside the workspace, not on the owner's desktop");
            System.Windows.Controls.Button behind = strip.WindowButtons.Last();
            string wanted = (string)behind.Tag;
            behind.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            bool fronted = false;
            for (int i = 0; i < 20 && !fronted; i++)
            {
                await Task.Delay(250);
                await strip.Refresh();
                fronted = strip.Titles.FirstOrDefault() == wanted;
            }
            Program.Check(fronted, "A window button brings that window to the front");
            // A picture of the real corner with the strip up, for a person to judge.
            if (GateWindow() is { } shown)
            {
                AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Always });
                InvokeHost("StirFrom", stored.Id);
                shown.ForceHoverForTests(true);
                await Task.Delay(1500);
                Program.Check(shown.Taskbar is { Visibility: Visibility.Visible }, "Hovering the corner shows the taskbar in Full desktop");
                if (((System.Windows.Controls.Image)shown.PhotographCard()).Source is System.Windows.Media.Imaging.BitmapSource card)
                {
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(card));
                    using var file = File.Create(Path.Combine(Program.Output, "corner-taskbar.png"));
                    png.Save(file);
                }
                shown.ForceHoverForTests(false);
            }
            Program.Check(WorkspaceTaskbar.Short("Untitled - Notepad") == "Untitled"
                && WorkspaceTaskbar.Short("Tiny shop - Google Chrome") == "Tiny shop"
                && WorkspaceTaskbar.Short(@"C:\Windows\system32\WindowsPowerShell\v1.0\powershell.exe") == "powershell",
                "Taskbar buttons read the document or page, not the app's whole title or a path");
            // The workspace page's roomier strip, photographed on its own.
            var page = new WorkspaceTaskbar(() => runtime, () => { }, 40) { Width = 900 };
            await page.Refresh();
            page.Measure(new Size(900, 40));
            page.Arrange(new Rect(0, 0, 900, 40));
            page.UpdateLayout();
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 40, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(page);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using (var file = File.Create(Path.Combine(Program.Output, "page-taskbar.png"))) encoder.Save(file);
            AppSettingsStore.Update(s => s with { AgentScreen = AgentScreenLook.Simple });
            Program.Check(AgentDesktop.TaskbarBand == 0, "Simple leaves no taskbar band");
            AppSettingsStore.Update(s => s with { AgentScreen = AgentScreenLook.Full });
        }
        finally
        {
            WorkspacePeekHost.Stop();
            runtime.Dispose();
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.ComesAndGoes,
                CornerLeft = null, CornerTop = null, CornerPinned = false,
            });
        }
    }

    static WorkspacePeekWindow? GateWindow() =>
        (WorkspacePeekWindow?)typeof(WorkspacePeekHost).GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    static bool Close(double a, double b) => Math.Abs(a - b) < 1.0;

    static object? InvokeHost(string method, params object?[] args) =>
        typeof(WorkspacePeekHost).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

    /// <summary>Sets one of the host's own private static fields directly - used only to force a
    /// clean starting point (a static, process-wide module otherwise carries real state left by
    /// every earlier gate section) before a timing-sensitive claim, never to fake a claim's result.</summary>
    static void SetHostField(string field, object? value) =>
        typeof(WorkspacePeekHost).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    static void InvokeWindow(WorkspacePeekWindow window, string method, params object?[] args) =>
        typeof(WorkspacePeekWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    /// <summary>
    /// Fade timing (items 4 and 15's hover/pinned claims, and item 4's hotkey fix) against the real
    /// host and a real window - not the pure policy function WantedChecks already covers. One real
    /// fixture workspace, waiting beyond the fixed five-second fade for each timing claim.
    /// </summary>
    static async Task HostBehaviorChecks()
    {
        StoredWorkspace stored = WorkspaceStore.Create("Corner host behavior fixture");
        WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        try
        {
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.ComesAndGoes, CornerPinned = false,
            });
            WorkspacePeekHost.Start();
            // The host is a static, process-wide module that ModuleEntry.Start() already turned on
            // for the whole gate run, so real activity from every earlier section (agent routing's
            // real bridge process included) may have left _stirred recent. Force the clean quiet
            // starting point this section's timing claims depend on, rather than assume one.
            SetHostField("_stirred", DateTimeOffset.MinValue);
            SetHostField("_dismissed", false);
            SetHostField("_summoned", false);
            InvokeHost("Rethink");
            Program.Check(GateWindow() is not { Watching: true },
                "The real window is not up while nothing has ever stirred it, nothing pinned, nothing busy");

            InvokeHost("StirFrom", stored.Id);
            WorkspacePeekWindow? window = GateWindow();
            Program.Check(window is { Watching: true }, "A real activity brings the real corner window up");

            // Every host tick used to restart the rise from 10 DIP, leaving a settled window jiggling.
            await Task.Delay(350);
            var rise = (System.Windows.Media.TranslateTransform)((FrameworkElement)window!.FindName("Root")).RenderTransform;
            for (int i = 0; i < 12; i++) { InvokeHost("Rethink"); await Task.Delay(25); }
            Program.Check(Math.Abs(rise.Y) < 0.1 && window.Opacity > 0.99,
                "Repeated preview refreshes leave the settled corner fully visible with zero entrance movement");
            window.Leave();
            await Task.Delay(65);
            window.Arrive();
            await Task.Delay(350);
            Program.Check(window.Watching && window.Opacity > 0.99 && Math.Abs(rise.Y) < 0.1,
                "Activity during a fade reverses it without hiding the window or restarting the rise");

            // Keep the owner's saved front-card position when a back card joins and leaves.
            double savedLeft = TestScreen.Work.Left + 160, savedTop = TestScreen.Work.Top + 160;
            AppSettingsStore.Update(s => s with { CornerLeft = savedLeft, CornerTop = savedTop });
            using (WorkspaceRuntime second = WorkspaceRuntime.Start(WorkspaceStore.Create("Second corner fixture")))
            {
                window.ForceHoverForTests(true);
                second.Plane!.OwnerTakes();
                InvokeHost("StirFrom", second.Id);
                Program.Check(Close(window.FrontRect.Left, savedLeft) && Close(window.FrontRect.Top, savedTop),
                    "Adding a stacked preview preserves the saved front-card position");
                Program.Check((string?)typeof(WorkspacePeekHost).GetField("_frontId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) == stored.Id,
                    "Another agent's activity cannot replace the screen under the owner's pointer");
                // Mimic the OS moving a captured window between input events. A preview tick must
                // not reapply the old saved geometry until the drag reports its final position.
                typeof(WorkspacePeekWindow).GetField("_moving", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Place(new Rect(savedLeft + 70, savedTop + 60, window.VisibleSize.Width, window.VisibleSize.Height));
                Rect moving = window.FrontRect;
                InvokeHost("Rethink");
                Program.Check(window.FrontRect == moving, "Preview refreshes do not snap a window back during a move");
                typeof(WorkspacePeekWindow).GetField("_moving", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                second.Plane.Release();
                window.ForceHoverForTests(false);
            }
            InvokeHost("StirFrom", stored.Id);
            Program.Check(Close(window.FrontRect.Left, savedLeft) && Close(window.FrontRect.Top, savedTop),
                "Removing a stacked preview preserves the saved front-card position");

            // Hover holds it up past the fixed five-second fade.
            window!.ForceHoverForTests(true);
            await Task.Delay(5800);
            Program.Check(window.Watching, "Hovering holds the real window up past five quiet seconds");
            window.ForceHoverForTests(false);
            await Task.Delay(5800);
            Program.Check(!window.Watching, "Letting go of hover lets the window fade after five quiet seconds");

            // Pinned stays up idle no matter how long the workspace has been quiet (item 15's "pinned stays" claim).
            InvokeHost("StirFrom", stored.Id);
            AppSettingsStore.Update(s => s with { CornerPinned = true });
            await Task.Delay(5800);
            Program.Check(GateWindow() is { Watching: true }, "Pinned keeps the real window up past five quiet seconds");
            AppSettingsStore.Update(s => s with { CornerPinned = false });
            await Task.Delay(5800);
            Program.Check(GateWindow() is { Watching: false }, "Un-pinning lets the real window fade once quiet again");

            // The tray request never touches CornerPinned, and its summon survives past the next
            // timer tick rather than only until it - checked in Off mode, where nothing else shows it.
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Off });
            await Task.Delay(600);
            Program.Check(GateWindow() is not { Watching: true }, "Off shows nothing on its own");
            bool pinnedBefore = AppSettingsStore.Current.CornerPinned;
            ModuleEntry.RequestShowCorner();
            window = GateWindow();
            Program.Check(window is { Watching: true }, "Show corner from the tray works while automatic showing is Off");
            Program.Check(AppSettingsStore.Current.CornerPinned == pinnedBefore,
                "Show corner from the tray never changes the pinned choice");
            await Task.Delay(900); // longer than one idle timer tick, shorter than the fixed fade
            Program.Check(window!.Watching,
                "The tray summon survives past the next timer tick");
            await Task.Delay(5800);
            Program.Check(!window.Watching, "The tray summon fades after five quiet seconds");

            // Hidden while ModuleEntry.HubShowing, on the real host (item 15's "HubShowing hides it" claim).
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Always });
            await Task.Delay(300);
            Program.Check(GateWindow() is { Watching: true }, "Always keeps the real window up with a workspace running");
            ModuleEntry.HubShowing = true;
            await Task.Delay(300);
            Program.Check(GateWindow() is { Watching: false }, "Showing the hub hides the real corner window even set to Always");
            ModuleEntry.HubShowing = false;
            await Task.Delay(300);
            Program.Check(GateWindow() is { Watching: true }, "Hiding the hub again brings the real corner window back");

            // Hide/dismiss (item 15): gone until the next activity, through the real Hide button's own
            // click handler rather than the host's internal dismissed flag set directly.
            window = GateWindow();
            InvokeWindow(window!, "HideButton_Click", null, new RoutedEventArgs());
            await Task.Delay(300);
            Program.Check(!window!.Watching, "Clicking the real Hide button dismisses the real window");
            InvokeHost("StirFrom", stored.Id);
            await Task.Delay(300);
            Program.Check(GateWindow() is { Watching: true }, "The next real activity brings a dismissed real window back");
        }
        finally
        {
            WorkspacePeekHost.Stop();
            runtime.Dispose();
            ModuleEntry.HubShowing = false;
            AppSettingsStore.Update(s => s with
            {
                CornerShow = CornerShow.ComesAndGoes,
                CornerLeft = null, CornerTop = null, CornerPinned = false,
            });
        }
    }

    /// <summary>
    /// Item 5's fix: a second Pause press hands back only the workspaces Pause itself took, never one
    /// the owner already held before he pressed it. Driven through the real host's own hotkey handler
    /// against two real workspaces - one already owner-driven, one free.
    /// </summary>
    static void PauseReleaseChecks()
    {
        StoredWorkspace ownerHeldStore = WorkspaceStore.Create("Corner pause fixture - owner held");
        StoredWorkspace freeStore = WorkspaceStore.Create("Corner pause fixture - free");
        WorkspaceRuntime ownerHeld = WorkspaceRuntime.Start(ownerHeldStore);
        WorkspaceRuntime free = WorkspaceRuntime.Start(freeStore);
        try
        {
            ownerHeld.Plane!.OwnerTakes();
            Program.Check(ownerHeld.Plane!.Driving == Driver.Owner && free.Plane!.Driving == Driver.Nobody,
                "Fixture: one workspace is already owner-driven before Pause, the other is free");
            WorkspacePeekHost.Start();
            ModuleEntry.RequestPauseAll();
            Program.Check(ownerHeld.Plane!.Driving == Driver.Owner,
                "The first Pause press leaves a workspace the owner already held untouched");
            Program.Check(free.Plane!.Driving == Driver.Owner,
                "The first Pause press takes a workspace the owner did not already hold");
            Program.Check(ModuleEntry.AllPaused, "The tray state follows Pause every agent");
            ModuleEntry.RequestPauseAll();
            Program.Check(free.Plane!.Driving == Driver.Nobody,
                "The second Pause press hands back the workspace Pause itself took");
            Program.Check(ownerHeld.Plane!.Driving == Driver.Owner,
                "The second Pause press never releases the workspace the owner held before Pause - only what Pause itself took (the fixed regression)");
            Program.Check(!ModuleEntry.AllPaused, "The tray state follows Resume every agent");

            ownerHeld.Plane.Release();
            ModuleEntry.RequestPauseAll();
            SetHostField("_stirred", DateTimeOffset.MinValue);
            InvokeHost("Rethink");
            WorkspacePeekWindow? paused = GateWindow();
            Program.Check(paused is { Watching: true, PausedToastVisible: true },
                "A paused front card stays up with its Resume link after the fade period");
            InvokeWindow(paused!, "ToastLink_Click", null, new RoutedEventArgs());
            Program.Check(!ModuleEntry.AllPaused && free.Plane.Driving == Driver.Nobody,
                "The toast Resume button resumes every agent through the shared toggle");

            using var input = new WorkspaceScreenInput(new System.Windows.Controls.Image(), () => ownerHeld);
            input.SimulateClickForTests();
            ModuleEntry.RequestPauseAll();
            input.Release();
            Program.Check(ownerHeld.Plane.Driving == Driver.Owner,
                "Releasing a screen during Pause immediately transfers its lease to Pause");
            ModuleEntry.RequestPauseAll();
            Program.Check(ownerHeld.Plane.Driving == Driver.Nobody,
                "Resume releases a lease that Pause retook from a released screen");
            input.SimulateClickForTests();
            ModuleEntry.RequestPauseAll();
            input.Dispose();
            Program.Check(ownerHeld.Plane.Driving == Driver.Owner,
                "Closing an owner-held screen keeps its agent paused");
            ModuleEntry.RequestPauseAll();
            Program.Check(ownerHeld.Plane.Driving == Driver.Nobody,
                "Resume releases the pause lease left by a closed screen");
        }
        finally
        {
            WorkspacePeekHost.Stop();
            ownerHeld.Dispose();
            free.Dispose();
        }
    }

    /// <summary>
    /// Item 7's fix: the result chip's own file-picking method, on the real host via reflection
    /// (there is no way to reach MissionState.Done here without a real agent process, which the gate
    /// may not run) - a file made before the run offers no chip, one made during it does.
    /// </summary>
    static void ResultChipChecks()
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "corner-chip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            DateTimeOffset since = DateTimeOffset.Now;
            string stale = Path.Combine(folder, "already-there.txt");
            File.WriteAllText(stale, "old");
            File.SetLastWriteTimeUtc(stale, since.UtcDateTime.AddMinutes(-5));
            File.SetCreationTimeUtc(stale, since.UtcDateTime.AddMinutes(-5));

            var beforeRun = ((string? Name, string? Path))InvokeHost("NewestFile", folder, since)!;
            Program.Check(beforeRun.Name is null,
                "The real result-chip method offers nothing when only a file that predates the run exists");

            string fresh = Path.Combine(folder, "made-this-run.txt");
            File.WriteAllText(fresh, "new");
            var afterRun = ((string? Name, string? Path))InvokeHost("NewestFile", folder, since)!;
            Program.Check(afterRun.Name == "made-this-run.txt" && afterRun.Path == fresh,
                "The real result-chip method offers the file created during the run rather than the older one still in the folder");

            string? opened = null;
            WorkspacePeekHost.OpenResultForTests = path => opened = path;
            WorkspacePeekWindow chip = (WorkspacePeekWindow)InvokeHost("Build")!;
            try
            {
                chip.ShowResultChip("made-this-run.txt", fresh);
                chip.ClickChipForTests();
                Program.Check(opened == fresh, "Clicking the result chip reaches the file-open seam");
            }
            finally { chip.Close(); WorkspacePeekHost.OpenResultForTests = null; }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// A competing real registration makes the stored Pause shortcut unavailable. The host saves
    /// the first available fallback for Settings and reports no refusal while one is held.
    /// </summary>
    static async Task ReportShortcutsChecks()
    {
        WorkspacePeekHotkey? blocker = null;
        try
        {
            blocker = new WorkspacePeekHotkey();
            Program.Check(blocker.Hold("Ctrl+Alt+Shift+F11"),
                "The fixture registers a real competing Pause shortcut");
            AppSettingsStore.Update(s => s with { PauseHotkey = "Ctrl+Alt+Shift+F11" });
            WorkspacePeekHost.Start();
            await Task.Delay(300);
            Program.Check(AppSettingsStore.Current.PauseHotkey == "Ctrl+Alt+Shift+P"
                && !ModuleEntry.PauseShortcutTaken,
                "Pause takes and saves the first free fallback when Windows refuses the stored key");
        }
        finally
        {
            blocker?.Dispose();
            WorkspacePeekHost.Stop();
            AppSettingsStore.Update(s => s with { PauseHotkey = "Ctrl+Alt+P" });
        }
    }
}
