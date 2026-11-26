using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

public partial class SettingsView
{
    /// <summary>One line on the Storage page: the categories it counts, the subset Remove deletes,
    /// and, for user data, the one-line warning the second click confirms.</summary>
    internal sealed record StorageGroup(string Label, string Hint, string[] Counts, string[] Removes, string? Warning);

    // Four rows, fixed order. Every scanned category lands in exactly one; anything unknown counts as app files.
    internal static readonly StorageGroup[] StorageGroups =
    [
        new("Browser sign-ins", "Saved sign-ins and site data.", ["browser-profiles"], ["browser-profiles"],
            "Signs stopped workspaces out of every site."),
        new("Caches, logs and temp files", "Safe to remove anytime.", ["caches", "logs", "temp"], ["caches", "logs", "temp"], null),
        new("Recordings and workspace files", "Remove deletes recordings and captures. Workspace files stay.",
            ["recorder-runs", "captures", "workspace-data"], ["recorder-runs", "captures"], "Deletes recordings and screen captures."),
        new("App and updates", "Remove keeps this version and one rollback.", ["current", "packages", "versions", "bridge-hosts", "settings"],
            ["packages", "versions"], null),
    ];

    internal static StorageGroup StorageGroupOf(string category) =>
        StorageGroups.FirstOrDefault(g => g.Counts.Contains(category)) ?? StorageGroups[^1];

    FrameworkElement Storage()
    {
        var page = new StackPanel();
        bool busy = false;

        // Before the first measurement: one centered button and nothing else.
        var start = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 36, 0, 36) };
        var question = Styled("How much space does ARS use?", "StorageTotal");
        question.FontSize = 15;
        question.HorizontalAlignment = HorizontalAlignment.Center;
        var measure = StorageButton("Measure storage", primary: true);
        measure.HorizontalAlignment = HorizontalAlignment.Center;
        measure.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetAutomationId(measure, "MeasureStorage");
        var spinner = new ProgressBar { Height = 4, Width = 160, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        start.Children.Add(question);
        start.Children.Add(measure);
        start.Children.Add(spinner);
        page.Children.Add(start);

        // After it: the total, four rows with Remove, and a short note only when something went wrong.
        var results = new StackPanel { Visibility = Visibility.Collapsed };
        var total = Styled("", "StorageTotal");
        total.FontSize = 20;
        total.TextWrapping = TextWrapping.Wrap;
        var measured = Styled("", "RowHint");
        measured.VerticalAlignment = VerticalAlignment.Center;
        // A link beside the time, not a button beside the total: the 320 DIP card clipped the total.
        var again = new Button { Content = "Measure again", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        again.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetName(again, "Measure again");
        var when = new StackPanel { Orientation = Orientation.Horizontal };
        when.Children.Add(measured);
        when.Children.Add(again);
        var header = new StackPanel { Margin = new Thickness(2, 0, 0, 10) };
        header.Children.Add(total);
        header.Children.Add(when);
        results.Children.Add(header);
        var note = Styled("", "RowHint");
        note.TextWrapping = TextWrapping.Wrap;
        note.Visibility = Visibility.Collapsed;
        note.Margin = new Thickness(2, 0, 0, 8);
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        results.Children.Add(note);

        StorageRow[] rows = [];
        rows = StorageGroups.Select((group, index) => new StorageRow(this, group, index, Remove)).ToArray();
        var card = Group(rows.Select(r => (UIElement)r.Element).ToArray());
        card.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(card);
        page.Children.Add(results);

        page.Children.Add(StorageFold("Automatic cleanup", StorageAutomatic));

        measure.Click += async (_, _) => await Measure();
        again.Click += async (_, _) => await Measure();
        return page;

        async Task Measure()
        {
            if (busy) return;
            SetBusy(true);
            measure.Content = "Measuring…";
            spinner.Visibility = Visibility.Visible;
            measured.Text = "Measuring…";
            try
            {
                var source = StorageController.Source;
                StorageUsage usage = await Task.Run(() => source.Usage(true));
                total.Text = SettingsActions.FormatStorageBytes(usage.TotalBytes) + " used by ARS";
                measured.Text = "Measured " + usage.MeasuredAt.ToLocalTime().ToString("t");
                foreach (var row in rows) row.Show(usage);
                // Folders without a workspace record and skipped links are expected; only unreadable files lower the total.
                bool unreadable = usage.Warnings.Any(w => !w.StartsWith("Workspace record unavailable:", StringComparison.Ordinal)
                    && !w.StartsWith("Skipped link:", StringComparison.Ordinal));
                Note(unreadable ? "Some files couldn’t be read, so sizes may be a little low." : "");
                start.Visibility = Visibility.Collapsed;
                results.Visibility = Visibility.Visible;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (results.Visibility == Visibility.Visible) Note("Couldn’t measure storage. " + e.Message);
                else question.Text = "Couldn’t measure storage. Try again.";
                measured.Text = "";
            }
            finally
            {
                measure.Content = "Measure storage";
                spinner.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
        }

        async Task Remove(StorageRow row)
        {
            if (busy) return;
            SetBusy(true);
            row.Removing();
            string outcome;
            try
            {
                StorageCleanup result = await StorageController.CleanSelected(row.Group.Removes, false);
                // Cleanup notes every running workspace it passes, even for app updates it never touches.
                bool running = row.Group.Removes.Any(c => c is not ("packages" or "versions")) && result.Skipped.Any(s => s.EndsWith("workspace is running", StringComparison.Ordinal)
                    || s.EndsWith("started before cleanup; skipped", StringComparison.Ordinal));
                int failed = result.Errors.Length;
                outcome = result.FreedBytes > 0 ? "Removed " + CleanupSize(result.FreedBytes) : "Nothing to remove";
                if (running) outcome += " · stop running workspaces to remove the rest";
                else if (failed > 0) outcome += $" · {failed:N0} {(failed == 1 ? "file was" : "files were")} in use";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                outcome = "Couldn’t remove. " + e.Message;
            }
            SetBusy(false);
            await Measure();
            row.Done(outcome);
        }

        void SetBusy(bool value)
        {
            busy = value;
            measure.IsEnabled = again.IsEnabled = !value;
            foreach (var row in rows) row.Enabled = !value;
        }

        void Note(string text)
        {
            note.Text = text;
            note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>A Storage row: label, one-line hint, size, and a Remove link. Rows holding user data
    /// ask once inline (the link turns into "Confirm" and the hint into the warning); nothing else asks.</summary>
    sealed class StorageRow
    {
        internal readonly StorageGroup Group;
        internal readonly Border Element;
        readonly TextBlock _hint, _size;
        readonly Button _remove;
        readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(6) };
        bool _armed, _removable, _enabled = true;

        internal StorageRow(SettingsView view, StorageGroup group, int index, Func<StorageRow, Task> remove)
        {
            Group = group;
            var text = RowText(group.Label, group.Hint);
            _hint = (TextBlock)text.Children[1];
            _hint.TextWrapping = TextWrapping.Wrap;
            _size = Styled("", "StorageSize");
            _remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            _remove.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            AutomationProperties.SetName(_remove, "Remove " + group.Label.ToLowerInvariant());
            AutomationProperties.SetAutomationId(_remove, "StorageRemove" + index);
            var side = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            side.Children.Add(_size);
            side.Children.Add(_remove);
            Element = view.Row(text, side);
            _disarm.Tick += (_, _) => Disarm();
            _remove.Click += async (_, _) =>
            {
                if (Group.Warning is not null && !_armed)
                {
                    _armed = true;
                    _remove.Content = "Confirm";
                    _remove.SetResourceReference(Control.ForegroundProperty, "DangerInkBrush");
                    AutomationProperties.SetName(_remove, "Confirm remove " + Group.Label.ToLowerInvariant());
                    _hint.Text = Group.Warning;
                    _hint.SetResourceReference(FrameworkElement.StyleProperty, "RowError");
                    _disarm.Start();
                    return;
                }
                Disarm();
                await remove(this);
            };
        }

        internal bool Enabled { set { _enabled = value; _remove.IsEnabled = value && _removable; } }

        internal void Show(StorageUsage usage)
        {
            long bytes = usage.Categories.Where(c => StorageGroupOf(c.Category) == Group).Sum(c => c.Bytes);
            _removable = usage.Categories.Any(c => Group.Removes.Contains(c.Category) && c.Bytes > 0);
            _size.Text = SettingsActions.FormatStorageBytes(bytes);
            _remove.Visibility = _removable ? Visibility.Visible : Visibility.Hidden;
            _remove.IsEnabled = _enabled && _removable;
            Disarm();
            _hint.Text = Group.Hint;
        }

        internal void Removing()
        {
            _remove.Content = "Removing…";
            _hint.Text = "Removing…";
        }

        internal void Done(string outcome)
        {
            _remove.Content = "Remove";
            _hint.Text = outcome;
        }

        void Disarm()
        {
            _disarm.Stop();
            if (!_armed) return;
            _armed = false;
            _remove.Content = "Remove";
            _remove.ClearValue(Control.ForegroundProperty);
            AutomationProperties.SetName(_remove, "Remove " + Group.Label.ToLowerInvariant());
            _hint.SetResourceReference(FrameworkElement.StyleProperty, "RowHint");
            _hint.Text = Group.Hint;
        }
    }

    static string CleanupSize(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : SettingsActions.FormatStorageBytes(bytes);

    static Button StorageButton(string title, Func<Task>? action = null, bool primary = false)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "DeskButton");
        AutomationProperties.SetName(button, title);
        if (action is not null) button.Click += async (_, _) => await action();
        return button;
    }

    static Expander StorageFold(string title, Func<UIElement> content)
    {
        var fold = new Expander { Header = title, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 4, 0, 4) };
        fold.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        AutomationProperties.SetName(fold, title);
        fold.Expanded += (_, _) => fold.Content ??= content();
        return fold;
    }

    /// <summary>The one cleanup submenu: the safe automatic trim and how much recording to keep.
    /// Caps and ages stay at their defaults in <see cref="StoragePolicy"/>.</summary>
    UIElement StorageAutomatic()
    {
        StoragePolicy policy = StorageController.Source.Policy;
        var error = Styled("", "RowError");
        error.Visibility = Visibility.Collapsed;
        var automatic = new CheckBox { IsChecked = policy.Automatic };
        automatic.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(automatic, "Clean up automatically");
        automatic.Checked += (_, _) => Save(p => p with { Automatic = true });
        automatic.Unchecked += (_, _) => Save(p => p with { Automatic = false });
        int[] limits = new[] { 0, 1, 2, 5, 10, 20, 50, policy.RecorderLimitGb }.Distinct().Order().ToArray();
        var limit = new ComboBox { MinWidth = 110, ItemsSource = limits.Select(v => v == 0 ? "Keep all" : v + " GB").ToArray(),
            SelectedIndex = Array.IndexOf(limits, policy.RecorderLimitGb) };
        limit.SetResourceReference(StyleProperty, "Dropdown");
        AutomationProperties.SetName(limit, "Recording limit");
        limit.SelectionChanged += (_, _) => { if (limit.SelectedIndex >= 0) Save(p => p with { RecorderLimitGb = limits[limit.SelectedIndex] }); };
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(Group(
            Row(RowText("Clean up automatically", "Trims old caches, logs and temp files at startup and every hour."), automatic),
            Row(RowText("Recording limit", "Oldest recordings go first when automatic cleanup is on."), limit)));
        panel.Children.Add(error);
        return panel;

        void Save(Func<StoragePolicy, StoragePolicy> change)
        {
            try { StorageController.Source.Policy = change(StorageController.Source.Policy); error.Visibility = Visibility.Collapsed; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { error.Text = "Couldn’t save. " + e.Message; error.Visibility = Visibility.Visible; }
        }
    }
}
