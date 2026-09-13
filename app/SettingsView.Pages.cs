using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Resources;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

namespace Deskweave;

/// <summary>Every settings page (MVP_SPEC Surfaces 4). Control and Browser &amp; accounts match the
/// references word for word; the rest follow the same look, since no reference draws them.</summary>
public partial class SettingsView
{
    FrameworkElement Build(string id) => id switch
    {
        "general" => General(),
        "agents" => Agents(),
        "control" => Control(),
        "browser" => Browser(),
        "corner" => Corner(),
        "alerts" => Alerts(),
        "history" => History(),
        "perf" => Performance(),
        "privacy" => Privacy(),
        "about" => About(),
        _ => General(),
    };

    FrameworkElement General()
    {
        var startup = Toggle("Start with Windows", s => s.StartWithWindows, (s, v) => s with { StartWithWindows = v });
        var startupError = Styled("Couldn't update Windows startup.", "RowError");
        startupError.Visibility = Visibility.Collapsed;
        RoutedEventHandler sync = (_, _) =>
        {
            if (_following) return;
            startupError.Visibility = SettingsActions.SyncStartup(AppSettingsStore.Current) ? Visibility.Collapsed : Visibility.Visible;
        };
        startup.Checked += sync;
        startup.Unchecked += sync;
        var startupText = new StackPanel();
        startupText.Children.Add(RowText("Start with Windows"));
        startupText.Children.Add(startupError);

        var theme = Dropdown("Theme", [
            new Choice(ThemeChoice.FollowWindows, "Follow Windows"),
            new Choice(ThemeChoice.Light, "Light"),
            new Choice(ThemeChoice.Dark, "Dark"),
        ], s => s.Theme, (s, v) => s with { Theme = v });

        var close = Dropdown("Close button", [
            new Choice(CloseChoice.KeepRunning, "Keep running in the tray"),
            new Choice(CloseChoice.Quit, "Quit"),
        ], s => s.CloseButton, (s, v) => s with { CloseButton = v });

        return Section(null, Group(
            Row(startupText, startup),
            Row(RowText("Theme"), theme),
            Row(RowText("Close button"), close)));
    }

    FrameworkElement Agents()
    {
        var claude = AgentRow(WorkspaceConnections.AgentApp.ClaudeCode, "Claude Code", "C", Color.FromRgb(0x8A, 0x5A, 0x44));
        var codex = AgentRow(WorkspaceConnections.AgentApp.Codex, "Codex", "X", Color.FromRgb(0x2B, 0x2F, 0x37));
        var another = AnotherAgentRow();
        var remind = RemindAgentsRow();

        var placement = Dropdown("Where agents go", [
            new Choice(AgentPlacement.OnePerProject, "One per project"),
            new Choice(AgentPlacement.OneShared, "One shared workspace"),
            new Choice(AgentPlacement.AskMe, "Ask me"),
        ], s => s.AgentsGo, (s, v) => s with { AgentsGo = v });

        var running = new List<Choice> { new(0, "Auto") };
        for (int count = 2; count <= 10; count++) running.Add(new Choice(count, count.ToString()));
        var runningAtOnce = Dropdown("Running at once", running, s => s.RunningAtOnce, (s, v) => s with { RunningAtOnce = v });

        var sleep = Dropdown("Sleep when quiet", [
            new Choice(5, "5 minutes"), new Choice(10, "10 minutes"), new Choice(30, "30 minutes"), new Choice(0, "Never"),
        ], s => s.SleepMinutes, (s, v) => s with { SleepMinutes = v });

        return new StackPanel
        {
            Children =
            {
                Section(null, Group(claude, codex, another, remind)),
                Section("Workspaces", Group(
                    Row(RowText("Where agents go"), placement),
                    Row(RowText("Running at once"), runningAtOnce),
                    Row(RowText("Sleep when quiet"), sleep))),
            },
        };
    }

    FrameworkElement AgentRow(WorkspaceConnections.AgentApp app, string name, string letter, Color tileColor)
    {
        var tile = new ContentControl { Content = letter, Background = new SolidColorBrush(tileColor) };
        tile.SetResourceReference(StyleProperty, "LetterTile");
        var status = Styled("", "RowHint");
        var error = Styled("", "RowError");
        error.Visibility = Visibility.Collapsed;
        error.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel();
        text.Children.Add(RowText(name));
        text.Children.Add(status);
        text.Children.Add(error);
        var toggle = new CheckBox();
        toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(toggle, name + " connected");

        bool settingProgrammatically = false;
        void Refresh()
        {
            AgentState state = SettingsActions.ReadAgent(app);
            status.Text = state switch
            {
                AgentState.NotInstalled => "Not installed",
                AgentState.Found => "Found on this PC",
                _ => "Connected",
            };
            settingProgrammatically = true;
            toggle.IsChecked = state == AgentState.Connected;
            settingProgrammatically = false;
            toggle.IsEnabled = state != AgentState.NotInstalled;
        }
        async void Changed(object sender, RoutedEventArgs e)
        {
            if (settingProgrammatically) return;
            bool on = toggle.IsChecked == true;
            toggle.IsEnabled = false;
            error.Visibility = Visibility.Collapsed;
            string? failure = await SettingsActions.Connect(app, on);
            if (failure is not null) { error.Text = failure; error.Visibility = Visibility.Visible; }
            Refresh();
        }
        toggle.Checked += Changed;
        toggle.Unchecked += Changed;
        Refresh();
        return Row(text, toggle, tile);
    }

    FrameworkElement AnotherAgentRow()
    {
        var button = new Button { Content = "Copy setup" };
        button.SetResourceReference(StyleProperty, "DeskButton");
        button.Click += (_, _) => SettingsActions.CopyText(SettingsActions.SetupText());
        return Row(RowText("Another agent"), button);
    }

    FrameworkElement RemindAgentsRow()
    {
        var toggle = Toggle("Remind agents to test in Deskweave", s => s.RemindAgents, (s, v) => s with { RemindAgents = v });
        var row = Row(RowText("Remind agents to test in Deskweave", "Adds one line to your agents' instructions"), toggle);
        row.Visibility = SettingsFeatures.RemindAgents ? Visibility.Visible : Visibility.Collapsed;
        return row;
    }

    FrameworkElement Control()
    {
        var choices = RadioRows("Control mode", "ControlMode",
        [
            (ControlMode.WorkAlongside, "Work alongside", "You and the agent both act. It re-checks the screen after you've touched it.", false),
            (ControlMode.TakeTurns, "Take turns", "The agent waits while you're using it, then carries on where it was.", true),
            (ControlMode.FullStop, "Full stop", "The agent is frozen until you hand it back.", false),
        ], s => s.Control, (s, v) => s with { Control = v });

        var carryOn = Dropdown("Carry on after you stop for", [
            new Choice(10, "10 seconds"), new Choice(20, "20 seconds"), new Choice(30, "30 seconds"), new Choice(60, "60 seconds"),
        ], s => s.CarryOnSeconds, (s, v) => s with { CarryOnSeconds = v });
        var carryOnRow = Row(RowText("Carry on after you stop for"), carryOn);

        var desktop = Dropdown("Agents open things on your desktop", [
            new Choice(DesktopOpen.AskFirst, "Ask me first"), new Choice(DesktopOpen.Always, "Always"), new Choice(DesktopOpen.Never, "Never"),
        ], s => s.DesktopRequests, (s, v) => s with { DesktopRequests = v });
        var desktopRow = Row(RowText("Agents open things on your desktop", "A file or a link the agent wants you to see"), desktop);

        var pause = ShortcutRow("Pause every agent", "Pause every agent", s => s.PauseHotkey, (s, v) => s with { PauseHotkey = v });
        var corner = ShortcutRow("Show the corner window", "Show the corner window", s => s.CornerHotkey, (s, v) => s with { CornerHotkey = v });
        pause.Control.Accepts = keys => !string.Equals(keys, corner.Control.Keys, StringComparison.OrdinalIgnoreCase);
        corner.Control.Accepts = keys => !string.Equals(keys, pause.Control.Keys, StringComparison.OrdinalIgnoreCase);

        return new StackPanel
        {
            Children =
            {
                Section("When you use a workspace", Group([.. choices, carryOnRow])),
                Section("Your desktop", Group(desktopRow)),
                Section("Shortcuts", Group(pause.Row, corner.Row)),
            },
        };
    }

    FrameworkElement Browser()
    {
        var banner = new ContentControl();
        banner.SetResourceReference(StyleProperty, "Banner");
        var bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        bannerText.Inlines.Add(new Run("Local only.") { FontWeight = FontWeights.SemiBold });
        bannerText.Inlines.Add(new Run(" Sign-ins live in Deskweave's own browser, on this PC. Nothing is uploaded, and your own Chrome is never touched."));
        banner.Content = bannerText;

        var accountRows = SettingsFeatures.Accounts
            ? SettingsActions.Accounts().Select(AccountRow).Cast<UIElement>().ToArray()
            : [];
        var signedIn = Section("Signed in", Group(accountRows), AddAccountButton());
        signedIn.Visibility = SettingsFeatures.Accounts ? Visibility.Visible : Visibility.Collapsed;

        var share = Toggle("Share sign-ins across workspaces", s => s.ShareSignIns, (s, v) => s with { ShareSignIns = v });
        var shareRow = Row(RowText("Share sign-ins across workspaces", "Sign in once, every project can use it"), share);
        var browserRow = Row(RowText("Browser"), BrowserDropdown());
        var openEarly = Toggle("Open the browser early", s => s.OpenBrowserEarly, (s, v) => s with { OpenBrowserEarly = v });
        var openEarlyRow = Row(RowText("Open the browser early", "Ready the moment an agent needs it"), openEarly);

        return new StackPanel
        {
            Children =
            {
                new StackPanel { Margin = new Thickness(0, 0, 0, 22), Children = { banner } },
                signedIn,
                Section("Agent browser", Group(shareRow, browserRow, openEarlyRow)),
            },
        };
    }

    Button AddAccountButton()
    {
        var icon = new System.Windows.Shapes.Path();
        icon.SetResourceReference(StyleProperty, "Icon12");
        icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon.Plus");
        icon.Margin = new Thickness(0, 0, 6, 0);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = "Add account", VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content };
        button.SetResourceReference(StyleProperty, "DeskButton");
        AutomationProperties.SetName(button, "Add account");
        button.Click += (_, _) => SettingsActions.AddAccount?.Invoke();
        return button;
    }

    FrameworkElement AccountRow(SettingsAccount account)
    {
        var tile = new ContentControl
        {
            Content = account.Site.Length > 0 ? account.Site[..1].ToUpperInvariant() : "?",
            Background = new SolidColorBrush(account.Tile),
        };
        tile.SetResourceReference(StyleProperty, "LetterTile");
        var text = RowText(account.Site, account.Name);

        var workspaces = WorkspaceStore.All();
        var scopeChoices = new List<Choice> { new("", "All workspaces") };
        scopeChoices.AddRange(workspaces.Select(w => new Choice(w.Id, w.Name + " only")));
        var scope = new ComboBox { ItemsSource = scopeChoices };
        scope.SetResourceReference(StyleProperty, "Dropdown");
        AutomationProperties.SetName(scope, account.Site + " scope");
        string Current() => AppSettingsStore.Current.AccountScopes.GetValueOrDefault(account.Key, "");
        scope.SelectedItem = scopeChoices.FirstOrDefault(c => (string)c.Value == Current()) ?? scopeChoices[0];
        scope.SelectionChanged += (_, _) =>
        {
            if (_following || scope.SelectedItem is not Choice choice) return;
            string value = (string)choice.Value;
            Write(s =>
            {
                var scopes = s.AccountScopes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (value.Length == 0) scopes.Remove(account.Key); else scopes[account.Key] = value;
                return s with { AccountScopes = scopes };
            });
        };
        var signOut = new Button { Content = "Sign out", Margin = new Thickness(8, 0, 0, 0) };
        signOut.SetResourceReference(StyleProperty, "LinkButton");
        signOut.Click += (_, _) => SettingsActions.SignOut?.Invoke(account);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(scope);
        controls.Children.Add(signOut);
        return Row(text, controls, tile);
    }

    ComboBox BrowserDropdown()
    {
        var installed = SettingsActions.InstalledBrowsers();
        var choices = installed.Select(b => new Choice(b, b == BrowserChoice.Chrome ? "Chrome" : "Edge")).ToList();
        var box = new ComboBox { ItemsSource = choices, IsEnabled = choices.Count > 0 };
        box.SetResourceReference(StyleProperty, "Dropdown");
        AutomationProperties.SetName(box, "Browser");
        BrowserChoice Effective(AppSettings s) => choices.Any(c => (BrowserChoice)c.Value == s.Browser)
            ? s.Browser : choices.Count > 0 ? (BrowserChoice)choices[0].Value : BrowserChoice.Auto;
        void Show(AppSettings s) => box.SelectedItem = choices.FirstOrDefault(c => (BrowserChoice)c.Value == Effective(s));
        box.SelectionChanged += (_, _) =>
        {
            if (_following || box.SelectedItem is not Choice choice) return;
            Write(s => s with { Browser = (BrowserChoice)choice.Value });
        };
        _followers.Add(Show);
        if (choices.Count > 0)
            _bound.Add(new Bound("Browser", box, choices.Select(c => c.Value).ToList(),
                s => Effective(s), (s, v) => s with { Browser = (BrowserChoice)v },
                v => box.SelectedItem = choices.FirstOrDefault(c => Equals(c.Value, v)),
                () => (box.SelectedItem as Choice)?.Value, Show));
        return box;
    }

    FrameworkElement Corner()
    {
        var show = Dropdown("Show", [
            new Choice(CornerShow.ComesAndGoes, "Comes and goes"), new Choice(CornerShow.Always, "Always"), new Choice(CornerShow.Off, "Off"),
        ], s => s.CornerShow, (s, v) => s with { CornerShow = v });
        var position = Dropdown("Position", [
            new Choice(CornerPosition.BottomRight, "Bottom right"), new Choice(CornerPosition.BottomLeft, "Bottom left"),
            new Choice(CornerPosition.TopRight, "Top right"), new Choice(CornerPosition.WhereILeaveIt, "Where I leave it"),
        ], s => s.CornerPosition, (s, v) => s with { CornerPosition = v });
        var size = Dropdown("Size", [
            new Choice(CornerSize.Small, "Small"), new Choice(CornerSize.Medium, "Medium"), new Choice(CornerSize.Large, "Large"),
        ], s => s.CornerSize, (s, v) => s with { CornerSize = v });
        var fade = Dropdown("Fade out after", [
            new Choice(3, "3 seconds"), new Choice(5, "5 seconds"), new Choice(10, "10 seconds"),
        ], s => s.FadeAfterSeconds, (s, v) => s with { FadeAfterSeconds = v });
        var click = Dropdown("Clicking it", [
            new Choice(CornerClick.UseItHere, "Use it right there"), new Choice(CornerClick.OpenWorkspace, "Open the workspace"),
        ], s => s.CornerClick, (s, v) => s with { CornerClick = v });

        return Section(null, Group(
            Row(RowText("Show"), show),
            Row(RowText("Position"), position),
            Row(RowText("Size"), size),
            Row(RowText("Fade out after"), fade),
            Row(RowText("Clicking it"), click)));
    }

    FrameworkElement Alerts()
    {
        var needsYou = Toggle("When an agent needs you", s => s.NotifyNeedsYou, (s, v) => s with { NotifyNeedsYou = v });
        var testFinished = Toggle("When a test finishes", s => s.NotifyTestFinished, (s, v) => s with { NotifyTestFinished = v });
        var onlyHidden = Toggle("Only when the corner window can't show it", s => s.NotifyOnlyWhenCornerCannot, (s, v) => s with { NotifyOnlyWhenCornerCannot = v });
        var sound = Toggle("Sound", s => s.NotifySound, (s, v) => s with { NotifySound = v });
        var dnd = Toggle("Follow Windows Do not disturb", s => s.FollowDoNotDisturb, (s, v) => s with { FollowDoNotDisturb = v });

        return Section(null, Group(
            Row(RowText("When an agent needs you"), needsYou),
            Row(RowText("When a test finishes"), testFinished),
            Row(RowText("Only when the corner window can't show it", "Hidden, off, or a full-screen app"), onlyHidden),
            Row(RowText("Sound"), sound),
            Row(RowText("Follow Windows Do not disturb"), dnd)));
    }

    FrameworkElement History()
    {
        var save = Dropdown("Save screenshots", [
            new Choice(ScreenshotMode.KeySteps, "Key steps"), new Choice(ScreenshotMode.Continuous, "Continuous"), new Choice(ScreenshotMode.Off, "Off"),
        ], s => s.Screenshots, (s, v) => s with { Screenshots = v });
        var continuous = Dropdown("Continuous every", [
            new Choice(1, "1 second"), new Choice(2, "2 seconds"), new Choice(5, "5 seconds"),
        ], s => s.ContinuousSeconds, (s, v) => s with { ContinuousSeconds = v });
        _followers.Add(s => continuous.IsEnabled = s.Screenshots == ScreenshotMode.Continuous);
        var keep = Dropdown("Keep history for", [
            new Choice(1, "1 day"), new Choice(7, "7 days"), new Choice(30, "30 days"), new Choice(0, "Forever"),
        ], s => s.KeepHistoryDays, (s, v) => s with { KeepHistoryDays = v });

        var spaceUsed = Styled(FormatBytes(SettingsActions.HistoryBytes()), "RowHint");
        var spaceLabel = new StackPanel();
        spaceLabel.Children.Add(RowText("Space used"));
        spaceLabel.Children.Add(spaceUsed);
        var clear = Confirm("Clear", "Clears the screenshots and step logs kept for every workspace.", () =>
        {
            SettingsActions.ClearHistory();
            spaceUsed.Text = FormatBytes(SettingsActions.HistoryBytes());
        }, danger: false);

        var openLogs = new Button { Content = "Open logs folder" };
        openLogs.SetResourceReference(StyleProperty, "RowButton");
        AutomationProperties.SetName(openLogs, "Open logs folder");
        openLogs.Click += (_, _) => SettingsActions.OpenFolder(ProductContext.Local("logs"));

        return Section(null, Group(
            Row(RowText("Save screenshots"), save),
            Row(RowText("Continuous every"), continuous),
            Row(RowText("Keep history for"), keep),
            Row(spaceLabel, clear),
            openLogs));
    }

    FrameworkElement Performance()
    {
        var speed = Dropdown("New workspace speed", [
            new Choice(WorkspacePower.Fast, "Fast"), new Choice(WorkspacePower.Light, "Light"),
        ], s => s.NewWorkspaceSpeed, (s, v) => s with { NewWorkspaceSpeed = v });
        var smoothness = Dropdown("Preview smoothness", [
            new Choice(PreviewSmoothness.Balanced, "Balanced"), new Choice(PreviewSmoothness.Smooth, "Smooth"), new Choice(PreviewSmoothness.BatterySaver, "Battery saver"),
        ], s => s.Smoothness, (s, v) => s with { Smoothness = v });
        var battery = Toggle("Pause previews on battery", s => s.PausePreviewsOnBattery, (s, v) => s with { PausePreviewsOnBattery = v });

        return Section(null, Group(
            Row(RowText("New workspace speed", "Light keeps a hard limit on what a workspace can use"), speed),
            Row(RowText("Preview smoothness"), smoothness),
            Row(RowText("Pause previews on battery"), battery)));
    }

    FrameworkElement Privacy()
    {
        var pauseWeb = Toggle("Pause commands after reading a web page", s => s.PauseAfterWebPage, (s, v) => s with { PauseAfterWebPage = v });
        var restrictions = Dropdown("Workspace restrictions", [
            new Choice(WorkspaceMode.Free, "Free"), new Choice(WorkspaceMode.Secure, "Secure"),
        ], s => s.Restrictions, (s, v) => s with { Restrictions = v });
        var deleteRow = Row(RowText("Delete all Deskweave data"),
            Confirm("Delete", "Deletes " + string.Join(" and ", SettingsActions.DataFolders) + ". This can't be undone.",
                () => SettingsActions.DeleteAllData(SettingsActions.DataFolders), danger: true));

        return new StackPanel
        {
            Children =
            {
                Section(null, Group(
                    Row(RowText("Pause commands after reading a web page", "Web pages can carry instructions aimed at agents"), pauseWeb),
                    Row(RowText("Workspace restrictions"), restrictions))),
                Section(null, Group(deleteRow)),
            },
        };
    }

    FrameworkElement About()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var versionRow = Row(RowText("Version"), Styled(version, "RowHint"));

        var licenseBody = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 0, 14, 12) };
        licenseBody.SetResourceReference(StyleProperty, "RowHint");
        var licenses = new Button { Content = "Licenses" };
        licenses.SetResourceReference(StyleProperty, "RowButton");
        AutomationProperties.SetName(licenses, "Licenses");
        licenses.Click += (_, _) =>
        {
            if (licenseBody.Visibility == Visibility.Visible) { licenseBody.Visibility = Visibility.Collapsed; return; }
            if (licenseBody.Text.Length == 0) licenseBody.Text = ReadNotices();
            licenseBody.Visibility = Visibility.Visible;
        };
        var licenseStack = new StackPanel();
        licenseStack.Children.Add(licenses);
        licenseStack.Children.Add(licenseBody);

        return Section(null, Group(
            versionRow,
            licenseStack,
            DataFolderRow("Local data", ProductContext.LocalRoot),
            DataFolderRow("Roaming data", ProductContext.RoamingRoot)));
    }

    FrameworkElement DataFolderRow(string label, string path)
    {
        var button = new Button { Content = "Open" };
        button.SetResourceReference(StyleProperty, "DeskButton");
        button.Click += (_, _) => SettingsActions.OpenFolder(path);
        return Row(RowText(label, path), button);
    }

    static string ReadNotices()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Deskweave;component/Assets/ThirdPartyNotices.txt");
            StreamResourceInfo? info = Application.GetResourceStream(uri);
            if (info is null) return "Third-party notices are unavailable.";
            using Stream stream = info.Stream;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return "Third-party notices are unavailable."; }
    }
}
