using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deskweave.AgentWorkspaces;

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
///
/// Reconsidered 2026-09-22, when an agent found the two sizes (this cap, and look at full size) a
/// tax to keep apart: the cap stays. Opus 4.7 and later read up to 2576 px with coordinates 1:1, but
/// the full screen still costs 2.25 times as many tokens, and Haiku 4.5 and Sonnet 4.6 shrink
/// anything past 1568 px on their side - a scale nobody here would know to map clicks back
/// through. What removed the tax instead is not needing pixels for anything numbered: computer
/// takes a control number wherever it takes x and y.
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
            Rect Box(WorkspaceElement element) => new((element.X - origin.X) * scale, (element.Y - origin.Y) * scale,
                element.Width * scale, element.Height * scale);
            static bool Acts(WorkspaceElement element) =>
                element.Does.Split('/').Any(verb => verb is "press" or "toggle" or "expand" or "select");
            // The label or icon inside a button or a list row is the button: WPF publishes it as an
            // element of its own, and its badge sat on the very label that says what the button is.
            bool Inside(WorkspaceElement element) => element.Type is "Text" or "Image" && !Acts(element)
                && elements.Any(other => !ReferenceEquals(other, element) && Acts(other) && Box(other).Contains(Box(element)));
            foreach (WorkspaceElement element in elements)
            {
                if (Inside(element)) continue;
                Rect box = Box(element);
                double x = box.X, y = box.Y, width = box.Width, height = box.Height;
                if (width < 4 || height < 4) continue;
                if (x + width < 0 || y + height < 0 || x > frame.PixelWidth || y > frame.PixelHeight) continue;
                canvas.DrawRectangle(null, line, box);

                // Inside the box, so a badge never covers the control next to it. The number shrinks
                // with the outline rather than keeping a fixed size: once marks are drawn on the
                // capped picture instead of the whole screen, a scrollbar arrow's box is a dozen
                // pixels across, and a 12pt badge on it spilled over the arrows either side - three
                // in a row wore each other's numbers. Seven is the smallest that still reads.
                double point = Math.Clamp(Math.Min(width - 2, height - 2) * 0.8, 7, 12);
                var text = new FormattedText(element.Id.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), point, ink, 1.0);
                var badge = new Rect(x + 1, y + 1,
                    Math.Min(text.Width + 6, Math.Max(1, width - 2)),
                    Math.Min(text.Height + 2, Math.Max(1, height - 2)));
                // On a short control - a button, a field - the badge covered the start of its own
                // label, and one whose app names nothing read "106 e selected" for Remove selected
                // (a Tk app, 2026-09-22). It moves just above the outline when that spot is empty:
                // not over another control, only over something that holds this one. A stacked list
                // has its neighbour there, so its rows keep their badges inside.
                if (height < 34 && text.Width + 6 <= width + 2)
                {
                    var above = new Rect(x, y - text.Height - 2, text.Width + 6, text.Height + 2);
                    // One pixel short of the outline, so a control that merely touches it - the
                    // label inside this very button, flush with its top - does not count as there.
                    var clear = new Rect(above.X, above.Y, above.Width, Math.Max(0, above.Height - 1));
                    bool free = above.Y >= 0 && !elements.Any(other =>
                    {
                        if (ReferenceEquals(other, element)) return false;
                        Rect them = Box(other);
                        return !them.Contains(box) && !box.Contains(them) && them.IntersectsWith(clear);
                    });
                    if (free) badge = above;
                }
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
