using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The owner's view of the commands an agent is running in this workspace, and the only place the
/// owner can stop one without taking the workspace and hunting for a console window. A command no
/// longer ends because a tool call gave up on it, so the owner has to be able to see what is still
/// going and end it deliberately.
/// </summary>
public partial class AgentWorkspacesPanel
{
    string _commandsShown = "";

    void ShowCommands()
    {
        if (CommandExpander is null) return;
        IReadOnlyList<CommandJob> jobs = _plane?.Commands.Jobs ?? [];
        if (jobs.Count == 0)
        {
            CommandExpander.Visibility = Visibility.Collapsed;
            _commandsShown = "";
            return;
        }

        // Rebuilding the list twice a second would fight the owner's own scrolling and clicking.
        // The elapsed seconds of a running job are deliberately left out of the signature; they are
        // read off the row when it is rebuilt for a reason that matters.
        string signature = string.Join("|", jobs.Select(j => j.Id + ":" + j.Status + ":" + j.ExitCode));
        int running = jobs.Count(j => j.Running);
        CommandExpander.Visibility = Visibility.Visible;
        CommandSummaryText.Text = running > 0
            ? $"Commands · {running} running"
            : $"Commands · {jobs.Count} recent";
        if (signature == _commandsShown) return;
        _commandsShown = signature;

        CommandList.Children.Clear();
        // Newest first: the one the owner is wondering about is the one that just started.
        foreach (CommandJob job in jobs.Reverse())
        {
            // Left-aligned and bounded, so Cancel sits beside the command it stops rather than at
            // the far edge of a wide panel where it reads as belonging to nothing.
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 9),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 620,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lines = new StackPanel();
            var what = new TextBlock
            {
                Text = WorkspaceCommands.Short(job.Command),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            what.SetResourceReference(TextBlock.ForegroundProperty, "ShellTextBrush");
            lines.Children.Add(what);
            var state = new TextBlock { Text = Describe(job), FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) };
            state.SetResourceReference(TextBlock.ForegroundProperty, "ShellMutedBrush");
            lines.Children.Add(state);
            lines.SetValue(Grid.ColumnProperty, 0);
            row.Children.Add(lines);

            if (job.Running)
            {
                var stop = new Button
                {
                    Content = "Cancel",
                    Style = (Style)FindResource("QuietAction"),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                AutomationProperties.SetName(stop, "Cancel command: " + WorkspaceCommands.Short(job.Command));
                string id = job.Id;
                stop.Click += (_, _) =>
                {
                    _plane?.Commands.Cancel(id, "the owner", out _);
                    _commandsShown = "";
                    ShowCommands();
                };
                stop.SetValue(Grid.ColumnProperty, 1);
                row.Children.Add(stop);
            }
            CommandList.Children.Add(row);
        }
    }

    static string Describe(CommandJob job)
    {
        string when = job.Seconds < 60 ? $"{job.Seconds:F0} s" : $"{job.Seconds / 60:F0} min";
        string head = job.Status + " · " + when + " · " + job.Shell;
        if (job.Running) return head + (job.LimitSeconds is { } limit ? $" · limit {limit:F0} s" : "");
        if (job.ExitCode is { } code) head += " · exit code " + code;
        return job.Reason.Length > 0 && job.Reason != "exit code " + job.ExitCode
            ? head + " · " + job.Reason : head;
    }
}
