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
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

namespace Deskweave.UiProbe;

/// <summary>
/// Where the gate's real windows go: a monitor other than the primary one when there is one, so a
/// run leaves the owner's main screen alone while he works (the harness scenes already render off
/// every monitor). The app's own defaults are pointed there through their test hooks; the placement
/// rules under test are the same on any monitor.
/// </summary>
static class TestScreen
{
    internal static Rect Work { get; private set; } = SystemParameters.WorkArea;
    internal static bool OwnerFullscreen { get; private set; }

    internal static void Use()
    {
        OwnerFullscreen = WorkspacePresentation.Suppressed;
        string occupied = System.Windows.Forms.Screen.FromHandle(WorkspacePresentation.ForegroundWindow).DeviceName;
        IReadOnlyList<Rect> fullscreen = WorkspacePresentation.FullscreenMonitors;
        if (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary
            && !fullscreen.Any(bounds => bounds.Left < s.Bounds.Right && bounds.Right > s.Bounds.Left
                && bounds.Top < s.Bounds.Bottom && bounds.Bottom > s.Bounds.Top)
            && (!OwnerFullscreen || fullscreen.Count > 0 || s.DeviceName != occupied)) is not { } other)
        {
            if (OwnerFullscreen) throw new InvalidOperationException("No secondary monitor is available for UI checks while a fullscreen app is visible. Close fullscreen before running the visible UI gate.");
            return;
        }
        System.Drawing.Rectangle px = other.WorkingArea;
        double guess = WorkspacePeekPlacement.PrimaryScale();
        Work = WorkspacePeekPlacement.MonitorFor(new Rect(px.Left / guess, px.Top / guess, px.Width / guess, px.Height / guess)).WorkArea;
        WorkspacePeekPlacement.HomeWorkArea = () => Work;
        ShellPlacement.Home = () => other;
    }
}

static class Program
{
    static readonly List<string> Passed = [];
    static MainWindow _window = null!;
    static string _output = "";
    internal static string Output => _output;

    static readonly ThemeChoice[] Themes = [ThemeChoice.Light, ThemeChoice.Dark];

    [STAThread]
    static int Main(string[] args)
    {
        try { return RunMain(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            // Startup failures precede the dispatcher handler. Keep them out of Windows Error
            // Reporting's modal dialog while preserving a failing process exit and diagnostics.
            if (_output.Length > 0)
            {
                try { Report(error); }
                catch (Exception reportError) { Console.Error.WriteLine(reportError); }
            }
            return 2;
        }
    }

    static int RunMain(string[] args)
    {
        // A folder: the UI gate. --mvp <folder> [scene name prefix]: the reference-screen harness (Mvp.cs).
        bool mvp = args.Length is 2 or 3 && args[0] == "--mvp";
        bool cornerRendering = args.Length == 2 && args[0] == "--corner-rendering";
        bool cornerDocking = args.Length == 2 && args[0] == "--corner-docking";
        if (!mvp && !cornerRendering && !cornerDocking && (args.Length != 1 || args[0].StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Usage: Deskweave.UiProbe <output-folder> | --mvp <output-folder> [scene-prefix] | --corner-rendering <output-folder> | --corner-docking <output-folder>");
            return 2;
        }
        _output = Path.GetFullPath(mvp || cornerRendering || cornerDocking ? args[1] : args[0]);
        Directory.CreateDirectory(_output);
        // The scenes stand in for every agent seam; if one is ever missed, what it writes lands here and
        // not in the owner's own configuration, which is what the gate-1 run did.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(_output, "agents", "claude"));
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(_output, "agents", "codex"));
        WorkspaceConnections.Profiles = app =>
        {
            bool claude = app == WorkspaceConnections.AgentApp.ClaudeCode;
            string root = Path.Combine(_output, "agents", claude ? "claude" : "codex");
            return [new(app, "Probe", root, Path.Combine(root, claude ? ".claude.json" : "config.toml"))];
        };
        // ponytail: one probe at a time on this PC, so parallel worktrees don't fight over the screen,
        // focus and CPU timings. Never released by hand; closing it at exit hands the turn on.
        using var turn = new Mutex(false, @"Local\Deskweave.Probe.Turn");
        try { turn.WaitOne(); } catch (AbandonedMutexException) { }
        ProductContext.Configure("DeskweaveUiProbe");
        using var scope = WorkspaceStore.UseRootForTests(Path.Combine(_output, "workspaces"));
        using var preferences = ShellPreferences.UseFileForTests(Path.Combine(_output, "shell.json"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(_output, "settings.json"));
        TestScreen.Use();
        // Fixtures use the selected spare monitor. Their synthetic fullscreen states must not
        // depend on the owner's game, and their explicit navigation must not activate over it.
        WorkspacePresentation.SuppressedForTests = () => false;
        MainWindow.AllowActivationForTests = false;
        using var focus = new FocusGuard();
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
            try
            {
                if (cornerDocking)
                {
                    AppearanceManager.Apply(ThemeChoice.Light);
                    await CornerScenes.DockingChecks();
                }
                else if (cornerRendering)
                {
                    AppearanceManager.Apply(ThemeChoice.Light);
                    await CornerScenes.RenderingLifecycleChecks();
                }
                else if (mvp) await Mvp.Run(_output, args.Length == 3 ? args[2] : null);
                else await Run();
            }
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
        return application.Run();
    }

    static async Task Run()
    {
        string? slice = Environment.GetEnvironmentVariable("DESKWEAVE_UI_GATE_SLICE")?.Trim().ToLowerInvariant();
        if (slice is not null and not ("hub" or "corner" or "settings" or "firstrun" or "scaling"))
            throw new ArgumentException("DESKWEAVE_UI_GATE_SLICE must be hub, corner, settings, firstrun or scaling.");
        if (slice != "settings") await TrayChecks.Run();
        _window = new MainWindow { ShowActivated = false, Left = TestScreen.Work.Left + 20, Top = TestScreen.Work.Top + 20 };
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
        // One picture before it stops, so there is a last look for Recent to keep.
        WorkspaceRuntime.Of(created.Id)!.Plane!.Frame();
        WorkspaceRuntime.Of(created.Id)?.Dispose();
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is null && _window.Hub.Asleep.Any(e => e.Id == created.Id),
            "Stopping a workspace's computer moves it from Working to Recent");
        HubEntry recent = _window.Hub.Asleep.First(e => e.Id == created.Id);
        for (int i = 0; i < 20 && recent.Preview is null; i++) await Settle();
        Check(recent.Preview is not null,
            "A workspace under Recent shows the last look its screen had, not an empty card");
        WorkspaceRuntime.Start(WorkspaceStore.Find(created.Id)!);
        await Settle();
        Check(WorkspaceRuntime.Of(created.Id) is not null && _window.Hub.Working.Any(e => e.Id == created.Id),
            "Starting it again brings it back under Working");

        // Rows expand in place; Show more opens the full workspace.
        InvokePrivate(_window, "WorkingCard_Click", new Button { Tag = created.Id }, new RoutedEventArgs());
        await Settle();
        Check(_window.DisplayMode == "stack" && _window.Hub.Find(created.Id)!.Expanded,
            "Clicking a working card expands that workspace inside the strip");
        _window.ShowWide(created.Id);
        await Settle();
        Check(_window.Hub.Working.Any(e => e.Id == created.Id && e.Selected), "The sidebar marks the open workspace selected");
        Capture("03-wide.png");
        await ScreenClickChecks(created.Id);
        _window.ShowStack();
        await Settle();
        await ScrollAndEmptyChecks();

        string scratchId = _window.Hub.Asleep.First(e => e.Name == "Scratch").Id;
        InvokePrivate(_window, "AsleepRow_Click", new Button { Tag = scratchId }, new RoutedEventArgs());
        await Settle();
        Check(_window.DisplayMode == "stack" && _window.Hub.Find(scratchId)!.Expanded,
            "Clicking a recent row expands that workspace inside the strip");
        _window.TogglePreview(scratchId);
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

        // The gear opens Settings inside the card, the window keeping its size (owner's pick,
        // 2026-09-22); Escape from a wide workspace returns to the stack (brief A.5).
        double stackWidth = _window.ActualWidth;
        Click("SettingsButton");
        await Settle();
        Check(_window.DisplayMode == "cardsettings" && _window.CardSettings is { IsVisible: true, AtCardHome: true }
            && Math.Abs(_window.ActualWidth - stackWidth) < 2, "The gear button opens Settings inside the card");
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
        if (slice is null or "hub") await HubScenes.Gate();
        if (slice is null or "hub") await InlineScenes.Gate();
        if (slice is null or "hub") await CardScenes.Gate();
        if (slice is null or "corner")
        {
            // Corner behavior is intentionally suppressed while any hub is visible. Leave the main
            // gate window the same way a real owner does before relying on the corner window.
            _window.Hide();
            await Settle();
            Check(!ModuleEntry.HubShowing, "The integrated gate leaves the hub before exercising the corner window");
            await CornerScenes.Gate();
            await CornerManyScenes.Gate();
        }
        if (slice is null or "settings") await SettingsScenes.Gate();
        if (slice is null or "firstrun") await FirstRunScenes.Gate();
        if (slice is null or "scaling") await ScalingScenes.Gate();
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
        // This fixture permits an outside agent while suppressing browser prewarm. An explicit
        // disabled policy represents owner revocation, which the router must continue to respect.
        WorkspaceAccessStore.Write(shared.Id, new WorkspaceAccessPolicy(true, false) { PrewarmBrowser = false });
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
            "An agent's first workspace tool lands it in the shared workspace, started for it, under its own name"
                + (sharedRuntime?.Access?.Controller == "probe-agent" ? "" : ": " + acquired.GetRawText()));
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


    /// <summary>Calls a private instance member on <paramref name="target"/> - the same reflection
    /// pattern as the reflection helpers, for members on the window itself (a data-templated card
    /// or row has no name of its own to find and click, and a filter field opened by keyboard has no
    /// button to click either).</summary>
    static void InvokePrivate(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    static void RaiseKey(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });


    /// <summary>
    /// The owner's hand on the live screen (brief A.3). A picture of a whole desktop drawn a few
    /// hundred pixels wide is easy to get subtly wrong - an overlay over the picture, a stretch the
    /// mapping does not match - and every one of those ends as "my clicks do nothing", so this
    /// drives a real click through the real page and asks the workspace where it landed. What it
    /// found goes to screen-click.txt beside the scene shots.
    /// </summary>
    static async Task ScreenClickChecks(string id)
    {
        var lines = new List<string>();
        WorkspaceFullView view = _window.OpenWorkspaceView!;
        System.Windows.Controls.Image picture = view.ScreenPicture;
        AgentDesktop computer = WorkspaceRuntime.Of(id)!.Computer!;
        // A workspace starts empty; the click needs a window to land on, so this brings its own.
        if (computer.Windows().Count == 0) computer.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        for (int i = 0; i < 40 && (picture.Source is null || computer.Windows().Count == 0); i++) await Settle();
        var source = (BitmapSource?)picture.Source;
        lines.Add($"screen metrics : {AgentDesktop.ScreenWidth}x{AgentDesktop.ScreenHeight}");
        lines.Add($"frame          : {(source is null ? "none" : $"{source.PixelWidth}x{source.PixelHeight}")}");
        lines.Add($"picture        : {picture.ActualWidth:F2}x{picture.ActualHeight:F2} {picture.Stretch}");
        Check(source is not null && source.PixelWidth == AgentDesktop.ScreenWidth,
            "The workspace page draws the whole workspace screen at its own size");

        // Every part of the picture takes a click: the taskbar strip is the only thing over it, and
        // only along the bottom.
        foreach (double fraction in new[] { 0.04, 0.5, 0.88 })
        {
            Point inWindow = picture.TransformToAncestor(_window)
                .Transform(new Point(picture.ActualWidth / 2, picture.ActualHeight * fraction));
            DependencyObject? landed = null;
            VisualTreeHelper.HitTest(_window, null, r => { landed = r.VisualHit; return HitTestResultBehavior.Stop; },
                new PointHitTestParameters(inWindow));
            lines.Add($"hit at {fraction:P0} down : {(landed is null ? "NOTHING" : landed.GetType().Name)}");
            Check(ReferenceEquals(landed, picture),
                $"A click {fraction:P0} of the way down the workspace screen reaches the picture itself");
        }

        // And the click the owner makes there arrives on the window that is under that point.
        IReadOnlyList<AgentWindow> windows = computer.Windows();
        Check(windows.Count > 0, "The workspace under test has a window to click");
        AgentWindow front = windows[0];
        double scale = picture.ActualWidth / AgentDesktop.ScreenWidth;
        var aim = new Point((front.X + front.Width / 2.0) * scale, (front.Y + front.Height / 2.0) * scale);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool taken = view.ScreenInput!.Press(aim, false);
        clock.Stop();
        lines.Add($"press {aim.X:F0},{aim.Y:F0}   : taken={taken} uiThreadMs={clock.ElapsedMilliseconds}");
        Check(taken, "A press on the picture is taken by the workspace page");
        Check(clock.ElapsedMilliseconds < 100,
            "The window never waits on the workspace's own pump to send a click");
        // The window under the point may be a control inside the front window (Notepad's text
        // area); it is the front window's own either way.
        for (int i = 0; i < 20 && RootOf(computer.LastClickedForTests) != front.Handle; i++) await Settle();
        lines.Add($"landed on      : {computer.LastClickedForTests} (front {front.Handle})");
        Check(RootOf(computer.LastClickedForTests) == front.Handle,
            "A click on the picture lands on the window that is under that point on the workspace screen");

        // And the whole gesture, which is what makes the picture the machine rather than a remote
        // control with one button: a window dragged by its title bar moves. Windows own move loop
        // cannot do this from here - it waits on a cursor that is not on this desktop - so the
        // engine moves the window itself, and this is the check that says it still does.
        AgentWindow before = computer.Windows()[0];
        var grab = new Point((before.X + before.Width / 2.0) * scale, (before.Y + 15) * scale);
        const int byX = 120, byY = 80;
        // Where that drag can actually end. A workspace keeps every window on its one screen
        // (WorkspaceScreen.Fit), so a window already near the bottom moves as far as the screen
        // allows and no further. Expecting the raw delta made this check depend on where the
        // workspace happened to put its first window: it passed at 156,156 and failed at 182,182,
        // with the drag itself working perfectly both times.
        int wantX = Math.Clamp(before.X + byX, 0, Math.Max(0, AgentDesktop.ScreenWidth - before.Width));
        int wantY = Math.Clamp(before.Y + byY, 0, Math.Max(0, AgentDesktop.ScreenHeight - before.Height));
        var dropped = new Point(grab.X + byX * scale, grab.Y + byY * scale);
        Check(view.ScreenInput!.Down(grab, false), "A press on a window title bar in the picture is taken");
        view.ScreenInput!.Moved(new Point(grab.X + byX * scale / 2, grab.Y + byY * scale / 2));
        view.ScreenInput!.Moved(dropped);
        view.ScreenInput!.Up(dropped);
        AgentWindow moved = before;
        for (int i = 0; i < 20; i++)
        {
            await Settle();
            moved = computer.Windows().FirstOrDefault(w => w.Handle == before.Handle) ?? before;
            // Wait for where the drag was aimed, not for any movement at all. Owner input crosses
            // to the workspace off-thread in one ordered chain, so the window really does pass
            // through the halfway Moved on its way; breaking on "it has moved" caught it there and
            // failed a drag that was still arriving. A drag that never lands still runs out of
            // attempts and still fails the claim below.
            if (Math.Abs(moved.X - wantX) <= 2 && Math.Abs(moved.Y - wantY) <= 2) break;
        }
        lines.Add($"grab           : {grab.X:F0},{grab.Y:F0} on \"{before.Title}\" ({before.ClassName}) "
            + $"at {before.X},{before.Y} {before.Width}x{before.Height}, scale {scale:F2}");
        lines.Add($"dragged        : {before.X},{before.Y} -> {moved.X},{moved.Y} "
            + $"(asked +{byX},+{byY}, the screen allows {wantX},{wantY})");
        // Written before the claim, not after it: a Check that fails throws, so the run that most
        // needs these numbers was the one run that never wrote them down.
        File.WriteAllLines(Path.Combine(_output, "screen-click.txt"), lines);
        Check(Math.Abs(moved.X - wantX) <= 2 && Math.Abs(moved.Y - wantY) <= 2,
            "Dragging a window by its title bar in the picture moves that window on the workspace screen");
    }

    /// <summary>
    /// The workspace page used to clip "What it did" and "Files" under the picture with no way to
    /// reach anything below the fold, and an empty workspace showed the two headings over blank
    /// space. This writes a real 40-line evidence log, opens the real page at the 960x600 minimum
    /// (the wide window's own floor) and checks the newest step is already on screen, then does the
    /// same for a workspace with nothing in it yet.
    /// </summary>
    static async Task ScrollAndEmptyChecks()
    {
        StoredWorkspace busy = WorkspaceStore.Create("Scroll check");
        string evidence = Path.Combine(WorkspaceStore.FolderOf(busy.Id), "evidence");
        Directory.CreateDirectory(evidence);
        DateTime start = DateTime.UtcNow.AddMinutes(-40);
        var lines = new List<string>();
        for (int i = 0; i < 40; i++)
            lines.Add($"{start.AddMinutes(i):O}\tsave\tstep-{i:D2}.txt\tok");
        File.WriteAllLines(Path.Combine(evidence, "actions.log"), lines);

        _window.ShowWide(busy.Id);
        _window.Width = 960;
        _window.Height = 600;
        await Settle();
        WorkspaceFullView busyView = _window.OpenWorkspaceView!;
        Check(busyView.DidList.Items.Count == 40 && ((DidRow)busyView.DidList.Items[39]!).Prefix == "Saved step-39.txt",
            "A workspace with 40 logged steps keeps every one, newest last");
        Check(FindAncestor<ScrollViewer>(busyView.DidList) == busyView.ActivityScroll
            && FindAncestor<ScrollViewer>(busyView.FilesList) == busyView.ActivityScroll,
            "A ScrollViewer covers both the What it did and Files lists");
        Check(busyView.ActivityScroll.ScrollableHeight > 0
            && busyView.ActivityScroll.VerticalOffset >= busyView.ActivityScroll.ScrollableHeight - 1,
            "At the 960x600 minimum the page opens already scrolled to the newest step, not clipping past it");

        StoredWorkspace empty = WorkspaceStore.Create("Empty check");
        _window.ShowWide(empty.Id);
        await Settle();
        WorkspaceFullView emptyView = _window.OpenWorkspaceView!;
        Check(emptyView.DidList.Items.Count == 0 && emptyView.FilesList.Items.Count == 0
            && emptyView.DidEmptyText.Visibility == Visibility.Visible && emptyView.FilesEmptyText.Visibility == Visibility.Visible,
            "An empty workspace shows \"Nothing yet\" and \"No files yet\" instead of blank columns");

        // Both are throwaway: left behind, they would sit under Recent for the rest of the run and
        // throw off every later check that counts the stack's asleep workspaces.
        _window.ShowStack();
        WorkspaceStore.Delete(busy.Id);
        WorkspaceStore.Delete(empty.Id);
        await Settle();
    }

    static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

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

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
    internal static nint RootOf(nint window) => window == 0 ? 0 : GetAncestor(window, 2);

    internal static void Check(bool condition, string claim)
    {
        if (!condition) throw new InvalidOperationException(claim);
        Passed.Add(claim);
        // Timed, so anything the run did on the owner's desktop can be matched to the check that did it.
        try { File.AppendAllText(Path.Combine(_output, "progress.log"), $"{DateTime.Now:HH:mm:ss.fff} {claim}\n"); }
        catch (IOException) { }
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
        var profiles = SettingsActions.ReadProfileCounts;
        var failure = SettingsActions.ReadConnectionFailure;
        SettingsActions.ReadProfileCounts = app => (WorkspaceConnections.IsConnected(app) ? 1 : 0,
            WorkspaceConnections.IsInstalled(app) ? 1 : 0);
        SettingsActions.ReadConnectionFailure = _ => null;
        return new Restore(() =>
        {
            (WorkspaceConnections.Locate, WorkspaceConnections.IsConnected, WorkspaceConnections.SetConnected)
                = (locate, connected, set);
            SettingsActions.ReadProfileCounts = profiles;
            SettingsActions.ReadConnectionFailure = failure;
        });
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
