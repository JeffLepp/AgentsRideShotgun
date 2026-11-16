using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

public partial class SettingsView
{
    FrameworkElement Storage()
    {
        var page = new StackPanel();
        var total = Styled("Measuring ARS storage…", "StorageTotal");
        total.TextWrapping = TextWrapping.Wrap;
        var message = Styled("", "RowHint");
        message.TextWrapping = TextWrapping.Wrap;
        var detail = new StackPanel();
        var preview = new StackPanel();
        Button refresh = null!;
        refresh = Button("Refresh", async () => await Refresh());
        page.Children.Add(Row(total, refresh));
        page.Children.Add(RowText("On this PC", "File sizes in ARS's own folders. Your projects and shared browser are outside this total."));
        page.Children.Add(message);
        page.Children.Add(detail);
        page.Children.Add(Section("Clean up", Group(
            Action("Browser caches", "Keeps sign-ins, passwords, tabs and saved browser data.", "caches"),
            Action("Logs", "Clears logs only when their workspace has stopped.", "logs"),
            Action("Screen captures", "Removes saved ARS screenshots from stopped workspaces.", "captures"),
            Action("Temporary files", "Clears temporary files in stopped workspaces.", "temp"),
            Action("Old update packages", "Keeps the current package, one rollback, and pending updates.", "packages"),
            Action("Previous versions", "Keeps one labelled rollback. Private recovery archives are protected.", "versions"),
            Action("Unused workspaces", "Deletes files and signed-in browser profiles. Review every path first.", "workspaces"))));
        page.Children.Add(preview);
        StoragePolicy policy = StorageController.Source.Policy;
        var automatic = new CheckBox { IsChecked = policy.Automatic, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(automatic, "Automatic safe cleanup");
        automatic.Checked += (_, _) => Save(p => p with { Automatic = true });
        automatic.Unchecked += (_, _) => Save(p => p with { Automatic = false });
        page.Children.Add(Section("Retention", Group(
            Row(RowText("Automatic safe cleanup", "Hourly, for stopped workspaces only. Never automatically deletes workspaces or saved browser profiles."), automatic),
            PolicyChoice("Browser cache limit per workspace", new[] { 64, 256, 512, 1024 }, policy.CacheCapMb, " MB", (p, v) => p with { CacheCapMb = v }),
            PolicyChoice("Log limit per workspace", new[] { 16, 64, 128, 256 }, policy.LogCapMb, " MB", (p, v) => p with { LogCapMb = v }),
            PolicyChoice("Keep logs for", new[] { 7, 30, 90, 365 }, policy.LogDays, " days", (p, v) => p with { LogDays = v }),
            PolicyChoice("Unused workspace threshold", new[] { 7, 30, 90, 180 }, policy.UnusedDays, " days", (p, v) => p with { UnusedDays = v }),
            Row(RowText("Captures and temporary files", "Automatic cleanup keeps these for 7 days; captures are also capped at 1 GB per workspace. Old packages are pruned after a successful version change.")))));
        _ = Refresh();
        return page;

        void Save(Func<StoragePolicy, StoragePolicy> change)
        {
            try { StorageController.Source.Policy = change(StorageController.Source.Policy); message.Text = "Retention settings saved."; }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { message.Text = "Couldn't save retention settings: " + e.Message; }
        }
        Border PolicyChoice(string title, int[] values, int value, string suffix, Func<StoragePolicy, int, StoragePolicy> change)
        {
            int[] choices = values.Append(value).Distinct().Order().ToArray();
            var combo = new ComboBox { MinWidth = 110, ItemsSource = choices.Select(v => v + suffix).ToArray(), SelectedIndex = Array.IndexOf(choices, value) };
            combo.SetResourceReference(StyleProperty, "Dropdown");
            AutomationProperties.SetName(combo, title);
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Save(p => change(p, choices[combo.SelectedIndex])); };
            return Row(RowText(title), combo);
        }
        Border Action(string title, string hint, string category) => Row(RowText(title, hint), Button("Preview", async () =>
        {
            preview.Children.Clear();
            message.Text = "Preparing cleanup preview…";
            try
            {
                StorageCleanup plan = await StorageController.Clean(category, true);
                preview.Children.Add(RowText("Cleanup preview", $"{SettingsActions.FormatStorageBytes(plan.PreviewBytes)} ({plan.PreviewBytes:N0} bytes) could be freed · {plan.Paths.Length:N0} " + (plan.Paths.Length == 1 ? "file" : "files")));
                if (plan.Skipped.Length > 0) preview.Children.Add(RowText("Skipped", string.Join("\n", plan.Skipped)));
                if (plan.Errors.Length > 0) preview.Children.Add(RowText("Couldn't inspect every file", string.Join("\n", plan.Errors.Take(5))));
                var paths = new TextBox { Text = string.Join(Environment.NewLine, plan.Paths), IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 8, 0, 8) };
                paths.SetResourceReference(ForegroundProperty, "InkBrush");
                paths.SetResourceReference(BackgroundProperty, "CardBrush");
                AutomationProperties.SetName(paths, "Files selected for cleanup");
                preview.Children.Add(paths);
                var confirm = Button(category == "workspaces" ? "Delete these workspace files" : "Clean up these files", async () =>
                {
                    // Apply only the reviewed selection. A newly eligible file requires a fresh preview.
                    StorageCleanup result = await StorageController.CleanReviewed(category, plan);
                    message.Text = $"Freed {SettingsActions.FormatStorageBytes(result.FreedBytes)}. {result.Skipped.Length} skipped; {result.Errors.Length} errors."
                        + (result.Errors.Length > 0 ? " " + result.Errors[0] : "");
                    preview.Children.Clear();
                    await Refresh(keepMessage: true);
                });
                confirm.IsEnabled = plan.Paths.Length > 0;
                preview.Children.Add(confirm);
                message.Text = category == "workspaces" ? "Deleting a workspace removes its files and browser sign-ins. Review the paths before confirming."
                    : "Review the files below. Running workspaces are skipped again when cleanup starts.";
                preview.BringIntoView();
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { message.Text = "Couldn't prepare cleanup: " + e.Message; }
        }));
        async Task Refresh(bool keepMessage = false)
        {
            refresh.IsEnabled = false;
            try
            {
                StorageReport report = await Task.Run(() => StorageController.Source.Report());
                total.Text = SettingsActions.FormatStorageBytes(report.TotalBytes) + " used by ARS";
                detail.Children.Clear();
                detail.Children.Add(Group(report.Items.GroupBy(i => i.Category).OrderByDescending(g => g.Sum(i => i.Bytes))
                    .Select(g => (UIElement)Row(RowText(WorkspaceStorage.Label(g.Key)), Styled(SettingsActions.FormatStorageBytes(g.Sum(i => i.Bytes)), "StorageSize"))).ToArray()));
                var workspaces = report.Items.Where(i => i.WorkspaceId is not null).GroupBy(i => i.WorkspaceId).ToArray();
                detail.Children.Add(Section("Workspaces", Group(workspaces.OrderByDescending(g => g.Sum(i => i.Bytes)).Select(g =>
                {
                    StorageItem w = g.First();
                    string state = w.Running ? "Running · cleanup protected" : "Stopped";
                    string used = w.LastUsed is { } date ? date.ToLocalTime().ToString("g") : "unknown";
                    long caches = g.Where(i => i.Category == "caches").Sum(i => i.Bytes);
                    long profiles = g.Where(i => i.Category == "browser-profiles").Sum(i => i.Bytes);
                    return (UIElement)Row(RowText(w.Name, $"{state} · last used {used}\nBrowser profile {SettingsActions.FormatStorageBytes(profiles)} · cache {SettingsActions.FormatStorageBytes(caches)}"),
                        Styled(SettingsActions.FormatStorageBytes(g.Sum(i => i.Bytes)), "StorageSize"));
                }).ToArray())));
                if (workspaces.Length == 0) detail.Children.Add(RowText("No saved workspaces", "Workspace usage will appear here after your first workspace is created."));
                if (!keepMessage) message.Text = report.Warnings.Length > 0 ? "Some details unavailable: " + string.Join("; ", report.Warnings.Take(3))
                    : "Measured " + report.MeasuredAt.ToLocalTime().ToString("t") + ". Bridge hosts are retained because other agent clients may still use them.";
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { message.Text = "Couldn't measure storage: " + e.Message; }
            finally { refresh.IsEnabled = true; }
        }
        Button Button(string title, Func<Task> action)
        {
            var button = new Button { Content = title };
            button.SetResourceReference(StyleProperty, "DeskButton");
            AutomationProperties.SetName(button, title);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action(); }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { message.Text = "Cleanup failed: " + e.Message; }
                finally { button.IsEnabled = true; }
            };
            return button;
        }
    }
}
