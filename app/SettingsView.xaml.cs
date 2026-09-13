using System.Windows.Controls;
using System.Windows.Input;

namespace Deskweave;

/// <summary>
/// Settings, hosted by the hub window below its title bar when it is 1200 DIP wide. Every control
/// reads and writes <see cref="HiveMind.AgentWorkspaces.AppSettingsStore"/>; nothing here keeps its
/// own copy of a setting.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { BackRequested?.Invoke(); e.Handled = true; } };
    }

    /// <summary>"Workspaces" at the top of the list was chosen, or Escape pressed.</summary>
    public event Action? BackRequested;

    /// <summary>Opens a category: general, agents, control, browser, corner, alerts, history,
    /// perf, privacy or about.</summary>
    public void Show(string category) { }
}
