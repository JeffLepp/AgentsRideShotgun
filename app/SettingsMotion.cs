using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Deskweave;

/// <summary>
/// The switch knob's slide: 150 ms, and none when Windows' animations are off. The template's
/// triggers put the knob where it belongs; this only animates the move, so a switch set before it
/// is shown, or with animations off, simply is where it belongs.
/// </summary>
public static class SettingsMotion
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.RegisterAttached(
        "Slide", typeof(bool), typeof(SettingsMotion), new PropertyMetadata(false, SlideChanged));

    public static bool GetSlide(DependencyObject target) => (bool)target.GetValue(SlideProperty);
    public static void SetSlide(DependencyObject target, bool value) => target.SetValue(SlideProperty, value);

    // The knob travels from 5 to 24 DIP inside the track (Controls.xaml, ToggleSwitch).
    const double Travel = 19;

    static void SlideChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ToggleButton toggle) return;
        toggle.Checked -= Moved;
        toggle.Unchecked -= Moved;
        if (e.NewValue is true)
        {
            toggle.Checked += Moved;
            toggle.Unchecked += Moved;
        }
    }

    static void Moved(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        if (!SystemParameters.ClientAreaAnimation || !toggle.IsLoaded) return;
        if (toggle.Template?.FindName("Knob", toggle) is not FrameworkElement knob) return;
        if (knob.RenderTransform is not TranslateTransform { IsFrozen: false } shift) knob.RenderTransform = shift = new TranslateTransform();
        // The trigger has already moved the knob; start it back where it was and let it slide home.
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toggle.IsChecked == true ? -Travel : Travel, 0,
            TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
