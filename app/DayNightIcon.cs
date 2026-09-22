using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Deskweave;

/// <summary>
/// The title bar's day and night glyph, 16 DIP like the app's other icons: a sun by day, a crescent
/// by night. Switching tucks the rays in and slides a shadow over the sun's core, so the button
/// shows which one is on and turns into the other when clicked (owner's pick, 2026-09-22).
/// </summary>
internal sealed class DayNightIcon : Canvas
{
    // Sun: a small core, the shadow parked off the glyph. Moon: a larger core with the shadow over
    // its upper right, leaving the crescent.
    const double SunCore = 3, MoonCore = 5.6;
    static readonly Point ShadowAway = new(18, -2), ShadowOver = new(11.2, 4.8);

    readonly EllipseGeometry _core = new(new Point(8, 8), SunCore, SunCore);
    readonly EllipseGeometry _shadow = new(ShadowAway, 4.6, 4.6);
    readonly Path _rays;
    readonly RotateTransform _raysTurn = new(0, 8, 8);
    readonly ScaleTransform _raysScale = new(1, 1, 8, 8);
    bool _dark;

    public DayNightIcon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        var ink = new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self };

        var core = new Path { Data = new CombinedGeometry(GeometryCombineMode.Exclude, _core, _shadow) };
        core.SetBinding(Shape.FillProperty, ink);

        var rays = new GeometryGroup();
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            rays.Children.Add(new LineGeometry(
                new Point(8 + 5.2 * Math.Cos(angle), 8 + 5.2 * Math.Sin(angle)),
                new Point(8 + 6.9 * Math.Cos(angle), 8 + 6.9 * Math.Sin(angle))));
        }
        _rays = new Path
        {
            Data = rays, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = new TransformGroup { Children = { _raysScale, _raysTurn } },
        };
        _rays.SetBinding(Shape.StrokeProperty, ink);

        Children.Add(_rays);
        Children.Add(core);
    }

    /// <summary>Night when true. Changing it animates unless Windows has animations off.</summary>
    public bool Dark
    {
        get => _dark;
        set
        {
            if (_dark == value) return;
            _dark = value;
            bool animate = IsLoaded && SystemParameters.ClientAreaAnimation;
            var time = animate ? TimeSpan.FromMilliseconds(460) : TimeSpan.Zero;
            IEasingFunction ease = value ? new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
            double core = value ? MoonCore : SunCore;
            _core.BeginAnimation(EllipseGeometry.RadiusXProperty, new DoubleAnimation(core, time) { EasingFunction = ease });
            _core.BeginAnimation(EllipseGeometry.RadiusYProperty, new DoubleAnimation(core, time) { EasingFunction = ease });
            _shadow.BeginAnimation(EllipseGeometry.CenterProperty, new PointAnimation(value ? ShadowOver : ShadowAway, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _rays.BeginAnimation(OpacityProperty, new DoubleAnimation(value ? 0 : 1, animate ? TimeSpan.FromMilliseconds(value ? 220 : 380) : TimeSpan.Zero));
            _raysTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(value ? -45 : 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _raysScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(value ? 0.5 : 1, time));
            _raysScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(value ? 0.5 : 1, time));
        }
    }
}
