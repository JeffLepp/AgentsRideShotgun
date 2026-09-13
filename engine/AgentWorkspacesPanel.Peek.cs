using System.Windows;
using System.Windows.Input;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The corner view's settings now live in Settings > Corner window, wired straight to
/// <see cref="AppSettingsStore"/> (see <see cref="WorkspacePeekHost"/>). This panel is no longer
/// hosted; what remains here only keeps its old XAML compiling.
/// </summary>
public partial class AgentWorkspacesPanel
{
    void StartPeek() => WorkspacePeekHost.Start();
    void StopPeek() { }

    void PeekMode_Checked(object sender, RoutedEventArgs e) { }
    void PeekCorner_Checked(object sender, RoutedEventArgs e) { }
    void PeekHotkey_Click(object sender, RoutedEventArgs e) { }
    void PeekHotkey_KeyDown(object sender, KeyEventArgs e) { }
    void PeekHotkey_LostFocus(object sender, RoutedEventArgs e) { }
    void PeekHotkeyClear_Click(object sender, RoutedEventArgs e) { }
}
