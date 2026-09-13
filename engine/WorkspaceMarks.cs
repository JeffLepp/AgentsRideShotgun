using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// What a model is actually sent: how big the picture is, and whether the things it can act on are
/// labelled on it.
///
/// Both are token questions before they are accuracy questions. Claude charges an image by 28x28
/// patches - ceil(w/28) * ceil(h/28) visual tokens - so a 1920x1080 screenshot is 2,691 tokens and
/// the same screen at 1280x720 is 1,196. Anthropic's computer-use guidance recommends 1024x768 or
/// 1280x720 for desktop work and warns against sending anything above 1920x1080; a tool_result
/// image over the model's limit is rejected outright rather than resized for us, so the resizing
/// has to happen here.
/// </summary>
internal static class WorkspaceMarks
{
    /// <summary>What an action group's picture is capped at. A deliberate `look` is not capped.</summary>
    internal const int ActionWidth = 1280, ActionHeight = 720;

    /// <summary>
    /// The size a source of this shape is sent at, and the factor between the two. Never scales up:
    /// a screen already smaller than the cap is sent as it is, at scale 1.
    /// </summary>
    internal static (int Width, int Height, double Scale) Fit(int width, int height, int capWidth, int capHeight)
    {
        if (width <= 0 || height <= 0) return (width, height, 1);
        double scale = Math.Min(1, Math.Min((double)capWidth / width, (double)capHeight / height));
        if (scale >= 1) return (width, height, 1);
        // At least one pixel each way, and never larger than the cap after rounding.
        int scaled(int side, int cap) => Math.Clamp((int)Math.Round(side * scale), 1, cap);
        return (scaled(width, capWidth), scaled(height, capHeight), scale);
    }

    /// <summary>
    /// A point the model gave, in the picture it was looking at, put back into the real coordinates
    /// the desktop and the browser use. Sizes and scroll amounts are left alone: they are distances
    /// rather than places, and the input layers already take them in their own units.
    /// </summary>
    internal static int ToSource(int value, double scale) =>
        scale >= 1 ? value : (int)Math.Round(value / scale);

    internal static ComputerAction ToSource(ComputerAction action, double scale)
    {
        if (scale >= 1) return action;
        return action with
        {
            X = ToSource(action.X, scale),
            Y = ToSource(action.Y, scale),
            Path = action.Path?.Select(point =>
                new ComputerPoint(ToSource(point.X, scale), ToSource(point.Y, scale))).ToArray(),
        };
    }

    /// <summary>Redraws a frame at the size it is being sent at. Returns the original when it fits.</summary>
    internal static BitmapSource Resize(BitmapSource frame, int width, int height)
    {
        if (frame.PixelWidth == width && frame.PixelHeight == height) return frame;
        var scaled = new TransformedBitmap(frame,
            new ScaleTransform((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Set-of-Mark: the controls the window publishes, drawn onto the picture as numbered boxes.
    /// The numbers are the same ones `controls`, `press`, `write` and `batch` already use, so a
    /// model that can see "5" on the OK button can press 5 rather than estimating a pixel - which is
    /// the grounding that overlaying numbered boxes was shown to buy in the Set-of-Mark and
    /// OmniParser work, without this having to guess at what is interactive: Windows already says.
    ///
    /// <paramref name="origin"/> is the screen point the picture's top-left corner is, so a shot of
    /// one window marks correctly; <paramref name="scale"/> is the factor the picture was sent at.
    /// </summary>
    internal static BitmapSource Draw(BitmapSource frame, IReadOnlyList<WorkspaceElement> elements,
        Point origin, double scale)
    {
        if (elements.Count == 0) return frame;
        var visual = new DrawingVisual();
        using (DrawingContext canvas = visual.RenderOpen())
        {
            canvas.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            var line = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), 2);
            line.Freeze();
            var label = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            label.Freeze();
            var ink = Brushes.White;
            foreach (WorkspaceElement element in elements)
            {
                double x = (element.X - origin.X) * scale, y = (element.Y - origin.Y) * scale;
                double width = element.Width * scale, height = element.Height * scale;
                if (width < 4 || height < 4) continue;
                if (x + width < 0 || y + height < 0 || x > frame.PixelWidth || y > frame.PixelHeight) continue;
                canvas.DrawRectangle(null, line, new Rect(x, y, width, height));

                var text = new FormattedText(element.Id.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, ink, 1.0);
                // Inside the box, so a badge never covers the control next to it.
                var badge = new Rect(x + 1, y + 1, text.Width + 6, text.Height + 2);
                canvas.DrawRectangle(label, null, badge);
                canvas.DrawText(text, new Point(badge.X + 3, badge.Y + 1));
            }
        }
        var marked = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        marked.Render(visual);
        marked.Freeze();
        return marked;
    }
}
