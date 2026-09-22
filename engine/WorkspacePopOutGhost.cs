using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// What follows the pointer once a window has been pulled off a picture of a workspace, the way a
/// browser tab follows it once it leaves the tab strip: that window's own picture, and one line
/// saying what letting go will do. It never takes the pointer or the focus; it only shows.
/// </summary>
sealed class WorkspacePopOutGhost : Window
{
    const double MaxWidth_ = 520;
    readonly TextBlock _line;
    readonly Image _picture;
    readonly Point _grab;          // where the pointer holds the picture, as a fraction of its size

    internal WorkspacePopOutGhost(ImageSource? picture, Size native, Point grab)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        _grab = grab;

        double scale = Math.Min(1, MaxWidth_ / Math.Max(1, native.Width));
        _picture = new Image
        {
            Source = picture, Stretch = Stretch.Fill,
            Width = Math.Max(120, native.Width * scale), Height = Math.Max(80, native.Height * scale),
        };
        var frame = new Border
        {
            Child = _picture, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(18),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.28 },
        };
        _line = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        var label = new Border
        {
            Child = _line, Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1D)),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 30),
        };
        Content = new Grid { Children = { frame, label } };
        SourceInitialized += (_, _) =>
        {
            // Click-through, never activated, not in Alt+Tab: it is a picture on the glass.
            nint handle = new WindowInteropHelper(this).Handle;
            const int exStyle = -20;
            const long transparent = 0x20, toolWindow = 0x80, noActivate = 0x08000000;
            SetWindowLongPtrW(handle, exStyle, GetWindowLongPtrW(handle, exStyle) | (nint)(transparent | toolWindow | noActivate));
        };
        Say(true);
    }

    /// <summary>Keeps the picture under the pointer, held where it was grabbed. The point is in
    /// screen pixels; letting go outside keeps its promise, letting go back over the workspace cancels.</summary>
    internal void Follow(Point screen, bool outside)
    {
        Say(outside);
        if (!IsVisible) Show();
        Matrix toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point at = toDips.Transform(screen);
        Left = at.X - 18 - _picture.Width * _grab.X;
        Top = at.Y - 18 - _picture.Height * _grab.Y;
    }

    internal void Refresh(ImageSource picture) => _picture.Source = picture;

    void Say(bool outside)
    {
        _line.Text = outside ? "Let go to open it on your desktop" : "Let go to leave it here";
        Opacity = outside ? 0.96 : 0.55;
    }

    [DllImport("user32.dll")] static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
