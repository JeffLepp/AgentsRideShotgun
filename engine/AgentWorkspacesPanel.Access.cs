using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace HiveMind.AgentWorkspaces;

public partial class AgentWorkspacesPanel
{
    bool _showingAccess;
    string _accessMessage = "";
    internal Action<string>? DesktopOpenForTests { get; set; }

    void ShowAccess()
    {
        if (AccessSummaryText is null) return;
        WorkspaceAccessPolicy policy = _live?.Access?.Policy ?? WorkspaceAccessStore.Read(_stored.Id);
        // Read fresh: the hub's menu and the router change this record while the panel is open.
        string rule = WorkspaceStore.Find(_stored.Id)?.Agents ?? _stored.Agents;
        _showingAccess = true;
        try
        {
            DesktopRequestsChoice.IsChecked = policy.DesktopRequests;
            WebContentBlockingChoice.IsChecked = policy.BlockProgramsAfterWebContent;
            PrewarmBrowserChoice.IsChecked = policy.PrewarmBrowser;
        }
        finally { _showingAccess = false; }
        foreach (RadioButton choice in new[] { AgentsJustMe, AgentsAnyone, AgentsFolder, AgentsClaude, AgentsCodex })
            choice.IsChecked = choice.Tag is "folder" ? WorkspaceHome.IsFolder(rule) : Equals(choice.Tag, rule);
        AgentsFolder.Content = WorkspaceHome.IsFolder(rule) ? WorkspaceHome.Label(rule) : "A folder…";
        DesktopRequestsChoice.IsEnabled = policy.Enabled;
        WebContentBlockingStateText.Text = policy.BlockProgramsAfterWebContent
            ? "On: after an agent reads a page or browser screenshot, new commands and program launches are blocked."
            : "Off: agents can inspect, edit and retest after browsing. Web pages may contain malicious instructions that influence commands on your PC.";
        var requests = _live?.Access?.Handoffs.All ?? [];
        int pending = requests.Count(r => r.State == "pending");
        string who = WorkspaceHome.Label(rule);
        AccessSummaryText.Text = pending > 0 ? $"Agents · {pending} desktop request{(pending == 1 ? "" : "s")} waiting"
            : "Agents · " + (who.Length > 0 ? who : "Just me");
        AccessDetailText.Text = _accessMessage.Length > 0 ? _accessMessage
            : _live?.Access?.Controller is { Length: > 0 } controller
                ? $"{WorkspaceHome.DisplayName(controller)} is working here. Click the screen to take over."
                : string.Empty;
        DesktopRequestList.Children.Clear();
        DesktopRequestList.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var request in requests.Where(r => r.State == "pending"))
        {
            var row = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
            var heading = new TextBlock { Text = request.Reason, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "ShellTextBrush");
            row.Children.Add(heading);
            var target = new TextBlock { Text = request.Target, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 6) };
            target.SetResourceReference(TextBlock.ForegroundProperty, "ShellTextBrush");
            row.Children.Add(target);
            var actions = new WrapPanel();
            foreach ((string label, bool approve) in new[] { ("Open on my desktop", true), ("Keep here", false) })
            {
                var button = new Button { Content = label, Style = (Style)FindResource("QuietAction"),
                    Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 0) };
                AutomationProperties.SetName(button, label + ": " + request.Reason);
                string requestId = request.Id;
                button.Click += (_, _) => DecideDesktopRequest(requestId, approve);
                actions.Children.Add(button);
            }
            row.Children.Add(actions);
            DesktopRequestList.Children.Add(row);
        }
    }

    void AccessExpander_Expanded(object sender, RoutedEventArgs e) => ShowAccess();

    /// <summary>Click, not Checked: choosing the folder again must reopen the picker.</summary>
    void AgentsChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        string? rule = tag == "folder" ? PickAgentsFolder() : tag;
        if (rule is not null)
        {
            try { WorkspaceHome.Set(_stored.Id, rule); _accessMessage = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { _accessMessage = "Could not save that: " + ex.Message; }
        }
        ShowAccess();
        // The hub's tile says who works here.
        WorkspaceChanged?.Invoke();
    }

    string? PickAgentsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Agents working in this folder use this workspace" };
        bool? picked = Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return picked == true ? WorkspaceHome.Folder(dialog.FolderName) : null;
    }

    void DesktopRequestsChoice_Click(object sender, RoutedEventArgs e)
    { if (!_showingAccess) SaveAccess(WorkspaceAccessStore.Read(_stored.Id) with { DesktopRequests = DesktopRequestsChoice.IsChecked == true }); }
    void WebContentBlockingChoice_Click(object sender, RoutedEventArgs e)
    { if (!_showingAccess) SaveAccess(WorkspaceAccessStore.Read(_stored.Id) with { BlockProgramsAfterWebContent = WebContentBlockingChoice.IsChecked == true }); }
    void PrewarmBrowserChoice_Click(object sender, RoutedEventArgs e)
    { if (!_showingAccess) SaveAccess(WorkspaceAccessStore.Read(_stored.Id) with { PrewarmBrowser = PrewarmBrowserChoice.IsChecked == true }); }

    internal void SaveAccess(WorkspaceAccessPolicy policy)
    {
        _accessMessage = "";
        try
        {
            // Apply only restrictions before persistence. A failed write must never enable
            // access, allow a handoff, or switch browser blocking off, even in a mixed update.
            if (_live?.Access is { } access && access.Policy.TightenWith(policy) is { } tightened
                && tightened != access.Policy) access.Configure(tightened);
            WorkspaceAccessStore.Write(_stored.Id, policy);
            _live?.Access?.Configure(policy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _accessMessage = "Could not save agent access: " + ex.Message; }
        ShowAccess();
    }

    internal void DecideDesktopRequest(string id, bool approve)
    {
        if (_live?.Access is not { } access) return;
        if (approve && (!access.Policy.Enabled || !access.Policy.DesktopRequests)) return;
        WorkspaceHandoff result = access.Handoffs.Decide(id, approve, DesktopOpenForTests);
        _accessMessage = result.Detail;
        ShowAccess();
    }
}
