using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Deskweave;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

static partial class CornerScenes
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint window, int index);

    static WorkspacePeekTab? EdgeTab(WorkspacePeekWindow window) =>
        (WorkspacePeekTab?)typeof(WorkspacePeekWindow).GetField("_edgeTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    internal static async Task DockingChecks()
    {
        await DockTransitionChecks();
        await HostBehaviorChecks();
        await DockPolicyChecks();
        await PresentationScenes.Gate();
    }

    static async Task DockTransitionChecks()
    {
        foreach (Rect work in new[] { new Rect(0, 0, 1920, 1040), new Rect(-1600, -200, 1600, 860) })
        {
            Rect left = WorkspacePeekPlacement.EdgeTab(work, new Rect(work.Left + 16, work.Bottom - 240, 344, 215));
            Rect right = WorkspacePeekPlacement.EdgeTab(work, new Rect(work.Right - 360, work.Bottom - 240, 344, 215));
            Program.Check(left.Left == work.Left && right.Right == work.Right && work.Contains(left) && work.Contains(right),
                "The tab uses the nearer side and stays above the taskbar, including negative monitor coordinates: " + work);
        }
        var window = new WorkspacePeekWindow { DockAnimationsForTests = true };
        window.EdgeRevealRequested += window.Arrive;
        Rect area = TestScreen.Work;
        try
        {
            foreach (ThemeChoice theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            foreach (bool right in new[] { true, false })
            {
                AppearanceManager.Apply(theme);
                window.Configure(new Size(344, 215), false, false);
                window.Place(new Rect(right ? area.Right - 360 : area.Left + 16, area.Bottom - 250, 344, 215));
                window.Describe("Corner docking fixture", "Agent", PeekTone.Quiet);
                window.Arrive();
                await Task.Delay(300);
                nint style = GetWindowLongPtr(new WindowInteropHelper(window).Handle, -16);
                Program.Check((style & 0x00040000) != 0, "The corner window has a native sizing border for its Windows resize command");
                Rect before = window.FrontRect;
                window.Dock("Corner docking fixture");
                await Task.Delay(55);
                var viewport = (FrameworkElement)window.FindName("DockViewport");
                var motion = (TranslateTransform)((FrameworkElement)window.FindName("Root")).RenderTransform;
                Program.Check(window.Sliding && !window.Watching && viewport.Clip is not null && (right ? motion.X > 0 : motion.X < 0),
                    theme + " corner slides toward the " + (right ? "right" : "left") + " edge within a clipped viewport");
                await Task.Delay(300);
                WorkspacePeekTab tab = EdgeTab(window)!;
                Program.Check(window.Docked && !window.IsVisible && tab.IsVisible && window.FrontRect == before,
                    "Docking leaves a visible tab and retains the exact card position");
                Mvp.Save(Mvp.Photograph(tab, 2), Path.Combine(Program.Output, $"edge-{theme}-{(right ? "right" : "left")}.png"));
                var button = (Button)tab.FindName("RevealButton");
                var peer = new ButtonAutomationPeer(button);
                Program.Check(peer.GetName().Contains("Corner docking fixture"), "The edge tab names its workspace to assistive technology");
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(300);
                Program.Check(window.Watching && !window.Docked && !tab.IsVisible && window.FrontRect == before && !window.ShowActivated,
                    "Invoking the edge tab returns the same card without activating it or changing its position");
            }

            Rect stable = window.FrontRect;
            window.Dock("Corner docking fixture");
            await Task.Delay(60);
            window.Arrive();
            await Task.Delay(300);
            Program.Check(window.Watching && !window.Sliding && !EdgeTab(window)!.IsVisible && window.FrontRect == stable,
                "Activity partway through a tuck reverses the slide without a late hide or position drift");
            window.Dock("Corner docking fixture");
            window.HideImmediately();
            await Task.Delay(300);
            Program.Check(!window.IsVisible && !EdgeTab(window)!.IsVisible && !window.Sliding,
                "Fullscreen suppression cancels an in-flight tuck without a delayed tab appearing");
            window.DockAnimationsForTests = false;
            window.Arrive();
            window.Dock("Corner docking fixture");
            Program.Check(!window.Sliding && !window.IsVisible && EdgeTab(window)!.IsVisible,
                "Reduced motion switches directly to the tab with no slide");
            window.Arrive();
            Program.Check(!window.Sliding && window.Watching && window.FrontRect == stable,
                "Reduced motion restores the card immediately at the saved position");
            window.Dock("Hover fixture");
            var hoverButton = (Button)EdgeTab(window)!.FindName("RevealButton");
            void Hover(bool enter) => hoverButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = enter ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
            // Route the same enter/leave events as WPF, keeping the owner's real cursor untouched.
            Hover(true);
            await Task.Delay(60);
            Program.Check(window.Docked, "Briefly passing the tab does not immediately open it");
            Hover(false);
            await Task.Delay(220);
            Program.Check(window.Docked, "Leaving the tab cancels the pending hover reveal");
            Hover(true);
            await Task.Delay(300);
            Program.Check(window.Watching && !EdgeTab(window)!.IsVisible,
                "A sustained WPF mouse-entry event reveals the workspace after the hover delay");
        }
        finally { window.Close(); }

        AppearanceManager.Apply(ThemeChoice.Light);
    }

    static async Task DockPolicyChecks()
    {
        AppSettings saved = AppSettingsStore.Current;
        StoredWorkspace stored = WorkspaceStore.Create("Dock policy fixture");
        using WorkspaceRuntime runtime = WorkspaceRuntime.Start(stored);
        try
        {
            ModuleEntry.HubShowing = false;
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.ComesAndGoes, CornerPinned = false });
            WorkspacePeekHost.Start();
            InvokeHost("StirFrom", stored.Id);
            await Task.Delay(300);
            WorkspacePeekWindow window = GateWindow()!;
            window.ForceHoverForTests(false);
            SetHostField("_stirred", DateTimeOffset.MinValue);
            InvokeHost("Rethink");
            await Task.Delay(300);
            Program.Check(window.Docked && EdgeTab(window) is { IsVisible: true }
                && typeof(WorkspacePeekHost).GetField("_beat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
                "The tucked host stops preview captures while leaving the tab reachable");
            var button = (Button)EdgeTab(window)!.FindName("RevealButton");
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(300);
            Program.Check(window.Watching && !window.Docked && runtime == WorkspaceRuntime.Of(stored.Id)
                && runtime.Plane!.Driving == Driver.Nobody,
                "Revealing through the host reuses the running workspace and does not take control from its agent");
            window.ForceHoverForTests(false);
            SetHostField("_stirred", DateTimeOffset.MinValue);
            InvokeHost("Rethink");
            await Task.Delay(300);
            ModuleEntry.HubShowing = true;
            await Task.Delay(300);
            Program.Check(!window.IsVisible && !EdgeTab(window)!.IsVisible, "Opening the hub hides both card and tab");
            ModuleEntry.HubShowing = false;
            await Task.Delay(300);
            Program.Check(EdgeTab(window)!.IsVisible, "Closing the hub restores the quiet workspace's tab");
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.Off });
            Program.Check(!window.IsVisible && !EdgeTab(window)!.IsVisible, "Off removes the edge tab as well as the card");
            AppSettingsStore.Update(s => s with { CornerShow = CornerShow.ComesAndGoes });
            await Task.Delay(300);
            runtime.Dispose();
            await Task.Delay(300);
            Program.Check(!window.IsVisible && !EdgeTab(window)!.IsVisible, "Stopping the last workspace removes its tab");
        }
        finally
        {
            WorkspacePeekHost.Stop();
            ModuleEntry.HubShowing = false;
            AppSettingsStore.Update(_ => saved);
        }
    }

}
