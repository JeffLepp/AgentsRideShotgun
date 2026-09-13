using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

public partial class WorkspaceFrame : UserControl
{
    /// <summary>
    /// The workspace's own screen. Null - a workspace that is not running, and every demo and
    /// review state - keeps the drawn preview, which is the only thing there is to show when there
    /// is no desktop to photograph.
    /// </summary>
    public static readonly DependencyProperty ScreenProperty = DependencyProperty.Register(
        nameof(Screen), typeof(BitmapSource), typeof(WorkspaceFrame),
        new PropertyMetadata(null, (frame, changed) =>
            ((WorkspaceFrame)frame).ShowScreen((BitmapSource?)changed.NewValue)));

    public WorkspaceFrame() => InitializeComponent();

    public BitmapSource? Screen
    {
        get => (BitmapSource?)GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    void ShowScreen(BitmapSource? screen)
    {
        LiveScreen.ImageSource = screen;
        Live.Visibility = screen is null ? Visibility.Collapsed : Visibility.Visible;
        // Collapsed rather than left underneath: the picture is opaque and covers it, but a drawn
        // desktop that is still in the layout is still measured and arranged every time the card is.
        Drawn.Visibility = screen is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
