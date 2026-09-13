using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The corner view: a small always-on-top picture of the workspace that is working, in a corner of
/// the owner's own screen. It shows and it opens; it does not drive. Taking control of a workspace
/// happens in the panel, where the lease, the evidence log and the takeover button are - a floating
/// window that could type into an agent's desktop would be a second input path to trust.
///
/// It never takes focus. <see cref="Window.ShowActivated"/> is false and the window is a tool window
/// (ShowInTaskbar false), so it appears over a full-screen editor or a game without stealing the
/// keystroke the owner was in the middle of.
/// </summary>
public partial class WorkspacePeekWindow : Window
{
    static readonly Duration Arriving = new(TimeSpan.FromMilliseconds(220));
    static readonly Duration Leaving = new(TimeSpan.FromMilliseconds(420));

    bool _leaving;

    internal WorkspacePeekWindow()
    {
        // Before InitializeComponent, and before any Show: WPF reads it when the source is created.
        ShowActivated = false;
        InitializeComponent();
    }

    /// <summary>The owner wants the whole workspace: the picture or the open button was clicked.</summary>
    internal event Action? OpenRequested;

    /// <summary>The pin was clicked. The host owns whether it is pinned; this only reports the click.</summary>
    internal event Action? PinClicked;

    /// <summary>The x was clicked. It hides now and comes back on the next activity.</summary>
    internal event Action? HideRequested;

    /// <summary>The owner dragged it somewhere, with where it landed in device-independent pixels.</summary>
    internal event Action<Rect>? Dropped;

    /// <summary>Puts the window exactly where the host decided. Never animated: a window sliding
    /// across the screen while an agent works is motion for its own sake.</summary>
    internal void Place(Rect where)
    {
        Width = where.Width;
        Height = where.Height;
        Left = where.Left;
        Top = where.Top;
    }

    /// <summary>Who is on screen and what they are doing, in the words the rest of the app uses.</summary>
    internal void Describe(string name, string doing, string dotBrushKey)
    {
        NameText.Text = name;
        DoingText.Text = doing;
        StateDot.SetResourceReference(Shape.FillProperty, dotBrushKey);
    }

    /// <summary>The latest picture, or null when there is not one yet.</summary>
    internal void ShowFrame(BitmapSource? frame)
    {
        LiveScreen.Source = frame;
        LiveScreen.Visibility = frame is null ? Visibility.Collapsed : Visibility.Visible;
        WaitingText.Visibility = frame is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Draws the pin as held or not. A pinned view ignores the quiet timer entirely.</summary>
    internal void ShowPinned(bool pinned)
    {
        PinGlyph.SetResourceReference(Shape.StrokeProperty,
            pinned ? "ShellAccentBrush" : "ShellIconBrush");
        PinButton.ToolTip = pinned ? "Let it fade when the workspace is quiet" : "Keep this on screen";
        System.Windows.Automation.AutomationProperties.SetName(PinButton,
            pinned ? "Let the corner view fade when the workspace is quiet"
                : "Keep the corner view on screen");
    }

    /// <summary>Fades in, from wherever the opacity currently is. Interrupts a fade out.</summary>
    internal void Arrive()
    {
        _leaving = false;
        if (!IsVisible) Show();
        // Another program may have gone topmost since this last appeared - a game, an installer.
        // Re-asserting it is what keeps the view in front rather than behind whatever went full
        // screen while the workspace was quiet.
        Topmost = true;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, Arriving));
    }

    /// <summary>Fades out and hides. Hidden rather than closed: the next activity is seconds away
    /// and rebuilding a window for it costs more than keeping one that draws nothing.</summary>
    internal void Leave()
    {
        if (!IsVisible || _leaving) return;
        _leaving = true;
        var fade = new DoubleAnimation(0, Leaving);
        fade.Completed += (_, _) =>
        {
            if (!_leaving) return;
            _leaving = false;
            Hide();
            // The picture is a whole-screen bitmap. A hidden window has no business holding one.
            ShowFrame(null);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Whether it is on screen and not on its way off it.</summary>
    internal bool Watching => IsVisible && !_leaving;

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { OpenRequested?.Invoke(); return; }
        // DragMove runs the whole drag and returns when the button comes up, so the landing place
        // is read on the next line rather than from a move event.
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        Dropped?.Invoke(new Rect(Left, Top, Width, Height));
    }

    void LiveScreen_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenRequested?.Invoke();

    void OpenButton_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();

    void PinButton_Click(object sender, RoutedEventArgs e) => PinClicked?.Invoke();

    void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
