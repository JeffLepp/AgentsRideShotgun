using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Resources;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

namespace Deskweave;

/// <summary>Every settings page (MVP_SPEC Surfaces 4, cut to four categories 2026-09-13). Agents,
/// Accounts and the Storage group of History &amp; privacy match their references word for word; General
/// has no reference and follows the same look.</summary>
public partial class SettingsView
{
    FrameworkElement Build(string id) => id switch
    {
        "general" => General(),
        "agents" => Agents(),
        "accounts" => Accounts(),
        "history" => HistoryPrivacy(),
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

        var agentScreen = Dropdown("Agent screens", [
            new Choice(AgentScreenLook.Full, "Full desktop"),
            new Choice(AgentScreenLook.Simple, "Simple"),
        ], s => s.AgentScreen, (s, v) => s with { AgentScreen = v });

        // On: CornerShow.ComesAndGoes (it comes and goes on its own). Off: CornerShow.Off (it never
        // appears on its own; the tray still shows it, per ModuleEntry.ShowCornerRequested).
        // Turning it off asks first (CornerQuestion).
        bool cornerConfirmed = false;
        CheckBox? cornerShow = null;
        var cornerAsk = CornerQuestion(() =>
        {
            cornerConfirmed = true;
            try { cornerShow!.IsChecked = false; } finally { cornerConfirmed = false; }
        });
        cornerShow = Toggle("Show the corner window", s => s.CornerShow != CornerShow.Off,
            (s, v) => s with { CornerShow = v ? CornerShow.ComesAndGoes : CornerShow.Off },
            on =>
            {
                if (on || cornerConfirmed) { cornerAsk.Visibility = Visibility.Collapsed; return true; }
                cornerAsk.Visibility = Visibility.Visible;
                return false;
            });
        // Turned off elsewhere (the card's tile, the other Settings), there is nothing left to ask.
        _followers.Add(s => { if (s.CornerShow == CornerShow.Off) cornerAsk.Visibility = Visibility.Collapsed; });
        var cornerText = new StackPanel();
        cornerText.Children.Add(RowText("Show the corner window"));
        cornerText.Children.Add(cornerAsk);

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var versionRow = Row(RowText("Version"), Styled(version, "RowHint"));

        var licenseBody = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 0, 14, 12) };
        licenseBody.SetResourceReference(StyleProperty, "RowHint");
        var licenses = new Button { Content = RowAction("Licenses", "Icon.Down") };
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

        var openData = new Button { Content = RowAction("Open Deskweave data", "Icon.Folder") };
        openData.SetResourceReference(StyleProperty, "RowButton");
        AutomationProperties.SetName(openData, "Open Deskweave data");
        openData.Click += (_, _) => SettingsActions.OpenFolder(ProductContext.LocalRoot);

        return new StackPanel
        {
            Children =
            {
                Section(null, Group(
                    Row(startupText, startup),
                    Row(RowText("Theme"), theme),
                    Row(RowText("Agent screens", "How the agent's screen looks behind its windows"), agentScreen),
                    Row(cornerText, cornerShow))),
                Section(null, Group(versionRow, licenseStack, openData)),
            },
        };
    }

    /// <summary>
    /// The question before the corner window goes off, the same from General's switch and the card's
    /// tile: a tester switched it off without knowing what it was. It starts hidden; Turn off runs
    /// <paramref name="turnOff"/>, and either answer hides it again.
    /// </summary>
    static StackPanel CornerQuestion(Action turnOff)
    {
        var question = new TextBlock
        {
            Text = "The corner window is where you watch your agents work. Turn it off? You can still open it from the tray.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        };
        question.SetResourceReference(StyleProperty, "RowHint");
        var off = new Button { Content = "Turn off", Margin = new Thickness(0, 0, 8, 0) };
        off.SetResourceReference(StyleProperty, "DeskButton");
        var keep = new Button { Content = "Keep it" };
        keep.SetResourceReference(StyleProperty, "DeskButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(off);
        buttons.Children.Add(keep);
        var ask = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        ask.Children.Add(question);
        ask.Children.Add(buttons);
        off.Click += (_, _) => { ask.Visibility = Visibility.Collapsed; turnOff(); };
        keep.Click += (_, _) => ask.Visibility = Visibility.Collapsed;
        return ask;
    }

    /// <summary>
    /// A row that does something rather than holds a setting: its label, and on the right a small
    /// glyph saying what a click does - opens below, or opens a folder - so it does not read as a
    /// plain label or as a way into another page.
    /// </summary>
    static FrameworkElement RowAction(string label, string icon)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        // The app's one stroke family (Icons.xaml), at the 13 DIP size the rows' other icons use.
        var mark = new System.Windows.Shapes.Path { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        mark.SetResourceReference(StyleProperty, "Icon13");
        mark.SetResourceReference(System.Windows.Shapes.Path.DataProperty, icon);
        mark.SetResourceReference(TextElement.ForegroundProperty, "MutedInkBrush");
        Grid.SetColumn(mark, 1);
        grid.Children.Add(text);
        grid.Children.Add(mark);
        return grid;
    }

    FrameworkElement Agents()
    {
        (WorkspaceConnections.AgentApp App, string Name, string Letter, Color Tile)[] candidates =
        [
            (WorkspaceConnections.AgentApp.ClaudeCode, "Claude Code", "C", Color.FromRgb(0x8A, 0x5A, 0x44)),
            (WorkspaceConnections.AgentApp.Codex, "Codex", "X", Color.FromRgb(0x2B, 0x2F, 0x37)),
        ];
        UIElement[] installedRows = candidates
            .Where(a => SettingsActions.ReadAgent(a.App) != AgentState.NotInstalled)
            .Select(a => (UIElement)AgentRow(a.App, a.Name, a.Letter, a.Tile))
            .ToArray();
        Border onThisPc = installedRows.Length > 0
            ? Group(installedRows)
            : Group(Row(RowText(FirstRunWindow.NoAgentLine)));

        var pause = ShortcutRow("Pause every agent", "Pause every agent", s => s.PauseHotkey,
            (s, v) => s with { PauseHotkey = v }, () => ModuleEntry.PauseShortcutTaken);

        var onThisPcSection = Section("On this PC", onThisPc);
        onThisPcSection.Margin = new Thickness(0, 0, 0, 21);
        var anotherAppSection = Section("Another app", Group(AnotherAgentRow()));
        anotherAppSection.Margin = new Thickness(0, 0, 0, 21);
        return new StackPanel
        {
            Children =
            {
                onThisPcSection,
                anotherAppSection,
                Section("Shortcut", Group(pause.Row)),
            },
        };
    }

    FrameworkElement AgentRow(WorkspaceConnections.AgentApp app, string name, string letter, Color tileColor)
    {
        var tile = new ContentControl { Content = letter, Background = new SolidColorBrush(tileColor) };
        tile.SetResourceReference(StyleProperty, "LetterTile");
        var status = Styled("", "RowHint");
        status.TextWrapping = TextWrapping.Wrap;
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
        bool connecting = false;
        string? requestFailure = null;
        void Refresh()
        {
            AgentState state = SettingsActions.ReadAgent(app);
            var profiles = SettingsActions.ReadProfileCounts(app);
            status.Text = state switch
            {
                AgentState.NotInstalled => "Not installed",
                _ when profiles.Total > 1 && profiles.Connected == profiles.Total => $"Connected \u00b7 {profiles.Total} profiles",
                _ when profiles.Total > 1 => $"{profiles.Connected} of {profiles.Total} profiles connected",
                AgentState.Found => "Found on this PC",
                _ => "Connected",
            };
            toggle.ToolTip = profiles.Total > 1
                ? $"Connects Deskweave to all {profiles.Total} detected configuration profiles." : null;
            string? failure = requestFailure ?? SettingsActions.ReadConnectionFailure(app);
            error.Text = failure ?? "";
            error.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
            // On while any profile is connected, or while a failed connection is still wanted and
            // being repaired. Off then always has something to do: disconnect what works, and stop
            // the repair. Drawing a partial set as off left the working profile no way out.
            settingProgrammatically = true;
            toggle.IsChecked = state == AgentState.Connected || profiles.Connected > 0
                || failure is not null && !WorkspaceConnections.TurnedOff(app);
            settingProgrammatically = false;
            toggle.IsEnabled = state != AgentState.NotInstalled && !connecting;
        }
        async void Changed(object sender, RoutedEventArgs e)
        {
            if (settingProgrammatically || connecting) return;
            bool on = toggle.IsChecked == true;
            connecting = true;
            toggle.IsEnabled = false;
            requestFailure = null;
            error.Visibility = Visibility.Collapsed;
            try { requestFailure = await SettingsActions.Connect(app, on); }
            finally { connecting = false; Refresh(); }
        }
        toggle.Checked += Changed;
        toggle.Unchecked += Changed;
        _followers.Add(_ => Refresh());
        Refresh();
        return Row(text, toggle, tile);
    }

    FrameworkElement AnotherAgentRow()
    {
        var button = new Button { Content = "Copy setup" };
        button.SetResourceReference(StyleProperty, "DeskButton");
        button.Click += (_, _) => SettingsActions.CopyText(SettingsActions.SetupText());
        return Row(RowText("Connect another agent", "For an agent Deskweave can't connect by itself"), button);
    }

    FrameworkElement Accounts()
    {
        var banner = new ContentControl();
        banner.SetResourceReference(StyleProperty, "Banner");
        // Line height straight on the block, not just the Banner template's ContentPresenter - it
        // wraps to two lines and any shortfall in the per-line height doubles up, shrinking the banner.
        var bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 18.85, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        bannerText.Inlines.Add(new Run("Local only.") { FontWeight = FontWeights.SemiBold });
        bannerText.Inlines.Add(new Run(" Sign-ins live in Deskweave's own browser, on this PC, and your own Chrome is never touched."));
        banner.Content = bannerText;

        var accountRows = SettingsFeatures.Accounts
            ? SettingsActions.Accounts().Select(AccountRow).Cast<UIElement>().ToArray()
            : [];
        var signedIn = Section("Signed in", Group(accountRows), AddAccountButton());
        // The account list needs Wave 2 slice E; with it off there is nothing else on this page
        // besides the banner, so the whole category leaves the nav (SettingsView.RefreshNavAvailability).
        signedIn.Visibility = SettingsFeatures.Accounts ? Visibility.Visible : Visibility.Collapsed;

        return new StackPanel
        {
            Children =
            {
                new StackPanel { Margin = new Thickness(0, 0, 0, 22), Children = { banner } },
                signedIn,
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
        var button = new Button { Content = content, MinWidth = 113 };
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
        var signOut = new Button { Content = "Sign out", Margin = new Thickness(9, 0, 0, 0) };
        signOut.SetResourceReference(StyleProperty, "LinkButton");
        signOut.Click += (_, _) => SettingsActions.SignOut?.Invoke(account);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(scope);
        controls.Children.Add(signOut);
        return Row(text, controls, tile);
    }

    FrameworkElement HistoryPrivacy()
    {
        var save = Dropdown("Save screenshots", [
            new Choice(ScreenshotMode.KeySteps, "Key steps"), new Choice(ScreenshotMode.Continuous, "Continuous"), new Choice(ScreenshotMode.Off, "Off"),
        ], s => s.Screenshots, (s, v) => s with { Screenshots = v });
        var saveRow = Row(RowText("Save screenshots", "They show up in What it did"), save);
        // Save screenshots saves a value nothing writes by yet; the whole group (its label included)
        // hides until Wave 2 slice F turns SettingsFeatures.History on. Storage and Delete all
        // Deskweave data below always show, so this page and History & privacy in the nav are never
        // left empty either way.
        var screenshots = Section("Screenshots", Group(saveRow));
        screenshots.Visibility = SettingsFeatures.History ? Visibility.Visible : Visibility.Collapsed;
        screenshots.Margin = new Thickness(0, 0, 0, 21);

        IReadOnlyList<StorageKind> kinds = SettingsStorage.Kinds();
        var totalText = Styled("— used", "StorageTotal");
        var meterGrid = new Grid();
        var meterStack = new StackPanel();
        meterStack.Children.Add(totalText);
        meterStack.Children.Add(StorageMeter(meterGrid));
        var meterRow = new Border { Padding = new Thickness(14, 13, 14, 13), MinHeight = 50, Child = meterStack };

        var sizeTexts = new TextBlock[kinds.Count];
        var kindRows = new UIElement[kinds.Count];
        for (int i = 0; i < kinds.Count; i++)
        {
            StorageKind kind = kinds[i];
            var sizeText = Styled("—", "StorageSize");
            sizeTexts[i] = sizeText;
            var clear = Confirm("Clear",
                () => sizeText.Text == "—" ? "Clear " + kind.ClearNoun + "?" : "Clear " + sizeText.Text + " of " + kind.ClearNoun + "?",
                () => { kind.Clear(); RefreshStorage(); },
                danger: false, style: "LinkButton", enabled: kind.CanClear, disabledTooltip: kind.DisabledTooltip);
            clear.Margin = new Thickness(9, 0, 0, 0);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(sizeText);
            controls.Children.Add(clear);
            var row = Row(RowText(kind.Label, kind.Hint), controls);
            // Agent browser has no reliable size source yet (Wave 2 slice E); its row hides until
            // SettingsFeatures.BrowserData turns on. The other three kinds always show.
            row.Visibility = kind.Visible() ? Visibility.Visible : Visibility.Collapsed;
            kindRows[i] = row;
        }
        var storage = Section("Storage", Group([meterRow, .. kindRows]));
        storage.Margin = new Thickness(0, 0, 0, 20);

        void RefreshStorage()
        {
            Task.Run(() =>
            {
                var measured = new long?[kinds.Count];
                for (int i = 0; i < kinds.Count; i++) measured[i] = kinds[i].Visible() ? kinds[i].Measure() : null;
                Dispatcher.BeginInvoke(() =>
                {
                    long total = 0;
                    for (int i = 0; i < kinds.Count; i++)
                    {
                        sizeTexts[i].Text = measured[i] is { } bytes ? SettingsActions.FormatStorageBytes(bytes) : "—";
                        if (measured[i] is { } known) total += known;
                    }
                    totalText.Text = SettingsActions.FormatStorageBytes(total) + " used";
                    FillStorageMeter(meterGrid, kinds, measured);
                });
            });
        }
        RefreshStorage();

        var projectEntries = SettingsFeatures.ProjectFiles ? SettingsStorage.Projects() : [];
        UIElement[] projectRows = projectEntries.Select(project =>
        {
            var sizeText = Styled(project.Bytes is { } bytes ? SettingsActions.FormatStorageBytes(bytes) : "—", "StorageSize");
            var show = new Button { Content = "Show in Explorer", Margin = new Thickness(8, 0, 0, 0) };
            show.SetResourceReference(StyleProperty, "LinkButton");
            show.Click += (_, _) => SettingsActions.OpenFolder(project.Path);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(sizeText);
            controls.Children.Add(show);
            return (UIElement)Row(RowText(project.Name, project.Path), controls);
        }).ToArray();
        var projects = Section("Made by agents in your projects", Group(projectRows));
        // No reliable way to find a workspace's project folder yet (Wave 2 slice F); hidden until
        // SettingsFeatures.ProjectFiles is on and there is at least one row to show.
        projects.Visibility = SettingsFeatures.ProjectFiles && projectRows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var deleteRow = Row(RowText("Delete all Deskweave data", "Stops every workspace, deletes Deskweave's data, and quits. Your projects stay."),
            Confirm("Delete", () => "Delete all Deskweave data? This can't be undone.",
                () => SettingsActions.DeleteAllData(SettingsActions.DataFolders), danger: true));
        var uninstallRow = Row(RowText("Uninstall Deskweave", "Deletes all Deskweave data, disconnects your agents, and removes the app. Your projects stay."),
            Confirm("Uninstall", () => "Uninstall Deskweave and delete all its data? This can't be undone.",
                () => SettingsActions.Uninstall(), danger: true));

        return new StackPanel
        {
            Children =
            {
                screenshots,
                storage,
                projects,
                Section(null, SettingsActions.Uninstaller is null ? Group(deleteRow) : Group(deleteRow, uninstallRow)),
            },
        };
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
