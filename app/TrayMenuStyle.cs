using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Deskweave;

/// <summary>
/// The tray menu in the app's own colours and spacing. The stock WinForms menu kept a grey icon
/// strip down its left for icons it has none of, stayed light in a dark app, and looked like a
/// different program's. Colours are the Theme tokens (AppearanceManager) flattened onto
/// the card colour, read at paint time so a theme change reaches the next opening.
/// </summary>
internal static class TrayMenuStyle
{
    internal static void Apply(ContextMenuStrip menu)
    {
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Renderer = new Renderer();
        menu.Font = new Font("Segoe UI", 9f);
        menu.Padding = new Padding(4, 4, 4, 4);
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem) { item.Padding = new Padding(6, 5, 18, 5); item.AutoSize = true; }
    }

    static bool Dark => AppearanceManager.Dark;
    static Color Surface => Dark ? Color.FromArgb(0x23, 0x28, 0x31) : Color.White;
    static Color Ink => Dark ? Color.FromArgb(0xEE, 0xF1, 0xF6) : Color.FromArgb(0x15, 0x18, 0x1D);
    static Color Faint => Dark ? Color.FromArgb(0x6E, 0x77, 0x87) : Color.FromArgb(0x8E, 0x96, 0xA3);
    // HoverBrush, HairlineBrush and ControlBorderBrush are translucent; these are them over Surface.
    static Color Hover => Dark ? Color.FromArgb(0x32, 0x37, 0x3F) : Color.FromArgb(0xF1, 0xF2, 0xF3);
    static Color Hairline => Dark ? Color.FromArgb(0x31, 0x36, 0x3E) : Color.FromArgb(0xE9, 0xEA, 0xEC);
    static Color Edge => Dark ? Color.FromArgb(0x41, 0x45, 0x4D) : Color.FromArgb(0xD9, 0xDA, 0xDD);

    sealed class Renderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(Surface);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Edge);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var box = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = Rounded(box, 4);
            using var fill = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Hairline);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        static GraphicsPath Rounded(Rectangle box, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(box.X, box.Y, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
