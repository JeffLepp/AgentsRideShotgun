using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Resources;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

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

        // On: CornerShow.ComesAndGoes (it comes and goes on its own). Off: CornerShow.Off (it never
        // appears on its own; the tray still shows it, per ModuleEntry.ShowCornerRequested).
        var cornerShow = Toggle("Show the corner window", s => s.CornerShow != CornerShow.Off,
            (s, v) => s with { CornerShow = v ? CornerShow.ComesAndGoes : CornerShow.Off });

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

        var openData = new Button { Content = "Open Deskweave data" };
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
                    Row(RowText("Show the corner window"), cornerShow))),
                Section(null, Group(versionRow, licenseStack, openData)),
            },
        };
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

        return new StackPanel
        {
            Children =
            {
                screenshots,
                storage,
                projects,
                Section(null, Group(deleteRow)),
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
