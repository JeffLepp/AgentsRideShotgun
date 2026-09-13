using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

public partial class AgentWorkspacesPanel : UserControl, IDisposable
{
    // ponytail: two frames a second. The capture is a GDI blit per window, so it costs far less
    // than one WPF redraw - raise it only if the owner asks for smoother motion.
    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Below this, say so instead of letting a workspace fill the disk quietly.</summary>
    const long LowDiskBytes = 2L << 30;

    StoredWorkspace _stored;
    DemoWorkspace _view;
    WorkspaceFullWindow? _fullWindow;
    WorkspaceRuntime? _live;
    object? _runtimeAttachment;
    Action? _detachRuntime;

    /// <summary>The message the agent is writing into right now, or null between messages.</summary>
    TextBlock? _typing;

    /// <summary>The owner's plan usage as last read. Refreshed when the panel opens on a workspace,
    /// when the settings are opened, and when a mission ends - never on a timer, because the number
    /// comes from a file another module refreshes and this panel has no business polling it.</summary>
    WorkspacePlanReading? _plan;
    DispatcherTimer? _frames;
    CancellationTokenSource? _storageRead;
    CancellationTokenSource? _setupCheck;
    BitmapSource? _lastFrame;
    WorkspacePower _power = WorkspacePower.Light;
    bool _photographed;
    bool _chatCollapsed;
    DispatcherTimer? _ownerIdle;
    bool _ownerByClick;

    /// <summary>How long the owner can leave a screen he clicked into before the agent carries on.</summary>
    static readonly TimeSpan OwnerIdleHandback = TimeSpan.FromSeconds(20);
    bool _disposed;
    bool _setupCheckStarted;
    int _storageGeneration;
    AgentSetupAction _shownSetupAction;

    /// <summary>How many panels this process has built. The only claim that cannot be made any
    /// other way is that a mission woke with none of them, so the count is kept here.</summary>
    public static int Built { get; private set; }

    public AgentWorkspacesPanel()
    {
        Built++;
        _stored = Opening();
        _power = _stored.Power;
        _view = null!;
        InitializeComponent();
        WorkspaceAgentSetup.Changed += SetupChanged;
        Loaded += Panel_Loaded;
        IsVisibleChanged += Panel_IsVisibleChanged;
        // The module's clock, not this panel's. It is started at app startup as well; starting it
        // again here is what makes a panel work in a host that never called Initialize.
        WorkspaceRuntime.Watch();
        ShowAccess();
        StartPeek();
        SetWorkspace(_stored);
    }

    void Panel_Loaded(object sender, RoutedEventArgs e) => UpdateVisibleWork();

    void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateVisibleWork();

    void UpdateVisibleWork()
    {
        if (_disposed) return;
        if (!IsVisible)
        {
            PauseFrames();
            CancelStorageRead();
            CancelSetupCheck();
            return;
        }

        if (_live is not null) ShowLiveScreen();
        UpdateLiveChrome();
        ShowStorage();
        BeginSetupCheck();
    }

    // Read through to the runtime rather than keeping a second copy. The panel is a view of a
    // workspace that is running, not the thing that runs it - which is why closing the page, or
    // never opening it, no longer ends a mission.
    AgentDesktop? _computer => _live?.Computer;
    WorkspaceControl? _plane => _live?.Plane;
    WorkspaceAgent? _agent => _live?.Agent;

    /// <summary>True while this workspace owns a live Windows desktop.</summary>
    internal bool ComputerIsRunning => _computer is not null;

    /// <summary>The shell has finished with this panel. Nothing may put it back on screen.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The record currently displayed, including changes from the built-in switcher.</summary>
    internal string SelectedWorkspaceId => _stored.Id;
    internal event Action? WorkspaceChanged;

    internal void ShowFullWindowButton() => FullWindowButton.Visibility = Visibility.Visible;

    /// <summary>The owner is driving. Read from the lease, not from a second copy of the truth.</summary>
    bool OwnerIsDriving => _plane?.Driving == Driver.Owner;

    /// <summary>
    /// The workspace the panel opens on: the card the owner clicked, else the one he used last.
    /// A machine with no workspaces yet gets one rather than an empty screen.
    /// </summary>
    static StoredWorkspace Opening()
    {
        IReadOnlyList<StoredWorkspace> stored = WorkspaceStore.All();
        string? wanted = ModuleEntry.Selected;
        foreach (StoredWorkspace workspace in stored)
            if (workspace.Id == wanted) return workspace;
        if (stored.Count > 0) return stored[^1];
        return WorkspaceStore.Create("Workspace");
    }

    /// <summary>The stored record as the existing frame and card bindings want to see it.</summary>
    static DemoWorkspace ViewOf(StoredWorkspace workspace, long? size = null) => new(
        workspace.Id,
        workspace.Name,
        "No agent yet",
        workspace.Task,
        WorkspaceVisualState.Sleeping,
        size is { } bytes ? $"Stored · {WorkspaceStore.Human(bytes)}" : "Stored",
        workspace.Name,
        $"Last used {workspace.LastUsed.LocalDateTime:d MMM HH:mm}");

    internal void SetWorkspace(StoredWorkspace workspace)
    {
        if (_disposed) return;
        if (_stored.Id != workspace.Id) SaveLastFrame();
        AgentSettingsPopup.IsOpen = false;
        _accessMessage = "";
        AccessExpander.IsExpanded = false;
        _stored = workspace;
        ModuleEntry.Selected = workspace.Id;
        _power = workspace.Power;
        _view = ViewOf(workspace);
        UpdateFramePlaceholder();
        WorkspaceNameText.Text = workspace.Name;
        CurrentTaskText.Text = _view.Task;
        StorageText.Text = string.Empty;
        // The conversation belongs to the workspace, not to the panel. Switching used to leave the
        // last workspace's transcript and mission line on screen, which reads as this one's.
        Transcript.Children.Clear();
        _typing = null;
        MissionText.Text = workspace.Task;
        ShowMissionCard();
        _lastFrame = LoadLastFrame(workspace.Id);
        RefreshPlanUsage();
        Adopt(WorkspaceRuntime.Of(workspace.Id));
        UpdateLiveChrome();
        ShowStorage();
        _fullWindow?.Retitle(workspace.Name);
        WorkspaceChanged?.Invoke();
    }

    /// <summary>Switches the view while each workspace retains its independent live runtime.</summary>
    void Select(string id)
    {
        if (id == _stored.Id) return;
        foreach (StoredWorkspace workspace in WorkspaceStore.All())
            if (workspace.Id == id) { ModuleEntry.Selected = id; SetWorkspace(workspace); return; }
    }

    void UpdateFramePlaceholder() => ComputerFrame.DataContext = _view with { State = _computer is null ? WorkspaceVisualState.Sleeping : WorkspaceVisualState.Ready };

    void UpdateLiveChrome()
    {
        bool running = _computer is not null;
        UpdateFramePlaceholder();
        StartButton.Content = running ? "Stop computer" : "Start computer";
        AutomationProperties.SetName(
            StartButton,
            running ? "Stop this workspace computer" : "Start this workspace computer");

        StateLabelText.Text = running ? "Running" : "Stored";
        StateDetailText.Text = string.Empty;
        StateDot.SetResourceReference(
            Shape.FillProperty,
            running ? "AWWorkingBrush" : WorkspaceDemoCatalog.BrushKey(WorkspaceVisualState.Sleeping));
        // The size is deliberately not measured here. This runs on every lease change and every
        // mission state change, and pricing the workspace walks every file in it - 825 ms on one
        // with 2000 files. It is measured where it can actually have changed: opening a workspace,
        // starting or stopping the computer, and dropping files on it.

        ControlButton.IsEnabled = running;
        ControlButton.Content = OwnerIsDriving ? "Give back control" : "Take control";
        AutomationProperties.SetName(
            ControlButton,
            OwnerIsDriving ? "Give control of this workspace back" : "Take control of this workspace");
        // Exactly one of the three, and never a guess: this reads the lease the input actually
        // obeys. Until an agent exists, an unclaimed workspace says so rather than blaming one.
        Driver driving = running ? _plane?.Driving ?? Driver.Nobody : Driver.Nobody;
        // An agent between two actions holds no lease, so reading only the lease says nobody is
        // driving in the middle of a working mission - which reads as stopped. The mission is the
        // honest answer to who has the workspace; the lease is the honest answer to who may act.
        ControllerText.Text = driving switch
        {
            Driver.Owner => "You have control · click and type in the screen above",
            Driver.Agent => _live?.Access?.Controller is { Length: > 0 } controller ? controller + " is driving" : "The agent is driving",
            _ when !running => string.Empty,
            _ when _agent?.State is MissionState.Working => "The agent is driving",
            _ => "Nobody is driving",
        };
        ControllerDot.SetResourceReference(
            Shape.FillProperty, driving == Driver.Owner ? "AWAccentBrush" : "AWWorkingBrush");
        ControllerDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        // A real screen replaces the drawn one outright: two desktops on top of each other reads
        // as a rendering bug, and the placeholder has nothing to add once there are real pixels.
        // A stopped workspace keeps showing the last thing it had on screen, which is the whole
        // difference between a workspace that was put down and one that was thrown away.
        //
        // A workspace that has just started has no windows yet, and a desktop with no windows on it
        // cannot be photographed at all. The wait for the first frame is cold-start variance, not a
        // constant - measured at 0.6 s and at 10.8 s on the same machine - and ten seconds of empty
        // black rectangle reads as a hang. Keep whatever it had, the drawing or the frame it was
        // stopped on, until there are real pixels, and say Starting.
        bool pixels = running ? _photographed || _lastFrame is not null : _lastFrame is not null;
        if (!running) LiveScreen.Source = _lastFrame;
        LiveScreen.Visibility = pixels ? Visibility.Visible : Visibility.Collapsed;
        ComputerFrame.Visibility = pixels ? Visibility.Collapsed : Visibility.Visible;
        bool starting = running && !_photographed;
        PreviewOnlyBadge.Visibility = starting ? Visibility.Visible : Visibility.Collapsed;
        // Empty rather than a leftover word while the badge is hidden: the badge is also where the
        // workspace says it has stopped answering, and a stale "Starting" underneath that is a lie
        // waiting for the next time something reads it.
        PreviewBadgeText.Text = starting ? "Starting" : string.Empty;
        ShowScreenState();

        ShowBoss(running);
        if (_stored.WakeAt is { } waking)
            CurrentTaskText.Text = $"Waiting until {waking.LocalDateTime:d MMM HH:mm}";
        ShowPower();
        OpenTerminalButton.IsEnabled = running;
        OpenNotepadButton.IsEnabled = running;
        OpenFilesButton.IsEnabled = running;
        ShowAccess();
    }

    /// <summary>
    /// What the boss agent is doing, from the mission state rather than from a second copy of it.
    /// A PC with no agent CLI on it says so plainly and the workspace still works by hand. Setup is
    /// offered here, but every download and browser sign-in remains behind its own visible click.
    /// </summary>
    void ShowBoss(bool running)
    {
        // A parked workspace whose computer is stopped has no agent object and is still waiting.
        // The record is what knows that, so it answers when there is nothing running to ask.
        // The record answers when nothing is running to ask, so an outcome the owner has not seen
        // survives closing the panel and closing HiveMind.
        // A running agent that has an opinion wins; one that has never been given a mission has no
        // opinion, and then the record answers. Without that second half a workspace whose computer
        // is running reads as Ready over an outcome the owner has not seen.
        MissionState state = _agent?.State is { } live && live != MissionState.Idle ? live
            : _stored.WakeAt is not null ? MissionState.Waiting
            : _stored.Mission;
        bool busy = state == MissionState.Working;
        AgentSetupSnapshot setup = WorkspaceAgentSetup.For(_stored.AgentCredentials);
        bool ready = setup.State == AgentSetupState.Ready;

        BossText.Text = !ready ? setup.State switch
            {
                AgentSetupState.Missing => "Needs setup",
                AgentSetupState.Installing => "Setting up",
                AgentSetupState.SignedOut => "Not signed in",
                AgentSetupState.SigningIn => "Signing in",
                _ => "Setup stopped",
            }
            : state switch
            {
                MissionState.Working => "Working",
                MissionState.Waiting => "Waiting",
                MissionState.NeedsYou => "Needs you",
                MissionState.Done => "Done",
                MissionState.Failed => "Stopped",
                MissionState.Interrupted => "Interrupted",
                _ => running ? "Ready" : "Idle",
            };
        BossDot.SetResourceReference(Shape.FillProperty, !ready ? setup.State switch
        {
            AgentSetupState.Installing or AgentSetupState.SigningIn => "AWWorkingBrush",
            AgentSetupState.Failed => "AWFailedBrush",
            _ => "AWWaitingBrush",
        } : state switch
        {
            MissionState.Working => "AWWorkingBrush",
            MissionState.NeedsYou or MissionState.Interrupted => "AWAccentBrush",
            _ => "AWWaitingBrush",
        });

        ShowSetup(setup);
        // Settings stay open to a working agent. They used to be disabled while it ran, which made
        // the one button that answers "what is this thing spending" dead for exactly as long as the
        // question was worth asking - the whole of a long mission. Nothing in there touches the run
        // in flight: a saved policy is picked up by the next invocation, and the flyout says so.
        AgentSettingsRunNote.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = running && ready;
        // Typeable while it works: what the owner writes now reaches the running mission.
        MissionBox.IsEnabled = running && ready;
        if (_live?.Access?.HasDriver == true) { SendButton.IsEnabled = false; MissionBox.IsEnabled = false; }
        SendGlyph.Opacity = SendButton.IsEnabled ? 1 : 0.4;
        ChatHintText.Text = !ready
            ? "The workspace computer still works by hand while its boss agent is unavailable."
            : _stored.WakeAt is { } asleep
            ? $"Asleep until {asleep.LocalDateTime:d MMM HH:mm}. It wakes itself and carries on."
            : !running ? "Start the computer first."
            : busy ? WorkingHint()
            : state == MissionState.NeedsYou ? "It asked you something. Answer it here."
            : state == MissionState.Interrupted
                ? "This mission was interrupted. Resume it, or send a new one."
            : _plan?.Line() is { } plan ? $"Uses your normal Windows file access. {plan}."
            : "Uses your normal Windows file access.";
        if (_live?.Access?.HasDriver == true)
        {
            BossText.Text = "Connected agent active";
            ChatHintText.Text = "Release the connected agent's control before starting the built-in boss.";
        }
        // Interrupted work is the one state that offers an action rather than only a word. The
        // mission is not gone: the CLI's own conversation is on disk and can be handed back.
        bool resumable = ready && WorkspaceMissions.CanResume(state)
            && (_live?.Access?.HasDriver != true) && _stored.Session.Length > 0;
        ResumeMissionButton.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        ResumeMissionButton.IsEnabled = resumable;
        // Stopping used to be what the Send button did while a mission ran. It has its own
        // button so that sending can mean sending.
        bool stoppable = ready && busy && _live?.Access?.HasDriver != true;
        StopMissionButton.Visibility = stoppable ? Visibility.Visible : Visibility.Collapsed;
        StopMissionButton.IsEnabled = stoppable;
        ShowUsage();
        UpdateHint();
    }

    internal void ShowSetup(AgentSetupSnapshot setup)
    {
        _shownSetupAction = setup.Action;
        bool ready = setup.State == AgentSetupState.Ready;
        AgentSetupPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        TranscriptScroll.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SetupHeadingText.Text = setup.Heading;
        SetupDetailText.Text = setup.Detail;
        bool progress = setup.IsBusy;
        SetupProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        SetupProgress.IsIndeterminate = progress && setup.ProgressPercent is null;
        SetupProgress.Value = setup.ProgressPercent ?? 0;
        SetupActionButton.Visibility = setup.Action == AgentSetupAction.None
            ? Visibility.Collapsed : Visibility.Visible;
        SetupActionButton.IsEnabled = !setup.IsBusy;
        SetupActionButton.Content = setup.Action == AgentSetupAction.Install ? "Set up" : "Sign in";
        AutomationProperties.SetName(SetupActionButton,
            setup.Action == AgentSetupAction.Install ? "Set up the boss agent" : "Sign in to Claude Code");
    }

    void SetupChanged(AgentSetupSnapshot setup)
    {
        if (_disposed) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_disposed) ShowBoss(_computer is not null);
        });
    }

    async void SetupActionButton_Click(object sender, RoutedEventArgs e)
    {
        AgentSetupAction action = _shownSetupAction;
        if (action == AgentSetupAction.Install) await WorkspaceAgentSetup.InstallAsync();
        else if (action == AgentSetupAction.SignIn) await WorkspaceAgentSetup.SignInAsync();
    }

    void AgentSettingsButton_Click(object sender, RoutedEventArgs e) => ShowAgentSettings();

    /// <summary>Stages the current workspace policy in the compact settings flyout.</summary>
    internal void ShowAgentSettings()
    {
        bool subscription = _stored.AgentCredentials == WorkspaceAgentCredentialMode.Subscription;
        SubscriptionCredentialsChoice.IsChecked = subscription;
        ConfiguredCredentialsChoice.IsChecked = !subscription;

        bool limited = _stored.RunUsageCeilingTokens is not null;
        LimitedRunChoice.IsChecked = limited;
        UnlimitedRunChoice.IsChecked = !limited;
        // Written in millions, because the number is in the millions and nobody wants to count
        // zeroes in a text box. The record keeps the tokens themselves.
        long shown = _stored.RunUsageCeilingTokens ?? WorkspaceAgent.DefaultRunUsageCeilingTokens;
        RunCeilingBox.Text = (shown / (double)Millions).ToString("0.####", CultureInfo.CurrentCulture);
        AgentSettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateAgentSettingsForm();
        AgentSettingsPopup.IsOpen = true;
    }

    void AgentSettingsChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        AgentSettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateAgentSettingsForm();
    }

    void UpdateAgentSettingsForm()
    {
        bool configured = ConfiguredCredentialsChoice.IsChecked == true;
        CredentialsDetailText.Text = configured
            ? "Uses credentials and providers already configured for Claude Code."
            : "Uses Claude sign-in and ignores API/provider overrides.";
        ShowPlanUsage(configured);
        bool limited = LimitedRunChoice.IsChecked == true;
        RunCeilingInput.IsEnabled = limited;
        RunCeilingInput.Opacity = limited ? 1 : 0.45;
    }

    /// <summary>
    /// What a subscription workspace has spent, in the unit the plan is measured in. It is the
    /// owner's whole plan, not this workspace's share of it, and the line says so: nothing here can
    /// attribute a percentage of an account limit to one mission, and pretending otherwise would be
    /// the same mistake as quoting a subscription in dollars.
    ///
    /// Read when the panel is opened, not polled. The number comes from the usage tracker's own
    /// snapshot, so this costs a small file read and none of the provider's read allowance.
    /// </summary>
    void ShowPlanUsage(bool configured)
    {
        if (!configured) RefreshPlanUsage();
        string? line = configured ? null : _plan?.Line();
        // What the provider reported about the owner's own plan, and nothing about billing.
        // This module deliberately makes no claim about what a run is charged - a committed
        // test guards that - so it shows the percentages and stops there.
        PlanUsageText.Text = configured ? string.Empty
            : line is not null
                ? "Your plan: " + line
                : "Plan usage is unavailable in this build. Run token totals are shown separately.";
        PlanUsageText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Re-reads the tracker's snapshot. A small file read, and no provider call.</summary>
    void RefreshPlanUsage() => _plan =
        _stored.AgentCredentials == WorkspaceAgentCredentialMode.Subscription
            ? WorkspacePlanUsage.Read(DateTimeOffset.Now)
            : null;

    /// <summary>
    /// What the run has spent so far, in the unit that is true for how this workspace is paying.
    /// A subscription run is counted in tokens against the plan's own limits; only CLI/API
    /// credentials are actually billed the dollars the CLI reports.
    /// </summary>
    string Spent() => WorkspaceAgent.UsageSpent(
        _stored.AgentCredentials, _agent?.UsageTokens ?? 0, _agent?.UsageUsd ?? 0);

    /// <summary>What is under the box while a run is in flight, spend included.</summary>
    string WorkingHint() => $"Working · {Spent()} · Type to send it a message.";

    /// <summary>
    /// The workspace's token counter, under the mission it is counting. The run in flight comes
    /// from the agent, which updates it as the CLI reports each request; the total across every run
    /// comes from the record, which is the only thing that survives a mission running with this
    /// panel closed.
    /// </summary>
    void ShowMissionCard() => MissionCard.Visibility =
        MissionText.Text.Length > 0 || UsageText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    void ShowUsage()
    {
        UsageText.Text = WorkspaceAgent.UsageLine(
            _stored.AgentCredentials, _agent?.UsageTokens ?? 0,
            _stored.UsageTokensTotal, _stored.UsageUsdTotal, _stored.UsageRuns);
        ShowMissionCard();
        // The hint carries the same run's spend and is otherwise only redrawn when the mission's
        // state moves - which is not while it is moving. Left alone it would sit on "no tokens yet"
        // for the whole of a run, under a counter saying otherwise.
        if (_agent?.State == MissionState.Working && _live?.Access?.HasDriver != true)
            ChatHintText.Text = WorkingHint();
    }

    void SaveAgentSettings_Click(object sender, RoutedEventArgs e) => SaveAgentSettings();

    /// <summary>Validates and persists the staged policy. Returns false without changing the record.</summary>
    internal bool SaveAgentSettings()
    {
        long? ceiling = null;
        if (LimitedRunChoice.IsChecked == true)
        {
            string value = RunCeilingBox.Text.Trim();
            bool parsed = double.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out double millions)
                || double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out millions);
            // The upper bound is not a policy, it is arithmetic: a number large enough to overflow
            // the count it turns into is not a ceiling anybody meant to set.
            if (!parsed || !double.IsFinite(millions) || millions <= 0 || millions > 1_000_000)
            {
                AgentSettingsErrorText.Text = "Enter a number of millions of tokens above zero.";
                AgentSettingsErrorText.Visibility = Visibility.Visible;
                RunCeilingBox.Focus();
                RunCeilingBox.SelectAll();
                return false;
            }
            ceiling = Math.Max(1, (long)Math.Round(millions * Millions));
        }

        WorkspaceAgentCredentialMode credentials = ConfiguredCredentialsChoice.IsChecked == true
            ? WorkspaceAgentCredentialMode.ClaudeConfiguration
            : WorkspaceAgentCredentialMode.Subscription;
        Store(_stored with
        {
            AgentCredentials = credentials,
            RunUsageCeilingTokens = ceiling,
        });
        AgentSettingsPopup.IsOpen = false;
        ShowBoss(_computer is not null);
        if (credentials == WorkspaceAgentCredentialMode.Subscription)
            BeginSetupCheck(force: true);
        return true;
    }

    void CancelAgentSettings_Click(object sender, RoutedEventArgs e) => AgentSettingsPopup.IsOpen = false;

    void AgentSettingsPopup_Closed(object? sender, EventArgs e) =>
        AgentSettingsErrorText.Visibility = Visibility.Collapsed;

    void RunCeilingBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveAgentSettings(); }
        else if (e.Key == Key.Escape) { e.Handled = true; AgentSettingsPopup.IsOpen = false; }
    }

    void UpdateHint() =>
        MissionHint.Visibility = MissionBox.Text.Length == 0 && MissionBox.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;

    void MissionBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateHint();

    void MissionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Send();
    }

    /// <summary>
    /// Rereads this workspace's record and redraws from it, the way opening the panel would. Only
    /// the measurement probes call it; the panel itself redraws from its own events.
    /// </summary>
    internal void ReloadForTests()
    {
        _stored = WorkspaceStore.Find(_stored.Id) ?? _stored;
        UpdateLiveChrome();
    }

    /// <summary>The unit the per-run ceiling is typed in. The record keeps tokens.</summary>
    const long Millions = 1_000_000;

    void SendButton_Click(object sender, RoutedEventArgs e) => Send();

    /// <summary>Ends the run now. The workspace and everything open in it are left alone.</summary>
    void StopMissionButton_Click(object sender, RoutedEventArgs e)
    {
        StopMissionButton.IsEnabled = false;
        _agent?.Stop();
    }

    /// <summary>
    /// Picks an interrupted mission back up. The agent is handed its own conversation and told that
    /// it was interrupted rather than that it slept, so it checks whether its last step finished
    /// instead of repeating it.
    /// </summary>
    void ResumeMissionButton_Click(object sender, RoutedEventArgs e)
    {
        ResumeMissionButton.IsEnabled = false;
        StoredWorkspace? stored = WorkspaceStore.Find(_stored.Id) ?? _stored;
        if (!WorkspaceRuntime.ResumeInterrupted(stored))
        {
            ResumeMissionButton.IsEnabled = true;
            ChatHintText.Text = "This mission could not be resumed. Send it a new message instead.";
            return;
        }
        _stored = WorkspaceStore.Find(_stored.Id) ?? _stored;
        Adopt(WorkspaceRuntime.Of(_stored.Id));
        Speak("You", "Resume this mission.");
        UpdateLiveChrome();
    }

    void Send()
    {
        if (_agent is null) return;
        if (_live?.Access?.HasDriver == true) return;

        string mission = MissionBox.Text.Trim();
        if (mission.Length == 0) return;

        // A mission already running is talked to, not stopped. Sending used to be the only way to
        // say anything to a working agent and it killed the run, so correcting one sentence cost
        // the owner everything the agent had done in the ten minutes before it. Stopping is its own
        // button now, for when stopping is what he means.
        if (_agent.State == MissionState.Working && _live?.Interject(mission) == true)
        {
            MissionBox.Text = string.Empty;
            Speak("You", mission);
            UpdateHint();
            return;
        }

        MissionBox.Text = string.Empty;
        MissionText.Text = mission;
        ShowMissionCard();
        // On the record, not just on screen: the dashboard card and the workspace itself still know
        // what it was asked to do after a switch, a restart or a reboot. Only the record - rebuilding
        // the view here would price the workspace again for a drawing that is behind live pixels.
        Store(_stored with { Task = mission });
        _live?.Note("You", mission);
        Speak("You", mission);
        UpdateLiveChrome();
        // Fire and forget on purpose: the run outlasts this click by minutes, and everything it has
        // to say arrives on its events. Nothing here waits on it and nothing polls it.
        _ = _live?.Run(mission);
    }

    /// <summary>
    /// The mission moved. The runtime is what writes the conversation id and the wake time to the
    /// record, because the thing that picks a mission up is not always a panel; this only catches up
    /// with what it wrote, since the panel holds a copy of the record for the other fields.
    /// </summary>
    void AgentMoved()
    {
        if (_disposed) return;
        if (WorkspaceStore.Find(_stored.Id) is { } fresh) _stored = fresh;
        // A run that just ended moved the plan's own numbers. Re-read once, here, rather than on
        // every lease change - the tracker refreshes the file on its own budget either way.
        if (_agent?.State is MissionState.Done or MissionState.Failed or MissionState.Waiting)
            RefreshPlanUsage();
        UpdateLiveChrome();
    }

    /// <summary>
    /// Takes on a workspace that is already running: one the module's clock woke with nothing on
    /// screen, or one whose page was closed and reopened while its mission carried on. The
    /// conversation is replayed out of the runtime, or a mission that ran while nobody was watching
    /// would open on an empty chat and read as if it had never happened.
    /// </summary>
    void Adopt(WorkspaceRuntime? runtime)
    {
        if (ReferenceEquals(runtime, _live)) return;
        Leave();
        _live = runtime;
        if (_live is null) return;
        // Capture this exact attachment, including a later return to the same runtime. Events
        // already queued from the previous view must not append to another workspace or detach it.
        object attachment = _runtimeAttachment = new();
        Action<string> said = text => PostForRuntime(attachment, () => Speak("Agent", text));
        Action<string> saying = text => PostForRuntime(attachment, () => Typing(text));
        Action<string, string> acting = (tool, detail) => PostForRuntime(attachment, () => Doing(tool, detail));
        Action<MissionState> moved = _ => PostForRuntime(attachment, AgentMoved);
        Action<bool> metered = banked => PostForRuntime(attachment, () => OnMetered(banked));
        Action<Driver> driver = _ => PostForRuntime(attachment, UpdateLiveChrome);
        Action ended = () => PostForRuntime(attachment, OnEnded);
        Action access = () => PostForRuntime(attachment, UpdateLiveChrome);
        runtime!.Said += said;
        runtime.Saying += saying;
        runtime.Acting += acting;
        runtime.Moved += moved;
        runtime.Metered += metered;
        runtime.DriverChanged += driver;
        runtime.Ended += ended;
        runtime.AccessChanged += access;
        _detachRuntime = () =>
        {
            runtime.Said -= said;
            runtime.Saying -= saying;
            runtime.Acting -= acting;
            runtime.Moved -= moved;
            runtime.Metered -= metered;
            runtime.DriverChanged -= driver;
            runtime.Ended -= ended;
            runtime.AccessChanged -= access;
        };
        foreach ((string who, string what) in _live.Conversation) Speak(who, what);
        if (_live.Doing.Length > 0) CurrentTaskText.Text = _live.Doing;
        ShowLiveScreen();
    }

    /// <summary>Stops drawing this runtime. It does not stop the runtime: that is StopComputer.</summary>
    void Leave()
    {
        // A screen the owner clicked into and then walked away from goes back to its agent.
        GiveBackClickControl();
        if (_live is null) return;
        _runtimeAttachment = null;
        _detachRuntime?.Invoke();
        _detachRuntime = null;
        _live = null;
        // Whatever it was part way through saying stays on screen as the last thing it said. The
        // message it belonged to is finishing somewhere this panel is no longer listening, so the
        // next one to arrive must open its own rather than being written into the end of that.
        _typing = null;
        StopFrames();
    }

    // The callback checks ownership when Windows actually dispatches it, after any intervening
    // view change. Unsubscribing an event alone cannot withdraw a queued dispatcher operation.
    void PostForRuntime(object attachment, Action update) => Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed && ReferenceEquals(_runtimeAttachment, attachment)) update();
    });

    /// <summary>
    /// The counter moved. The record is re-read only when a finished run was just added to the
    /// workspace's total; the rest of the time this is the running total of the run in flight and
    /// costs no file read at all.
    /// </summary>
    void OnMetered(bool banked)
    {
        if (_disposed) return;
        if (banked && WorkspaceStore.Find(_stored.Id) is { } fresh) _stored = fresh;
        ShowUsage();
    }

    void OnEnded()
    {
        if (_live is null || _disposed) return;
        SaveLastFrame();
        Leave();
        UpdateLiveChrome();
    }

    /// <summary>Two frames a second, and only while a panel is watching. A workspace running with
    /// nothing on screen costs no capture at all.</summary>
    void ShowLiveScreen()
    {
        if (!IsVisible || _frames is not null) return;
        _frames = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
        _frames.Tick += DrawFrame;
        _frames.Start();
    }

    /// <summary>
    /// Says so when the picture is not the workspace as it is now. Without this the panel shows a
    /// frozen screen that looks exactly like a working one, and the owner watches a stale picture
    /// wondering why nothing is happening.
    /// </summary>
    void ShowScreenState()
    {
        if (PreviewOnlyBadge is null || _plane is not { } plane) return;
        string badge = plane.ScreenState.Badge;
        if (badge.Length == 0)
        {
            // Leave whatever the ordinary chrome decided; only take the badge over while stalled.
            if (_stalled) { _stalled = false; UpdateLiveChrome(); }
            return;
        }
        _stalled = true;
        PreviewOnlyBadge.Visibility = Visibility.Visible;
        PreviewBadgeText.Text = badge;
    }

    bool _stalled;

    void StopFrames()
    {
        PauseFrames();
        ShowCommands();
        _photographed = false;
        LiveScreen.Source = null;
        LiveScreen.Visibility = Visibility.Collapsed;
    }

    void PauseFrames()
    {
        _frames?.Stop();
        if (_frames is not null) _frames.Tick -= DrawFrame;
        _frames = null;
    }

    void Speak(string who, string what)
    {
        // The finished form of the message that was being typed. It replaces what streamed rather
        // than being added under it: the CLI reports a model's text twice on purpose, and printing
        // both is how a panel ends up showing everything the agent said in duplicate.
        if (_typing is not null && who == "Agent")
        {
            _typing.Text = what.Trim();
            _typing = null;
            TranscriptScroll.ScrollToEnd();
            return;
        }
        Opens(who);
        var message = new TextBlock
        {
            Text = what.Trim(),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "ShellTextBrush");
        Transcript.Children.Add(message);
        TranscriptScroll.ScrollToEnd();
    }

    /// <summary>
    /// A piece of what the agent is writing. This is the whole of the typing effect: a message
    /// opens the first time a piece of it arrives and grows in place until it is finished.
    /// </summary>
    void Typing(string typed)
    {
        if (_disposed) return;
        if (_typing is null)
        {
            Opens("Agent");
            _typing = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            _typing.SetResourceReference(TextBlock.ForegroundProperty, "ShellTextBrush");
            Transcript.Children.Add(_typing);
        }
        _typing.Text += typed;
        TranscriptScroll.ScrollToEnd();
    }

    /// <summary>The rule and the name that open one turn in the transcript.</summary>
    void Opens(string who)
    {
        if (Transcript.Children.Count > 0)
        {
            var divider = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 13, 0, 13),
            };
            divider.SetResourceReference(Border.BackgroundProperty, "ShellDividerBrush");
            Transcript.Children.Add(divider);
        }
        var speaker = new TextBlock
        {
            Text = who,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
        };
        speaker.SetResourceReference(TextBlock.ForegroundProperty, "ShellMutedBrush");
        Transcript.Children.Add(speaker);
    }

    /// <summary>
    /// What the agent is doing right this second, in the strip under the screen. It comes from the
    /// tool call itself, so it stays honest while the model is thinking and costs nothing to produce.
    /// </summary>
    void Doing(string tool, string detail)
    {
        CurrentTaskText.Text = tool switch
        {
            "look" => "Looking at the screen",
            "controls" => "Reading a window",
            "press" or "click" => "Clicking",
            "write" or "type" or "key" => "Typing",
            "open" => "Opening " + detail.Replace("program=", ""),
            "browse" or "page" or "page_click" or "page_type" => "In the browser",
            "wait" => "Waiting",
            "sleep" => "Going to sleep",
            "done" or "ask" => "Finishing up",
            _ => tool,
        };
    }

    void ShowStorage()
    {
        if (!IsVisible || _disposed) return;
        CancelStorageRead();
        string id = _stored.Id;
        int generation = ++_storageGeneration;
        var read = new CancellationTokenSource();
        _storageRead = read;
        _ = MeasureStorage(id, generation, read);
    }

    async Task MeasureStorage(string id, int generation, CancellationTokenSource read)
    {
        try
        {
            (long size, long free) = await Task.Run(() =>
            {
                long size = WorkspaceStore.SizeOf(id, read.Token);
                read.Token.ThrowIfCancellationRequested();
                return (size, WorkspaceStore.FreeBytes());
            }, read.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !IsVisible || read.IsCancellationRequested
                    || generation != _storageGeneration || _stored.Id != id) return;
                StorageText.Text = free < LowDiskBytes ? $"· only {WorkspaceStore.Human(free)} free" : string.Empty;
                _view = ViewOf(_stored, size);
                UpdateFramePlaceholder();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_storageRead, read)) _storageRead = null;
            read.Dispose();
        }
    }

    void CancelStorageRead()
    {
        _storageGeneration++;
        CancellationTokenSource? read = _storageRead;
        _storageRead = null;
        read?.Cancel();
    }

    void BeginSetupCheck(bool force = false)
    {
        if (!IsVisible || _disposed || _setupCheckStarted && !force) return;
        CancelSetupCheck();
        _setupCheckStarted = true;
        var check = new CancellationTokenSource();
        _setupCheck = check;
        _ = CheckSetup(check);
    }

    async Task CheckSetup(CancellationTokenSource check)
    {
        try
        {
            await WorkspaceAgentSetup.VerifyExistingAsync(check.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_setupCheck, check)) _setupCheck = null;
            check.Dispose();
        }
    }

    void CancelSetupCheck()
    {
        CancellationTokenSource? check = _setupCheck;
        _setupCheck = null;
        if (check is null) return;
        _setupCheckStarted = false;
        check.Cancel();
    }

    void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_live is not null) { StopComputer(); return; }

        try
        {
            _photographed = false;
            // One way in, whoever is asking. The clock that wakes a parked mission with no panel on
            // screen calls the same thing, so there is no second way to start a workspace to get
            // wrong - and asking twice hands back the one already running rather than laying a
            // second desktop over the first.
            Adopt(WorkspaceRuntime.Start(_stored));
        }
        catch (InvalidOperationException ex)
        {
            StateDetailText.Text = ex.Message;
            return;
        }

        Store(_stored with { LastUsed = DateTimeOffset.Now });
        UpdateLiveChrome();
        ShowStorage();
    }

    // One button, two settings, no paragraph explaining them. Light keeps the workspace out of the
    // owner's way; Fast is for when he is not using the machine.
    void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        _power = _power == WorkspacePower.Light ? WorkspacePower.Fast : WorkspacePower.Light;
        if (_computer is not null) _computer.Power = _power;
        Store(_stored with { Power = _power });
        ShowPower();
    }

    void ShowPower()
    {
        string label = _power == WorkspacePower.Light ? "Light" : "Fast";
        string other = _power == WorkspacePower.Light ? "Fast" : "Light";
        PowerButton.Content = label;
        AutomationProperties.SetName(PowerButton, $"Workspace power: {label}. Click for {other}.");
    }

    void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SwitchButton,
            Placement = PlacementMode.Bottom
        };
        foreach (StoredWorkspace workspace in WorkspaceStore.All())
        {
            string id = workspace.Id;
            var item = new MenuItem
            {
                Header = workspace.Name,
                IsCheckable = true,
                IsChecked = id == _stored.Id
            };
            item.Click += (_, _) => Select(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());

        var opened = new MenuItem { Header = "Open its folder" };
        opened.Click += (_, _) => OpenFolder();
        menu.Items.Add(opened);

        var created = new MenuItem { Header = "New workspace" };
        created.Click += (_, _) => NewWorkspace();
        menu.Items.Add(created);

        var refreshed = new MenuItem { Header = "Fresh start" };
        refreshed.Click += (_, _) => FreshStart();
        menu.Items.Add(refreshed);

        var deleted = new MenuItem { Header = "Delete this workspace" };
        deleted.Click += (_, _) => DeleteWorkspace();
        menu.Items.Add(deleted);

        menu.IsOpen = true;
    }

    void NewWorkspace()
    {
        StoredWorkspace workspace = WorkspaceStore.Create($"Workspace {WorkspaceStore.All().Count + 1}");
        ModuleEntry.Selected = workspace.Id;
        SetWorkspace(workspace);
        // The name is the first thing anyone wants to change, so it is already selected.
        WorkspaceNameText.Focus();
        WorkspaceNameText.SelectAll();
    }

    /// <summary>
    /// The same workspace, a clean machine. Stops the computer, throws away what the programs and
    /// the last mission left behind, and keeps the workspace's own files - which is what handing it
    /// a different job needs, and what neither Stop nor Delete does.
    /// </summary>
    void FreshStart()
    {
        // Nothing the owner or the agent wrote is removed, so this does not ask - except of a
        // mission that is still working, which is the one thing here that cannot be picked back up.
        if (_agent?.State == MissionState.Working && MessageBox.Show(
                $"{_stored.Name} has a mission running. A fresh start ends it and clears the "
                    + "conversation. Its files are kept.",
                "Fresh start",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (_computer is not null) StopComputer();
        _lastFrame = null;
        LiveScreen.Source = null;
        bool clean = WorkspaceStore.FreshStart(_stored.Id, out StoredWorkspace? fresh);
        if (fresh is null)
        {
            StateDetailText.Text = "This workspace is not on disk any more.";
            return;
        }

        // Reopening on the cleared record is what puts the panel itself back: no transcript, no
        // mission line, no last frame, and nothing adopted.
        SetWorkspace(fresh);
        StateDetailText.Text = clean
            ? "Fresh start - app data, browser profile, temporary files and the last mission cleared. Files kept."
            : "Fresh start - Windows still held some files. Stop the computer and try again.";
    }

    void DeleteWorkspace()
    {
        // Deleting a workspace deletes every file an agent produced in it, and nothing here can put
        // them back, so this is the one place the module always asks. A fresh start only asks when
        // a mission is running, because it takes no file with it.
        if (MessageBox.Show(
                $"Delete {_stored.Name} and everything in it?",
                "Delete workspace",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (_computer is not null) StopComputer();
        WorkspaceAccessStore.Write(_stored.Id, new WorkspaceAccessPolicy());
        WorkspaceAccessStore.Withdraw(_stored.Id);
        _lastFrame = null;
        LiveScreen.Source = null;
        if (!WorkspaceStore.Delete(_stored.Id))
        {
            StateDetailText.Text = "Windows is still holding files in this workspace. Try again.";
            return;
        }

        IReadOnlyList<StoredWorkspace> left = WorkspaceStore.All();
        StoredWorkspace next = left.Count > 0 ? left[^1] : WorkspaceStore.Create("Workspace");
        ModuleEntry.Selected = next.Id;
        SetWorkspace(next);
    }

    // Windows cannot drag between desktops, and does not have to: this drop happens on the owner's
    // own desktop, into a HiveMind window, and the files are copied in from there.
    void ScreenSurface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void ScreenSurface_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        e.Handled = true;
        int landed = WorkspaceStore.Accept(_stored.Id, dropped);
        StateDetailText.Text = landed switch
        {
            0 => "Nothing could be copied into this workspace.",
            1 => "1 file copied into this workspace's inbox folder.",
            _ => $"{landed} files copied into this workspace's inbox folder."
        };
        ShowStorage();
    }

    /// <summary>Opens the workspace's folder on the owner's own desktop, so files can come back out.</summary>
    void OpenFolder()
    {
        string folder = WorkspaceStore.FolderOf(_stored.Id);
        if (!Directory.Exists(folder)) return;
        using var explorer = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    void WorkspaceName_LostFocus(object sender, RoutedEventArgs e) => CommitName();

    void WorkspaceName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;
        if (e.Key == Key.Escape) WorkspaceNameText.Text = _stored.Name;
        else CommitName();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void CommitName()
    {
        string name = WorkspaceNameText.Text.Trim();
        if (name.Length == 0) { WorkspaceNameText.Text = _stored.Name; return; }
        if (name == _stored.Name) return;
        // Only the display name changes. The id, the desktop and the folder never move, because
        // renaming a folder a low integrity process is running out of would break it.
        Store(_stored with { Name = name });
        _view = ViewOf(_stored);
        UpdateFramePlaceholder();
        _fullWindow?.Retitle(_stored.Name);
        WorkspaceChanged?.Invoke();
    }

    /// <summary>
    /// Saves the panel's half of the record. Through the store's read-modify-write, because the
    /// runtime writes the conversation id and the wake time to the same file and this panel's copy
    /// can be older than they are - saving it whole could forget a park that had just been made.
    /// </summary>
    void Store(StoredWorkspace workspace)
    {
        _stored = WorkspaceStore.Update(workspace.Id, current => workspace with
        {
            Session = current.Session,
            WakeAt = current.WakeAt,
            WakeNote = current.WakeNote,
            SessionReadUntrustedContent = current.SessionReadUntrustedContent,
            Mission = current.Mission,
            Outcome = current.Outcome,
            MissionAt = current.MissionAt,
            Announced = current.Announced,
            // The runtime banks a finished run's tokens on this record while the panel is holding
            // its own copy. Saving the panel's copy whole would hand back the total from before
            // that run, so the counter would go backwards every time a setting was saved.
            UsageTokensTotal = current.UsageTokensTotal,
            UsageUsdTotal = current.UsageUsdTotal,
            UsageRuns = current.UsageRuns,
        }) ?? workspace;
        _live?.Configure(_stored);
    }

    void StopComputer()
    {
        SaveLastFrame();
        WorkspaceRuntime? runtime = _live;
        Leave();
        runtime?.Dispose();
        Store(_stored with { LastUsed = DateTimeOffset.Now });
        UpdateLiveChrome();
        ShowStorage();
    }

    /// <summary>Keeps the last screen a stopped workspace had, so it is recognisable when reopened.</summary>
    void SaveLastFrame()
    {
        if (LiveScreen.Source is not BitmapSource frame) return;
        try
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(frame));
            using var file = File.Create(WorkspaceStore.LastFrameOf(_stored.Id));
            png.Save(file);
            _lastFrame = frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static BitmapSource? LoadLastFrame(string id)
    {
        string path = WorkspaceStore.LastFrameOf(id);
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // so the file is not left open
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Two frames a second, taken off this thread. A capture is bounded now, but even a bounded one
    /// is up to two seconds, and the UI thread is the one thread in HiveMind that must never spend
    /// two seconds anywhere. One capture is in flight at a time; the desktop refuses a second.
    /// </summary>
    void DrawFrame(object? sender, EventArgs e)
    {
        AgentDesktop? computer = _computer;
        if (computer is null || _drawing) return;
        object? attachment = _runtimeAttachment;
        ShowCommands();
        WorkspaceControl? plane = _plane;
        _drawing = true;
        Task.Run<(BitmapSource? Frame, bool Gone)>(() =>
        {
            try
            {
                // Through the control plane, so the picture and the "is it answering" question have
                // one answer for the owner and the agent.
                return (plane is not null ? plane.Frame() : computer.CaptureScreen(ScreenWidth, ScreenHeight), false);
            }
            catch (ObjectDisposedException) { return (null, true); }
        }).ContinueWith(taken => Dispatcher.BeginInvoke(() =>
        {
            _drawing = false;
            // A capture belongs to the view that requested it. Switching away and back is a new
            // attachment too: its queued stale image must not replace that view's current frame.
            if (_disposed || !ReferenceEquals(_runtimeAttachment, attachment)
                || !ReferenceEquals(_computer, computer) || !taken.IsCompletedSuccessfully) return;
            if (taken.Result.Gone) { StopComputer(); return; }
            ShowScreenState();
            if (taken.Result.Frame is not BitmapSource frame) return;
            LiveScreen.Source = frame;
            // The first one is the moment the workspace stops looking like it is hanging.
            if (_photographed) return;
            _photographed = true;
            UpdateLiveChrome();
        }), TaskScheduler.Default);
    }

    bool _drawing;

    void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (_computer is null || sender is not Button { Tag: string exe }) return;
        if (_computer.Launch(exe) == 0)
            StateDetailText.Text = $"Windows would not start {exe} in this workspace.";
    }

    void ControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plane is null) return;
        // The button is deliberate, so its control stays until the owner gives it back.
        _ownerByClick = false;
        _ownerIdle?.Stop();
        // Taking control is instant and never waits for an agent action to finish: the lease number
        // changes here, and input the agent had already queued is dropped on the desktop pump.
        if (OwnerIsDriving) _plane.Release(); else _plane.OwnerTakes();
        if (OwnerIsDriving) LiveScreen.Focus();
        UpdateLiveChrome();
    }

    // The owner's real pointer never moves onto the agent's desktop. The click is translated into
    // the agent's screen coordinates and delivered as a window message, exactly as the agent's own
    // clicks are - so there is one input path to trust instead of two.
    void LiveScreen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_computer is null || _plane is null) return;
        // Clicking the screen is taking over. The agent pauses on the spot and carries on once the
        // owner has left the screen alone for a while.
        if (!OwnerIsDriving) { _plane.OwnerTakes(); _ownerByClick = true; UpdateLiveChrome(); }
        OwnerActive();
        LiveScreen.Focus();
        if (!TryMapToScreen(e.GetPosition(LiveScreen), out int x, out int y)) return;
        _computer.Click(x, y, e.ChangedButton == MouseButton.Right);
        e.Handled = true;
    }

    void LiveScreen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!OwnerIsDriving || _computer is null) return;
        OwnerActive();
        if (!TryMapToScreen(e.GetPosition(LiveScreen), out int x, out int y)) return;
        _computer.Scroll(x, y, e.Delta);
        e.Handled = true;
    }

    void LiveScreen_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (!OwnerIsDriving || _computer is null || string.IsNullOrEmpty(e.Text)) return;
        OwnerActive();
        _computer.TypeText(e.Text);
        e.Handled = true;
    }

    void LiveScreen_KeyDown(object sender, KeyEventArgs e)
    {
        if (!OwnerIsDriving || _computer is null) return;
        // TextInput carries the printable characters; these are the ones it never reports.
        if (e.Key is not (Key.Enter or Key.Tab or Key.Back or Key.Delete or Key.Escape
            or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        OwnerActive();
        _computer.SendKey(KeyInterop.VirtualKeyFromKey(e.Key));
        e.Handled = true;
    }

    /// <summary>Keeps click-taken control while the owner is still using the screen.</summary>
    void OwnerActive()
    {
        if (!_ownerByClick) return;
        if (_ownerIdle is null)
        {
            _ownerIdle = new DispatcherTimer(DispatcherPriority.Background) { Interval = OwnerIdleHandback };
            _ownerIdle.Tick += (_, _) => GiveBackClickControl();
        }
        _ownerIdle.Stop();
        _ownerIdle.Start();
    }

    /// <summary>Hands click-taken control back to whoever was working. Button-taken control stays.</summary>
    void GiveBackClickControl()
    {
        _ownerIdle?.Stop();
        if (!_ownerByClick) return;
        _ownerByClick = false;
        if (OwnerIsDriving) { _plane?.Release(); UpdateLiveChrome(); }
    }

    bool TryMapToScreen(Point point, out int x, out int y)
    {
        x = y = 0;
        if (LiveScreen.ActualWidth <= 0 || LiveScreen.ActualHeight <= 0) return false;

        // Stretch="Uniform" letterboxes the frame, so undo the scale and the centring offset.
        double scale = Math.Min(LiveScreen.ActualWidth / ScreenWidth, LiveScreen.ActualHeight / ScreenHeight);
        if (scale <= 0) return false;
        double left = (LiveScreen.ActualWidth - ScreenWidth * scale) / 2;
        double top = (LiveScreen.ActualHeight - ScreenHeight * scale) / 2;

        x = (int)Math.Round((point.X - left) / scale);
        y = (int)Math.Round((point.Y - top) / scale);
        return x >= 0 && y >= 0 && x < ScreenWidth && y < ScreenHeight;
    }

    static int ScreenWidth => AgentDesktop.ScreenWidth;

    static int ScreenHeight => AgentDesktop.ScreenHeight;

    void ChatToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _chatCollapsed = !_chatCollapsed;
        ChatColumn.Width = new GridLength(_chatCollapsed ? 0 : 286);
        ChatPanel.Visibility = _chatCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ChatToggleGlyph.Data = Geometry.Parse(
            _chatCollapsed ? "M6,5 L13,12 L6,19" : "M13,5 L6,12 L13,19");
        ChatToggleButton.ToolTip = _chatCollapsed ? "Show boss chat" : "Collapse boss chat";
        AutomationProperties.SetName(
            ChatToggleButton,
            _chatCollapsed ? "Show boss chat" : "Collapse boss chat");
    }

    // The workspace moves into the window; it is not copied into a second panel. A copy meant two
    // panels on one workspace, each with its own computer, its own lease and its own agent - and
    // then Take control in one window did not stop an agent driven from the other, which is the one
    // promise this product cannot break. Measured in measurements/panel-probe.cs before the change.
    void FullWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fullWindow is { IsLoaded: true }) { _fullWindow.Activate(); return; }
        // ponytail: the shell puts a module panel in a ContentControl. Hosted anywhere else, stay put.
        if (Parent is not ContentControl slot) return;

        slot.Content = null;
        FullWindowButton.Visibility = Visibility.Collapsed;
        _fullWindow = new WorkspaceFullWindow(this, slot, _stored.Name);
        _fullWindow.Closed += (_, _) => _fullWindow = null;
        _fullWindow.Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WorkspaceAgentSetup.Changed -= SetupChanged;
        Loaded -= Panel_Loaded;
        IsVisibleChanged -= Panel_IsVisibleChanged;
        // Only this page's view of the corner view goes. The window itself keeps running: closing
        // the page is not a reason to stop showing a mission that is still working.
        StopPeek();
        CancelStorageRead();
        CancelSetupCheck();
        // The standalone shell owns all workspace lifetimes. Closing or replacing one view
        // leaves its desktop running; an explicit Stop or app shutdown ends the runtime.
        SaveLastFrame();
        Leave();
        WorkspaceFullWindow? window = _fullWindow;
        _fullWindow = null;
        if (window is { IsLoaded: true }) window.Close();
    }
}

sealed class WorkspaceFullWindow : Window
{
    readonly AgentWorkspacesPanel _panel;
    readonly ContentControl _slot;
    readonly Thickness _margin;

    public WorkspaceFullWindow(AgentWorkspacesPanel panel, ContentControl slot, string name)
    {
        Title = $"{name} - Deskweave";
        MinWidth = 760;
        MinHeight = 520;
        Width = 1180;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        SetResourceReference(BackgroundProperty, "ShellSurfaceBrush");

        if (Application.Current?.MainWindow is Window owner && !ReferenceEquals(owner, this))
            Owner = owner;

        _panel = panel;
        _slot = slot;
        _margin = panel.Margin;
        panel.Margin = new Thickness(26, 22, 26, 24);
        Content = panel;
    }

    public void Retitle(string name) => Title = $"{name} - Deskweave";

    protected override void OnClosed(EventArgs e)
    {
        Content = null;
        _panel.Margin = _margin;
        // Back into the card the shell took it from, unless the shell has opened another app there
        // or the panel is on its way out. A card holding a disposed panel is worse than an empty one.
        if (_slot.Content is null && !_panel.IsDisposed)
        {
            _slot.Content = _panel;
            _panel.ShowFullWindowButton();
        }
        base.OnClosed(e);
    }
}
