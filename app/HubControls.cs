using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Deskweave;

/// <summary>
/// An uppercase, letter-spaced group label (reference .grp, +0.07em). WPF has no letter-spacing
/// property; this draws each glyph on its own with the gap added between them, rather than faking
/// it with plain spaces.
/// </summary>
internal sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(11d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(TrackedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText), new FrameworkPropertyMetadata(0.07,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double TextFontSize { get => (double)GetValue(TextFontSizeProperty); set => SetValue(TextFontSizeProperty, value); }
    public Brush TextForeground { get => (Brush)GetValue(TextForegroundProperty); set => SetValue(TextForegroundProperty, value); }
    /// <summary>Extra space after each glyph, in em (font-size) units.</summary>
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        (double width, double height) = Layout();
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext context)
    {
        string text = (Text ?? "").ToUpperInvariant();
        double x = 0;
        foreach (char letter in text)
        {
            FormattedText glyph = Glyph(letter);
            context.DrawText(glyph, new Point(x, 0));
            x += glyph.WidthIncludingTrailingWhitespace + TextFontSize * Tracking;
        }
    }

    (double Width, double Height) Layout()
    {
        string text = (Text ?? "").ToUpperInvariant();
        if (text.Length == 0) return (0, TextFontSize * 1.3);
        double width = 0, height = 0;
        foreach (char letter in text)
        {
            FormattedText glyph = Glyph(letter);
            width += glyph.WidthIncludingTrailingWhitespace + TextFontSize * Tracking;
            height = Math.Max(height, glyph.Height);
        }
        return (Math.Max(0, width - TextFontSize * Tracking), height);
    }

    FormattedText Glyph(char letter)
    {
        FontFamily family = TryFindResource("SkinFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        return new FormattedText(letter.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, TextFontSize, TextForeground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}

/// <summary>Hides a group (its label and its list) when nothing is in it.</summary>
internal sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
