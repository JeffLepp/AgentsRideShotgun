using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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

    static readonly ThemeChoice[] Themes = [ThemeChoice.Light, ThemeChoice.Dark];

    [STAThread]
    static void Main(string[] args)
    {
        // A folder: the UI gate. --mvp <folder> [scene name prefix]: the reference-screen harness (Mvp.cs).
        bool mvp = args.Length is 2 or 3 && args[0] == "--mvp";
        _output = Path.GetFullPath(mvp ? args[1] : args.Single());
        Directory.CreateDirectory(_output);
        // The scenes stand in for every agent seam; if one is ever missed, what it writes lands here and
        // not in the owner's own configuration, which is what the gate-1 run did.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(_output, "agents", "claude"));
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_output, "agents", "codex"));
        // ponytail: one probe at a time on this PC, so parallel worktrees don't fight over the screen,
        // focus and CPU timings. Never released by hand; closing it at exit hands the turn on.
        using var turn = new Mutex(false, @"Local\Deskweave.Probe.Turn");
        try { turn.WaitOne(); } catch (AbandonedMutexException) { }
        ProductContext.Configure("DeskweaveUiProbe");
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(_output, "workspaces"));
        using var preferences = ShellPreferences.UseFileForTests(Path.Combine(_output, "shell.json"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(_output, "settings.json"));
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
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Deskweave;component/Controls.xaml")
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
            try { await (mvp ? Mvp.Run(_output, args.Length == 3 ? args[2] : null) : Run()); }
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
        Check(WorkspaceStore.All() is [{ Name: "Scratch" }] && !WorkspaceRuntime.AnyRunning,
            "Opening the empty app creates one Scratch workspace and starts no desktop");
        Check(_window.DisplayMode == "stack" && _window.Hub.Asleep.Any(e => e.Name == "Scratch"),
            "The hub opens on the stack, with Scratch showing under Recent");
        Check(_window.FindName("EmptyState") is null && _window.FindName("WorkspaceTiles") is null
            && _window.FindName("CompactButton") is null && _window.FindName("CollapseButton") is null
            && _window.FindName("HelpButton") is null && _window.FindName("SearchBox") is null
            && _window.FindName("AppearanceButton") is null && _window.FindName("FocusSlot") is null,
            "The icon rail, overview grid, compact/collapsed modes, search box, help panel and boss panel are gone");
        Check(_window.FindName("NewButton") is null, "The title bar has no new-workspace button (brief A.1)");
        Check(AppearanceManager.Choice == ThemeChoice.FollowWindows && AppearanceManager.Dark == AppearanceManager.WindowsIsDark(),
            "The theme follows Windows until Settings forces one");
        CheckSettings();
        Capture("01-stack.png");
        foreach (ThemeChoice theme in Themes)
        {
            AppearanceManager.Apply(theme);
            await Settle();
            Point close = Find<Button>("CloseButton").TransformToAncestor(_window).Transform(new Point());
            Check(close.X > _window.ActualWidth - 100, theme + " theme keeps the close action at the right");
            Capture("theme-" + Lower(theme) + "-stack.png");
        }
        AppearanceManager.Apply(ThemeChoice.Light);
        await Settle();

        _window.Width = 320;
        _window.Height = 480;
        await Settle();
        Capture("02-narrow-stack.png");
        _window.Width = 340;
        _window.Height = 804;
        await Settle();

        // A workspace the engine's own router creates (brief A.7: no + button; workspaces make
        // themselves), started with the store's speed and restrictions (brief A.11).
        var created = WorkspaceStore.Create("Workspace 1");
        created = WorkspaceStore.Update(created.Id, w => w with { Power = WorkspacePower.Fast, Mode = WorkspaceMode.Free }) ?? created;
        WorkspaceRuntime.Start(created);
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is not null, "A new workspace starts its own computer");
        Check(_window.Hub.Working.Any(e => e.Id == created.Id), "A running workspace shows under Working on the stack");
        Check(WorkspaceStore.Find(created.Id)!.Power == WorkspacePower.Fast
            && WorkspaceStore.Find(created.Id)!.Mode == WorkspaceMode.Free,
            "An automatically created workspace starts Fast and Free");
        WorkspaceRuntime.Of(created.Id)?.Dispose();
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is null && _window.Hub.Asleep.Any(e => e.Id == created.Id),
            "Stopping a workspace's computer moves it from Working to Recent");
        WorkspaceRuntime.Start(WorkspaceStore.Find(created.Id)!);
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is not null && _window.Hub.Working.Any(e => e.Id == created.Id),
            "Starting it again brings it back under Working");

        // Clicking a card or row widens the window onto that workspace (brief A.5).
        InvokePrivate(_window, "WorkingCard_Click", new Button { Tag = created.Id }, new RoutedEventArgs());
        await Settle();
        Check(_window.DisplayMode == "wide" && _window.SelectedWorkspaceId == created.Id,
            "Clicking a working card widens the window onto that workspace");
        Check(_window.Hub.Working.Any(e => e.Id == created.Id && e.Selected), "The sidebar marks the open workspace selected");
        Capture("03-wide.png");
        _window.ShowStack();
        await Settle();

        string scratchId = _window.Hub.Asleep.First(e => e.Name == "Scratch").Id;
        InvokePrivate(_window, "AsleepRow_Click", new Button { Tag = scratchId }, new RoutedEventArgs());
        await Settle();
        Check(_window.DisplayMode == "wide" && _window.SelectedWorkspaceId == scratchId,
            "Clicking a recent row also opens that workspace in the wide window");
        _window.ShowStack();
        await Settle();

        // The filter field (Ctrl+F opens it; Escape clears it): the stack's only search surface (brief A.3).
        InvokePrivate(_window, "BeginFilter");
        await Settle();
        Check(Find<Border>("FilterHost").Visibility == Visibility.Visible, "The filter field can be opened");
        Find<TextBox>("FilterBox").Text = "Workspace";
        await Settle();
        Check(Find<ItemsControl>("StackWorkingList").Items.Count == 1 && Find<ItemsControl>("StackAsleepList").Items.Count == 0,
            "Typing in the filter narrows the stack to matching names");
        RaiseKey(_window, Key.Escape);
        await Settle();
        Check(Find<Border>("FilterHost").Visibility == Visibility.Collapsed && Find<ItemsControl>("StackAsleepList").Items.Count == 1,
            "Escape clears the filter and shows every workspace again");

        // The gear opens Settings (brief A.7); Escape from a wide workspace returns to the stack (brief A.5).
        Click("SettingsButton");
        await Settle();
        Check(_window.DisplayMode == "settings", "The gear button opens Settings in the wide window");
        _window.ShowStack();
        await Settle();

        // Previews only run while the hub is on screen and not minimized (brief A.10); the corner
        // window watches the same flag to know when to stay away.
        Check(ModuleEntry.HubShowing, "The corner window stays away while the hub is on screen");
        _window.WindowState = WindowState.Minimized;
        await Settle();
        Check(!ModuleEntry.HubShowing, "Minimizing lets the corner window return");
        _window.WindowState = WindowState.Normal;
        await Settle();
        Check(ModuleEntry.HubShowing, "Restoring brings the hub back in front of the corner window");

        // Reopening from the tray or the taskbar always lands on the stack, even from wide (brief A.5).
        _window.ShowWide(created.Id);
        await Settle();
        _window.Hide();
        await Settle();
        _window.RestoreWorkspaceWindow();
        await Settle();
        Check(_window.DisplayMode == "stack" && _window.IsVisible, "Reopening from the tray returns to the stack");

        // The engine asking for a specific workspace opens it in the wide window (brief A.8).
        ModuleEntry.Selected = created.Id;
        bool raised = false;
        void OnOpen() => raised = true;
        ModuleEntry.DashboardOpenRequested += OnOpen;
        ModuleEntry.RequestDashboardOpen();
        await Settle();
        ModuleEntry.DashboardOpenRequested -= OnOpen;
        Check(raised && _window.DisplayMode == "wide" && _window.SelectedWorkspaceId == created.Id,
            "The corner window's open-in-hub opens that workspace in the wide window");
        _window.ShowStack();
        await Settle();

        WorkspaceRuntime.Of(created.Id)?.Dispose();
        await Settle();
        WorkspaceAccessStore.Write(created.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        var runtime = WorkspaceRuntime.Start(WorkspaceStore.Find(created.Id)!);
        await Task.Delay(1500);
        var second = WorkspaceStore.Create("Build desk");
        WorkspaceStore.Create("Review desk");
        await Settle();
        await RunPanelRegressions(WorkspaceStore.Find(created.Id)!, second, runtime);
        await RunAgentRouting();
        runtime.Dispose();
        Check(WorkspaceStore.Find(created.Id) is not null && WorkspaceStore.Find(second.Id) is not null,
            "Stopping the desktop retains stored workspaces");

        // Window mode, selection and geometry persist; an explicit theme wins over Windows.
        _window.ShowWide(created.Id);
        _window.Width = 1100;
        _window.Height = 700;
        await Settle();
        AppSettingsStore.Update(s => s with { Theme = ThemeChoice.Dark });
        await Settle();
        _window.Dispose();
        var saved = ShellPreferences.Read();
        Check(File.Exists(Path.Combine(_output, "shell.json")) && saved.Mode == "wide"
            && saved.SelectedWorkspace == created.Id && saved.Wide is not null,
            "Window mode, selection and geometry persist to the isolated app store");
        _window.Close();
        _window = new MainWindow { ShowActivated = false };
        _window.Show();
        await Settle();
        Check(_window.DisplayMode == saved.Mode && _window.SelectedWorkspaceId == created.Id
            && Math.Abs(_window.ActualWidth - saved.Wide!.Width) <= 2,
            "A new app window restores the saved mode, selection and width");
        Check(File.ReadAllText(Path.Combine(_output, "settings.json")).Contains("\"Theme\": \"Dark\"") && AppearanceManager.Dark,
            "An explicit theme persists and wins over Windows after reopening");

        // Each Wave 1 slice adds its behavior checks in its own Scenes.*.cs file. A named slice is
        // useful while repairing one checker; ordinary validation leaves it unset and runs all.
        string ownerAgents = OwnerAgentEntries();
        string? slice = Environment.GetEnvironmentVariable("DESKWEAVE_UI_GATE_SLICE")?.Trim().ToLowerInvariant();
        if (slice is not null and not ("hub" or "corner" or "settings" or "firstrun"))
            throw new ArgumentException("DESKWEAVE_UI_GATE_SLICE must be hub, corner, settings or firstrun.");
        if (slice is null or "hub") await HubScenes.Gate();
        if (slice is null or "corner")
        {
            // Corner behavior is intentionally suppressed while any hub is visible. Leave the main
            // gate window the same way a real owner does before relying on the corner window.
            _window.Hide();
            await Settle();
            Check(!ModuleEntry.HubShowing, "The integrated gate leaves the hub before exercising the corner window");
            await CornerScenes.Gate();
        }
        if (slice is null or "settings") await SettingsScenes.Gate();
        if (slice is null or "firstrun") await FirstRunScenes.Gate();
        Check(OwnerAgentEntries() == ownerAgents,
            "The gate left Deskweave's entry in the owner's own Claude Code and Codex configuration alone");
    }

    /// <summary>
    /// Deskweave's own entry in the owner's real agent configuration, as text. Nothing in the gate
    /// may write it: a stand-in that misses one path would connect the owner's agents to a debug
    /// build for real, and this is where that shows up. The rest of those files belongs to his own
    /// agent sessions, which rewrite their history while the gate runs, so only the entry is read.
    /// </summary>
    static string OwnerAgentEntries()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Entry(Path.Combine(profile, ".claude.json")) + "|" + Entry(Path.Combine(profile, ".codex", "config.toml"));

        static string Entry(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                {
                    bool inside = false;
                    return string.Concat(File.ReadLines(path).Where(line =>
                    {
                        if (line.TrimStart().StartsWith('[')) inside = line.Trim() == "[mcp_servers.deskweave]";
                        return inside;
                    }));
                }
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                    && servers.TryGetProperty(WorkspaceConnections.AppName, out JsonElement ours) ? ours.GetRawText() : "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return "unreadable"; }
        }
    }

    internal static MainWindow Window => _window;

    static string Lower(ThemeChoice theme) => theme.ToString().ToLowerInvariant();

    static void CheckSettings()
    {
        // Only choices that remain visible after the cut round belong in this default claim.
        AppSettings s = AppSettingsStore.Current;
        Check(s.StartWithWindows && s.Theme == ThemeChoice.FollowWindows && !s.FirstRunDone && !s.ConnectAgents
            && s.AgentsOff.Count == 0 && s.PauseHotkey == "Ctrl+Alt+P" && s.AccountScopes.Count == 0
            && s.CornerShow == CornerShow.ComesAndGoes && !s.CornerPinned
            && s.CornerLeft is null && s.CornerTop is null && s.CornerWidth is null
            && s.Screenshots == ScreenshotMode.KeySteps,
            "Every remaining choice starts at the MVP spec's default");
        AppSettings odd = new AppSettings
        {
            Theme = (ThemeChoice)9, PauseHotkey = "P", CornerWidth = double.NaN, AccountScopes = null!,
            AgentsOff = ["Codex", "Codex", "SomeAgentThisBuildNeverHeardOf"],
        }.Sane();
        Check(odd.Theme == ThemeChoice.FollowWindows && odd.PauseHotkey == "Ctrl+Alt+P"
            && odd.CornerWidth is null && odd.AccountScopes is { Count: 0 }
            && odd.AgentsOff is ["Codex"], "A hand-edited settings file falls back to real choices");
        Check(s.AccountScopes is not Dictionary<string, string> && AppSettingsStore.Current.AccountScopes is not Dictionary<string, string>,
            "Account scopes cannot be changed behind the store's back");
        int heard = 0;
        void Heard(AppSettings _) => heard++;
        AppSettingsStore.Changed += Heard;
        AppSettingsStore.Update(settings => settings with { CornerPinned = true });
        AppSettingsStore.Changed -= Heard;
        Check(heard == 1 && AppSettingsStore.Current.CornerPinned
            && File.ReadAllText(Path.Combine(_output, "settings.json")).Contains("\"CornerPinned\": true"),
            "A settings change is saved and announced once");
        List<ThemeChoice> seen = [];
        void First(AppSettings s)
        {
            seen.Add(s.Theme);
            if (s.Theme == ThemeChoice.Light) AppSettingsStore.Update(settings => settings with { Theme = ThemeChoice.Dark });
        }
        void Second(AppSettings s) => seen.Add(s.Theme);
        AppSettingsStore.Changed += First;
        AppSettingsStore.Changed += Second;
        AppSettingsStore.Update(settings => settings with { Theme = ThemeChoice.Light });
        AppSettingsStore.Changed -= First;
        AppSettingsStore.Changed -= Second;
        Check(seen is [ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Dark],
            "A listener that changes a setting leaves every listener hearing each state once, in order");
        AppSettingsStore.Update(settings => settings with { Theme = ThemeChoice.FollowWindows, CornerPinned = false });
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
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        WorkspaceHome.Route fresh = WorkspaceHome.Decide([], project, "claude-code", _ => false);
        Check(fresh.Existing is null && fresh.Name == "Alpha" && fresh.Rule == WorkspaceHome.Folder(project),
            "An agent nothing fits gets a new workspace named for, and kept for, its project folder");
        WorkspaceHome.Route home = WorkspaceHome.Decide([], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "claude-code", _ => false);
        Check(home.Existing is null && home.Name == "Scratch" && home.Rule == WorkspaceHome.Scratch,
            "An agent started from its home folder gets Scratch");
        static StoredWorkspace Fixture(string id, string rule) => new() { Id = id, Name = id, Agents = rule };
        StoredWorkspace[] set = [Fixture("shared", WorkspaceHome.Anyone), Fixture("codex", WorkspaceHome.Agent("codex")),
            Fixture("alpha", WorkspaceHome.Folder(project)), Fixture("outer", WorkspaceHome.Folder(_output)), Fixture("mine", "")];
        Check(WorkspaceHome.Decide(set, Path.Combine(project, "src"), "codex-mcp-client", _ => false).Existing == "alpha",
            "The deepest matching project folder beats a shallower folder, a named agent and a shared workspace");
        Check(WorkspaceHome.Decide(set, "", "codex-mcp-client", _ => false).Rule == WorkspaceHome.Scratch,
            "Legacy agent-specific rules do not intercept Scratch");
        Check(WorkspaceHome.Decide(set, "", "another-agent", _ => false).Existing is null,
            "Unrelated shared and private workspaces are left alone");
        StoredWorkspace[] pair = [Fixture("scratch", WorkspaceHome.Scratch), Fixture("free", WorkspaceHome.Anyone)];
        Check(WorkspaceHome.Decide(pair, "", "x", id => id == "scratch").Existing == "scratch",
            "Scratch keeps its identity even when another agent is using it");

        // End to end: a real bridge process, the real router, a real desktop.
        StoredWorkspace shared = WorkspaceHome.EnsureScratch();
        WorkspaceAccessStore.Write(shared.Id, new WorkspaceAccessPolicy { PrewarmBrowser = false });
        WorkspaceRouter.Start();
        Check(File.Exists(WorkspaceAccessStore.RouterTicket) && File.Exists(WorkspaceConnections.Bridge),
            "Deskweave publishes one connection for every outside agent, reached through its packaged bridge");
        var start = new ProcessStartInfo(WorkspaceConnections.Bridge)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Remove("CLAUDE_PROJECT_DIR");
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
        _window.ShowStack();
        await Settle();
        Check(_window.Hub.Working.Any(e => e.Id == shared.Id && e.AgentText == "Probe-agent"),
            "The hub names the agent working in a workspace");
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
        await RoutingChecks.Run(_output);
    }

    static void InvokePanel(AgentWorkspacesPanel panel, string method, params object?[] args) =>
        typeof(AgentWorkspacesPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args);

    /// <summary>Calls a private instance member on <paramref name="target"/> - the same reflection
    /// pattern as <see cref="InvokePanel"/>, for members on the window itself (a data-templated card
    /// or row has no name of its own to find and click, and a filter field opened by keyboard has no
    /// button to click either).</summary>
    static void InvokePrivate(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    static void RaiseKey(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });

    static T Find<T>(string name) where T : class =>
        _window.FindName(name) as T ?? throw new InvalidOperationException("Missing control " + name);
    static void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    internal static void Check(bool condition, string claim)
    {
        if (!condition) throw new InvalidOperationException(claim);
        Passed.Add(claim);
    }

    /// <summary>
    /// Stands in for the three engine fields every agent path shares: where an agent's command is,
    /// whether its configuration already names Deskweave, and the one call that writes it. First
    /// launch, Settings and the keep-up loop all read and write through these, so a scene answers
    /// for all three at once and none of them reaches the owner's own agents. Put them back with
    /// the handle this gives.
    /// </summary>
    internal static IDisposable AgentSeams()
    {
        var (locate, connected, set) =
            (WorkspaceConnections.Locate, WorkspaceConnections.IsConnected, WorkspaceConnections.SetConnected);
        return new Restore(() =>
            (WorkspaceConnections.Locate, WorkspaceConnections.IsConnected, WorkspaceConnections.SetConnected)
                = (locate, connected, set));
    }

    /// <summary>What is on this PC, as the three states a row shows.</summary>
    internal static void Agents(Func<WorkspaceConnections.AgentApp, AgentState> state)
    {
        WorkspaceConnections.Locate = app => state(app) == AgentState.NotInstalled ? null : "agent.exe";
        WorkspaceConnections.IsConnected = app => state(app) == AgentState.Connected;
    }

    internal sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

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
