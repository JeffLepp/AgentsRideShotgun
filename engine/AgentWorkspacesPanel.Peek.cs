using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The corner view's settings. The window itself belongs to <see cref="WorkspacePeekHost"/> and runs
/// whether or not this page exists; this is only where the owner chooses what it does.
/// </summary>
public partial class AgentWorkspacesPanel
{
    bool _showingPeek;
    bool _recordingHotkey;

    void StartPeek()
    {
        // Also started at app startup. Started here as well because a panel can be built in a host
        // that never called Initialize, and because the settings below must act on something live.
        WorkspacePeekHost.Start();
        WorkspacePeekHost.Changed += PeekChanged;
        ShowPeek();
    }

    void StopPeek() => WorkspacePeekHost.Changed -= PeekChanged;

    /// <summary>The window was pinned, moved or dismissed somewhere else. Raised off this thread
    /// when the hotkey did it, so it hops before it touches a control.</summary>
    void PeekChanged() => Dispatcher.BeginInvoke(() => { if (!_disposed) ShowPeek(); });

    void ShowPeek()
    {
        if (PeekSummaryText is null) return;
        PeekSettings settings = WorkspacePeekHost.Settings;

        // Filling the radio buttons checks them, and a checked handler that saved would write the
        // settings back every time the page drew itself.
        _showingPeek = true;
        try
        {
            PeekOffChoice.IsChecked = settings.Mode == PeekMode.Off;
            PeekActivityChoice.IsChecked = settings.Mode == PeekMode.Activity;
            PeekAlwaysChoice.IsChecked = settings.Mode == PeekMode.Always;
            PeekBottomRightChoice.IsChecked = settings.Corner == PeekCorner.BottomRight;
            PeekBottomLeftChoice.IsChecked = settings.Corner == PeekCorner.BottomLeft;
            PeekTopRightChoice.IsChecked = settings.Corner == PeekCorner.TopRight;
            PeekTopLeftChoice.IsChecked = settings.Corner == PeekCorner.TopLeft;
            PeekHotkeyButton.Content = settings.Hotkey.Length > 0 ? settings.Hotkey : "None";
        }
        finally
        {
            _showingPeek = false;
        }

        PeekSummaryText.Text = "Corner view · " + settings.Mode switch
        {
            PeekMode.Activity => "While it works",
            PeekMode.Always => "Always",
            _ => "Off",
        } + (WorkspacePeekHost.Pinned ? " · held up now" : string.Empty);

        PeekModeNote.Text = settings.Mode switch
        {
            PeekMode.Activity =>
                $"Fades in when the workspace does something and fades out about {settings.QuietSeconds}"
                + " seconds after it stops. It stays up for as long as a mission runs.",
            PeekMode.Always => "Stays up while a workspace is running, and goes when the last one stops.",
            _ => "Nothing runs while it is off: no window, no timer, no extra picture taken. The"
                + " shortcut still calls it up.",
        };

        // Where it is only when that is not one of the four buttons above - a second monitor, which
        // has no corner in these settings and keeps the exact place it was dropped.
        if (settings is { Left: not null, Top: not null })
            PeekModeNote.Text += " You have dragged it off your main screen, so it stays where you put it.";

        PeekHotkeyNote.Text = settings.Hotkey.Length == 0
            ? "No shortcut. It appears on its own, by the rule above."
            : WorkspacePeekHost.HotkeyHeld
                ? $"{settings.Hotkey} holds it up and lets it go again. With no workspace running it"
                    + " opens this page instead."
                : $"Windows would not give Deskweave {settings.Hotkey}: another program is already"
                    + " holding it. Choose another.";
    }

    void PeekMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_showingPeek || sender is not RadioButton { Tag: string tag }) return;
        if (Enum.TryParse(tag, out PeekMode mode)) SavePeek(WorkspacePeekHost.Settings with { Mode = mode });
    }

    void PeekCorner_Checked(object sender, RoutedEventArgs e)
    {
        if (_showingPeek || sender is not RadioButton { Tag: string tag }) return;
        // A corner chosen here beats a place it was dragged to, or the choice would do nothing at
        // all for a window the owner had moved by hand.
        if (Enum.TryParse(tag, out PeekCorner corner))
            SavePeek(WorkspacePeekHost.Settings with { Corner = corner, Left = null, Top = null });
    }

    void SavePeek(PeekSettings settings)
    {
        bool saved = WorkspacePeekHost.Apply(settings);
        ShowPeek();
        if (!saved)
            PeekHotkeyNote.Text = "That is what it is doing now, but the choice could not be written"
                + " to disk, so it will not survive a restart.";
    }

    void PeekHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        PeekHotkeyButton.Content = "Press keys";
        PeekHotkeyNote.Text = "Hold Ctrl, Alt, Shift or Win and press a key. Escape keeps the one it has.";
        PeekHotkeyButton.Focus();
    }

    void PeekHotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;
        // Alt combinations arrive as Key.System with the real key alongside them.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            PeekHotkeyNote.Text = "Add Ctrl, Alt, Shift or Win. A key on its own would be taken away"
                + " from every other program on the PC.";
            return;
        }
        _recordingHotkey = false;
        SavePeek(WorkspacePeekHost.Settings with { Hotkey = WorkspaceHotkey.Format(Keyboard.Modifiers, key) });
    }

    void PeekHotkey_LostFocus(object sender, RoutedEventArgs e) => EndRecording();

    void PeekHotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = false;
        SavePeek(WorkspacePeekHost.Settings with { Hotkey = string.Empty });
    }

    void EndRecording()
    {
        if (!_recordingHotkey) return;
        _recordingHotkey = false;
        ShowPeek();
    }
}
