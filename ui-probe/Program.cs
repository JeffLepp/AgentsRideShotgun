using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

namespace Deskweave.UiProbe;

static class Program
{
    static readonly List<string> Passed = [];
    static MainWindow _window = null!;
    static string _output = "";

    [STAThread]
    static void Main(string[] args)
    {
        _output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(_output);
        ProductContext.Configure("DeskweaveUiProbe");
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(_output, "workspaces"));
        using var preferences = ShellPreferences.UseFileForTests(Path.Combine(_output, "shell.json"));
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Report(new TimeoutException("UI probe exceeded its three-minute limit."));
            Environment.Exit(2);
        }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Deskweave;component/Theme.xaml")
        });
        application.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
            application.Shutdown(1);
        };
        application.Dispatcher.BeginInvoke(async () =>
        {
            Exception? failure = null;
            try { await Run(); }
            catch (Exception e) { failure = e; }
            finally
            {
                _window?.Dispose();
                ModuleEntry.Shutdown();
                _window?.Close();
                Report(failure);
                application.Shutdown(failure is null ? 0 : 1);
            }
        });
        application.Run();
    }

    static async Task Run()
    {
        _window = new MainWindow { ShowActivated = false, Left = 20, Top = 20 };
        _window.Show();
        await Settle();
        Check(WorkspaceStore.All().Count == 0 && !WorkspaceRuntime.AnyRunning,
            "Opening the empty app creates no workspace or desktop");
        Check(Find<FrameworkElement>("EmptyState").IsVisible, "First-run guidance is visible");
        Check(AutomationProperties.GetName(Find<Button>("NewButton")).Length > 0,
            "Create action has an accessible name");
        Check(_window.CurrentSkin == AppearanceSkin.Windows && AppearanceManager.Resolve("unknown") == AppearanceSkin.Windows,
            "Windows is the first-run and invalid-preference fallback appearance on Windows");
        Check(InWindow(Find<Button>("AppearanceButton")), "Appearance choice is discoverable in the main toolbar");
        Capture("01-empty.png");
        foreach (AppearanceSkin skin in Enum.GetValues<AppearanceSkin>())
        {
            _window.SetAppearance(skin);
            await Settle();
            Point close = Find<Button>("CloseButton").TransformToAncestor(_window).Transform(new Point());
            Check(skin == AppearanceSkin.Mac ? close.X < 100 : close.X > _window.ActualWidth - 100,
                skin + " keeps its single close action on the intended side");
            Capture("skin-" + skin.ToString().ToLowerInvariant() + "-empty.png");
        }
        _window.SetAppearance(AppearanceSkin.Windows);
        _window.Width = 900;
        _window.Height = 620;
        await Settle();
        Check(InWindow(Find<Button>("NewButton")), "Create remains reachable at minimum full size");
        Capture("02-narrow-empty.png");
        _window.Width = 1280;
        _window.Height = 820;
        Click("NewButton");
        await Settle();
        Check(WorkspaceStore.All().Count == 1 && WorkspaceStore.All()[0].Name == "Workspace 1",
            "One click creates a workspace with a default name");
        Check(Find<FrameworkElement>("OverviewContent").IsVisible && Find<ItemsControl>("WorkspaceTiles").Items.Count == 1,
            "Creating a workspace stays on the overview");
        var created = WorkspaceStore.All()[0];
        Check(WorkspaceRuntime.Of(created.Id) is not null,
            "Creating a workspace starts its computer, so the new tile shows a real desktop");
        var tile = (WorkspaceTile)Find<ItemsControl>("WorkspaceTiles").Items[0]!;
        Check(tile.Running && tile.StoppedVisibility == Visibility.Collapsed && MenuItems(created.Id).Contains("Stop computer"),
            "A running workspace hides the start button and offers Stop computer");
        _window.StopWorkspace(created.Id);
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is null && !tile.Running
            && tile.StoppedVisibility == Visibility.Visible && MenuItems(created.Id).Contains("Start computer"),
            "Stopping from the overview ends the desktop and offers to start it again");
        _window.StartWorkspace(created.Id);
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is not null && tile.Running,
            "Starting from the overview brings the same workspace back up");
        _window.StopWorkspace(created.Id);
        await Settle();
        Click("FocusNav");
        await Settle();
        Capture("03-focused.png");
        _window.Width = 900;
        _window.Height = 620;
        await Settle();
        Check(InWindow(Find<Button>("BackButton")), "Focus return action remains reachable at minimum size");
        var focusedPanel = (AgentWorkspacesPanel)Find<ContentControl>("FocusSlot").Content;
        var placeholder = (WorkspaceFrame)focusedPanel.FindName("ComputerFrame");
        var transform = placeholder.TransformToAncestor(_window);
        Check(Math.Abs(transform.Transform(new Point(1, 0)).X - transform.Transform(new Point()).X - 1) < 0.01,
            "Stopped-desktop frame stays unscaled at minimum width");
        Capture("03b-narrow-focus.png");
        await CheckAppearances(focusedPanel);
        _window.Width = 1280;
        _window.Height = 820;
        await Settle();
        var research = WorkspaceStore.All()[0];
        WorkspaceAccessStore.Write(research.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        var runtime = WorkspaceRuntime.Start(research);
        await Task.Delay(1500);
        var second = WorkspaceStore.Create("Build desk");
        WorkspaceStore.Create("Review desk");
        Click("OverviewNav");
        await Task.Delay(1800);
        Check(Find<ItemsControl>("WorkspaceTiles").Items.Count == 3,
            "Overview shows all three real fixture workspaces");
        var tileContainer = (FrameworkElement)Find<ItemsControl>("WorkspaceTiles").ItemContainerGenerator.ContainerFromIndex(0);
        Check(tileContainer.ActualHeight <= _window.PreviewHeight + 20,
            "Monitor tiles are only their widescreen preview, no header or caption rows");
        Capture("04-overview.png");
        foreach (AppearanceSkin skin in Enum.GetValues<AppearanceSkin>())
        {
            _window.SetAppearance(skin);
            await Settle();
            Check(ReferenceEquals(runtime, WorkspaceRuntime.Of(research.Id)) && WorkspaceStore.All().Count == 3,
                skin + " appearance keeps the running desktop and all saved workspaces");
            Capture("skin-" + skin.ToString().ToLowerInvariant() + "-overview.png");
        }
        _window.SetAppearance(AppearanceSkin.Windows);
        Find<TextBox>("SearchBox").Text = "Build";
        await Settle();
        Check(Find<ItemsControl>("WorkspaceTiles").Items.Count == 1, "Search filters workspace names");
        Find<TextBox>("SearchBox").Text = "";
        await Settle();
        Click("CompactButton");
        await Settle();
        Check(_window.Width < 500 && _window.Height > 300, "Compact mode uses a small monitor window");
        Check(ReferenceEquals(runtime, WorkspaceRuntime.Of(research.Id)),
            "Compact mode preserves the same running desktop");
        Check(InWindow(Find<Button>("CompactButton")), "Compact restore control remains reachable");
        foreach (AppearanceSkin skin in Enum.GetValues<AppearanceSkin>())
        {
            _window.SetAppearance(skin);
            await Settle();
            Check(InWindow(Find<Button>("AppearanceButton")) && InWindow(Find<Button>("CompactButton")),
                skin + " compact appearance and restore actions fit the window");
            Capture("skin-" + skin.ToString().ToLowerInvariant() + "-compact.png");
        }
        _window.SetAppearance(AppearanceSkin.Windows);
        Capture("05-compact.png");
        Click("CollapseButton");
        await Settle();
        Check(_window.Height < 140, "Collapse reduces the app to a small bar");
        Check(!_window.PreviewLoopRunning, "Collapsed bar stops overview capture");
        Check(InWindow(Find<Button>("CollapseButton")), "Collapsed expand control remains reachable");
        Capture("06-collapsed.png");
        _window.RestoreWorkspaceWindow();
        await Settle();
        Check(_window.Height > 300, "Tray restore expands the collapsed app");
        Check(ReferenceEquals(runtime, WorkspaceRuntime.Of(research.Id)),
            "Restore does not replace or restart the running desktop");
        _window.WindowState = WindowState.Minimized;
        await Task.Delay(1250);
        Check(!_window.PreviewLoopRunning, "Minimizing stops overview capture");
        Check(ReferenceEquals(runtime, WorkspaceRuntime.Of(research.Id)),
            "Minimizing retains active work");
        _window.WindowState = WindowState.Normal;
        _window.RestoreWorkspaceWindow();
        await Settle();
        if (_window.DisplayMode != "full") Click("CompactButton");
        _window.Width = 1280;
        _window.Height = 820;
        Click("OverviewNav");
        await Settle();
        Capture("07-restored.png");
        await RunPanelRegressions(research, second, runtime);
        await RunAgentRouting();
        runtime.Dispose();
        Check(WorkspaceStore.Find(research.Id) is not null && WorkspaceStore.Find(second.Id) is not null,
            "Stopping the desktop retains stored workspaces");
        _window.SetAppearance(AppearanceSkin.Mac);
        _window.Dispose();
        var saved = ShellPreferences.Read();
        Check(File.Exists(Path.Combine(_output, "shell.json")) && saved.Mode == "full" && saved.Full is not null,
            "Window mode and geometry persist to the isolated app store");
        _window.Close();
        _window = new MainWindow { ShowActivated = false };
        _window.Show();
        await Settle();
        Check(_window.DisplayMode == saved.Mode && Math.Abs(_window.ActualWidth - saved.Full!.Width) <= 2,
            "A new app window restores saved mode and width");
        Check(saved.Appearance == "Mac" && _window.CurrentSkin == AppearanceSkin.Mac,
            "An explicit appearance selection persists and overrides the host default after reopening");
        Click("HelpButton");
        await Settle();
        Check(Find<FrameworkElement>("HelpPanel").IsVisible, "Getting started and quit instructions remain available");
        Capture("08-help.png");
    }

    static async Task CheckAppearances(AgentWorkspacesPanel panel)
    {
        InvokePanel(panel, "Speak", "You", "Appearance validation fixture.");
        InvokePanel(panel, "Typing", "A streaming appearance fixture.");
        var transcript = (StackPanel)panel.FindName("Transcript");
        var message = transcript.Children.OfType<TextBlock>().Single(t => t.Text == "Appearance validation fixture.");
        var streaming = transcript.Children.OfType<TextBlock>().Single(t => t.Text == "A streaming appearance fixture.");
        var speaker = transcript.Children.OfType<TextBlock>().First(t => t.Text == "You");
        var divider = transcript.Children.OfType<Border>().Last();
        foreach (AppearanceSkin skin in Enum.GetValues<AppearanceSkin>())
        {
            _window.SetAppearance(skin);
            await Settle();
            Check(ReferenceEquals(panel, Find<ContentControl>("FocusSlot").Content),
                skin + " appearance retains the same focused workspace panel");
            Check(ColorOf(message.Foreground) == ResourceColor("ShellTextBrush")
                && ColorOf(streaming.Foreground) == ResourceColor("ShellTextBrush")
                && ColorOf(speaker.Foreground) == ResourceColor("ShellMutedBrush")
                && ColorOf(divider.Background) == ResourceColor("ShellDividerBrush"),
                skin + " appearance updates existing transcript text and divider colors");
            foreach (string background in new[] { "ShellSurfaceBrush", "ShellPanelBrush", "ShellChipBrush" })
                foreach (string foreground in new[] { "ShellTextBrush", "ShellMutedBrush" })
                    if (Contrast(ResourceColor(foreground), ResourceColor(background)) < 4.5)
                        throw new InvalidOperationException(skin + " has insufficient " + foreground + " contrast on " + background);
            Check(Contrast(ResourceColor("OnAccentBrush"), ResourceColor("ShellAccentBrush")) >= 4.5
                && Contrast(ResourceColor("OnAccentBrush"), ResourceColor("ShellAccentHoverBrush")) >= 4.5,
                skin + " body, secondary text and primary-button colors meet 4.5:1 contrast");
            var startButton = (Button)panel.FindName("StartButton");
            Check(ColorOf(startButton.Foreground) == ResourceColor("OnAccentBrush"),
                skin + " actual primary control follows the current on-accent ink");
            Capture("skin-" + skin.ToString().ToLowerInvariant() + "-focus.png");
        }
        _window.SetAppearance(AppearanceSkin.Windows);
    }

    static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    static Color ResourceColor(string key) => ColorOf((Brush)Application.Current.FindResource(key));
    static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double normalized = value / 255d; return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static async Task RunPanelRegressions(StoredWorkspace first, StoredWorkspace second, WorkspaceRuntime firstRuntime)
    {
        WorkspaceAccessStore.Write(second.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        using var secondRuntime = WorkspaceRuntime.Start(second);
        ModuleEntry.Selected = first.Id;
        using var panel = new AgentWorkspacesPanel();
        panel.SetWorkspace(first);
        Check(((DemoWorkspace)((WorkspaceFrame)panel.FindName("ComputerFrame")).DataContext).State == WorkspaceVisualState.Ready,
            "A running desktop awaiting capture is never labeled stopped");
        const string sentinel = "queued-only-for-the-first-workspace";
        var said = typeof(WorkspaceRuntime).GetField("Said", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(firstRuntime) as Action<string>;
        Check(said is not null, "Panel subscribes to its actual runtime transcript");
        said!(sentinel);
        panel.SetWorkspace(second);
        await Settle();
        var transcript = (StackPanel)panel.FindName("Transcript");
        Check(transcript.Children.OfType<TextBlock>().All(t => !t.Text.Contains(sentinel)),
            "Queued transcript from the prior workspace cannot enter the new workspace");
        panel.SetWorkspace(first);
        InvokePanel(panel, "DrawFrame", null, EventArgs.Empty);
        panel.SetWorkspace(second);
        for (int i = 0; i < 80 && (bool)typeof(AgentWorkspacesPanel).GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!; i++)
            await Task.Delay(50);
        Check(!(bool)typeof(AgentWorkspacesPanel).GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!,
            "An in-flight desktop capture completes within the test bound");
        Check(((Image)panel.FindName("LiveScreen")).Source is null && ReferenceEquals(WorkspaceRuntime.Of(second.Id), secondRuntime),
            "A stale screenshot cannot paint or stop the newly selected desktop");
        WorkspaceStore.Update(second.Id, current => current with
        {
            Mission = MissionState.Done,
            Outcome = "independently saved result",
            Announced = MissionState.Done,
            SessionReadUntrustedContent = true,
            Session = "retained-provider-session"
        });
        ((TextBox)panel.FindName("WorkspaceNameText")).Text = "Renamed build desk";
        InvokePanel(panel, "CommitName");
        var record = WorkspaceStore.Find(second.Id)!;
        Check(record.Name == "Renamed build desk" && record.Mission == MissionState.Done
            && record.Outcome == "independently saved result" && record.Announced == MissionState.Done
            && record.SessionReadUntrustedContent && record.Session == "retained-provider-session",
            "Renaming from an older panel preserves current mission, session, notification and browser state");
        Check(panel.SelectedWorkspaceId == second.Id, "Panel selection contract identifies the actual workspace");
        panel.Dispose();
        Check(ReferenceEquals(firstRuntime, WorkspaceRuntime.Of(first.Id)) && ReferenceEquals(secondRuntime, WorkspaceRuntime.Of(second.Id)),
            "Disposing a workspace view retains both independent desktops");
    }

    static async Task RunAgentRouting()
    {
        // The rules, without starting anything.
        string project = Path.Combine(_output, "projects", "Alpha");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        WorkspaceHome.Route fresh = WorkspaceHome.Decide([], project, "claude-code", _ => false);
        Check(fresh.Existing is null && fresh.Name == "Alpha" && fresh.Rule == WorkspaceHome.Folder(project),
            "An agent nothing fits gets a new workspace named for, and kept for, its project folder");
        WorkspaceHome.Route home = WorkspaceHome.Decide([], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "claude-code", _ => false);
        Check(home.Existing is null && home.Name == "Claude Code" && home.Rule == WorkspaceHome.Agent("claude-code"),
            "An agent started from its home folder gets a workspace kept for that agent, not one for home");
        static StoredWorkspace Fixture(string id, string rule) => new() { Id = id, Name = id, Agents = rule };
        StoredWorkspace[] set = [Fixture("shared", WorkspaceHome.Anyone), Fixture("codex", WorkspaceHome.Agent("codex")),
            Fixture("alpha", WorkspaceHome.Folder(project)), Fixture("outer", WorkspaceHome.Folder(_output)), Fixture("mine", "")];
        Check(WorkspaceHome.Decide(set, Path.Combine(project, "src"), "codex-mcp-client", _ => false).Existing == "alpha",
            "The deepest matching project folder beats a shallower folder, a named agent and a shared workspace");
        Check(WorkspaceHome.Decide(set, Path.GetTempPath(), "codex-mcp-client", _ => false).Existing == "codex",
            "A workspace kept for Codex takes Codex outside any assigned folder");
        Check(WorkspaceHome.Decide(set, Path.GetTempPath(), "another-agent", _ => false).Existing == "shared",
            "Any other agent goes to the shared workspace and never to one kept for the owner");
        StoredWorkspace[] pair = [Fixture("busy", WorkspaceHome.Anyone), Fixture("free", WorkspaceHome.Anyone)];
        Check(WorkspaceHome.Decide(pair, "", "x", id => id == "busy").Existing == "free",
            "Between shared workspaces an agent goes to the one nobody is using");

        // End to end: a real bridge process, the real router, a real desktop.
        StoredWorkspace shared = WorkspaceStore.Create("Shared desk");
        WorkspaceAccessStore.Write(shared.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        WorkspaceHome.Set(shared.Id, WorkspaceHome.Anyone);
        WorkspaceRouter.Start();
        Check(File.Exists(WorkspaceAccessStore.RouterTicket) && File.Exists(WorkspaceConnections.Bridge),
            "Deskweave publishes one connection for every outside agent, reached through its packaged bridge");
        var start = new ProcessStartInfo(WorkspaceConnections.Bridge)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(WorkspaceAccessStore.RouterTicket);
        using Process bridge = Process.Start(start) ?? throw new InvalidOperationException("The bridge did not start.");
        int next = 0;
        async Task<JsonElement> Call(string method, object parameters)
        {
            int id = ++next;
            await bridge.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await bridge.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                string line = await bridge.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new InvalidOperationException("The bridge closed: " + await bridge.StandardError.ReadToEndAsync());
                using var reply = JsonDocument.Parse(line);
                if (reply.RootElement.TryGetProperty("id", out JsonElement got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
                    return reply.RootElement.GetProperty("result").Clone();
            }
        }
        JsonElement hello = await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "probe-agent", version = "1" } });
        Check(hello.GetProperty("serverInfo").GetProperty("name").GetString() == "deskweave" && WorkspaceRuntime.Of(shared.Id) is null,
            "Connecting an agent starts no workspace until it uses one");
        JsonElement tools = await Call("tools/list", new { });
        Check(tools.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "browse"),
            "A connected agent sees the workspace tools before it has a workspace");
        JsonElement acquired = await Call("tools/call", new { name = "acquire", arguments = new { } });
        WorkspaceRuntime? sharedRuntime = WorkspaceRuntime.Of(shared.Id);
        Check(!acquired.TryGetProperty("isError", out _) && sharedRuntime?.Access?.Controller == "probe-agent",
            "An agent's first workspace tool lands it in the shared workspace, started for it, under its own name");
        Click("OverviewNav");
        await Settle();
        Check(Find<ItemsControl>("WorkspaceTiles").Items.OfType<WorkspaceTile>().Single(t => t.Id == shared.Id).AgentsText == "Probe-agent",
            "The hub tile names the agent working in it");
        Capture("09-agent-working.png");
        sharedRuntime!.Plane!.OwnerTakes();
        Task<JsonElement> paused = Call("tools/call", new { name = "wait", arguments = new { seconds = 1 } });
        await Task.Delay(2000);
        Check(!paused.IsCompleted, "While the owner has control an agent's next action waits instead of failing");
        sharedRuntime.Plane.Release();
        JsonElement resumed = await paused;
        Check(!resumed.TryGetProperty("isError", out _), "The agent carries on by itself once the owner lets go");
        bridge.StandardInput.Close();
        using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await bridge.WaitForExitAsync(exit.Token);
        await Settle();
        Check(sharedRuntime.Access?.HasDriver == false, "Closing an agent's session hands its workspace back");
        WorkspaceRouter.Stop();
        Check(!File.Exists(WorkspaceAccessStore.RouterTicket), "Quitting Deskweave withdraws the agent connection");
        sharedRuntime.Dispose();
    }

    static void InvokePanel(AgentWorkspacesPanel panel, string method, params object?[] args) =>
        typeof(AgentWorkspacesPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args);

    static T Find<T>(string name) where T : class =>
        _window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    static void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static string[] MenuItems(string id) =>
        _window.CreateWorkspaceMenu(id).Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
    static Task Settle() => Task.Delay(300);

    static bool InWindow(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1) return false;
        var origin = element.TransformToAncestor(_window).Transform(new Point());
        return origin.X >= 0 && origin.Y >= 0 && origin.X + element.ActualWidth <= _window.ActualWidth + 1
            && origin.Y + element.ActualHeight <= _window.ActualHeight + 1;
    }

    static void Capture(string name)
    {
        _window.UpdateLayout();
        var image = new RenderTargetBitmap((int)_window.ActualWidth, (int)_window.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        image.Render(_window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(_output, name));
        encoder.Save(output);
    }

    static void Check(bool condition, string claim)
    {
        if (!condition) throw new InvalidOperationException(claim);
        Passed.Add(claim);
    }

    static void Report(Exception? failure) => File.WriteAllText(Path.Combine(_output, "ui-report.json"),
        JsonSerializer.Serialize(new
        {
            success = failure is null,
            version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
            modelCalls = 0,
            passed = Passed,
            failure = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
}
