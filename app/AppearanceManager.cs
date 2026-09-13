using System.Windows;
using System.Windows.Media;

namespace Deskweave;

internal enum AppearanceSkin { Windows, Mac, Linux }

/// <summary>Appearance is independent of the workspace's host and execution environment.</summary>
internal static class AppearanceManager
{
    internal static AppearanceSkin Current { get; private set; } = Default;
    internal static AppearanceSkin Default => OperatingSystem.IsMacOS() ? AppearanceSkin.Mac
        : OperatingSystem.IsLinux() ? AppearanceSkin.Linux : AppearanceSkin.Windows;

    internal static AppearanceSkin Resolve(string? saved) =>
        Enum.TryParse(saved, ignoreCase: true, out AppearanceSkin skin) && Enum.IsDefined(skin) ? skin : Default;

    internal static void Apply(AppearanceSkin skin)
    {
        Application application = Application.Current ?? throw new InvalidOperationException("Appearance requires the app dispatcher.");
        application.Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(skin)) skin = Default;
        Current = skin;
        ResourceDictionary resources = application.Resources;
        Palette palette = skin switch
        {
            AppearanceSkin.Mac => new("#F0F2F6", "#FFFFFF", "#E5E9F1", "#DCE2ED", "#DDE7FF", "#CCD2DE",
                "#202631", "#535E71", "#2456D9", "#FFFFFF", "#1947C4", "#216753", "#7C5A1C", "#AE3544", "#586476",
                "#FAFBFD", "#E3E8F2", "#E8ECF3", "#F7F9FD", "#E6EBF3", "#F3DDE0", "#912B37", 10, 14, 12),
            AppearanceSkin.Linux => new("#101719", "#192326", "#223033", "#2A3A3D", "#293A2C", "#354447",
                "#F1F4F1", "#AFBDB9", "#B5E879", "#142014", "#C8F39A", "#B5E879", "#E6BF70", "#F29A92", "#A0B2AB",
                "#182224", "#111A1C", "#101719", "#152023", "#111B1D", "#412C2C", "#F8B7AD", 4, 6, 0),
            _ => new("#11151D", "#1A202B", "#252D3B", "#2D3849", "#243653", "#343E50",
                "#F3F6FC", "#ADB8C9", "#8FB7FF", "#102449", "#B0CDFF", "#93DCC4", "#EDCE91", "#FFAAA6", "#9EAABE",
                "#212938", "#161C27", "#10141C", "#19212F", "#101722", "#402A35", "#FFB9BB", 6, 9, 8)
        };
        Set("ShellSurfaceBrush", palette.Surface);
        Set("ShellPanelBrush", palette.Panel);
        Set("ShellChipBrush", palette.Chip);
        Set("ShellHoverBrush", palette.Hover);
        Set("ShellSelectedBrush", palette.Selected);
        Set("ShellBorderBrush", palette.Border);
        Set("ShellDividerBrush", palette.Border);
        Set("ShellTextBrush", palette.Text);
        Set("ShellInkBrush", palette.Text);
        Set("ShellMutedBrush", palette.Muted);
        Set("ShellHintBrush", palette.Muted);
        Set("ShellIconBrush", palette.Muted);
        Set("ShellAccentBrush", palette.Accent);
        Set("OnAccentBrush", palette.OnAccent);
        Set("ShellAccentHoverBrush", palette.AccentHover);
        Set("ShellPreviewBrush", palette.Preview);
        Set("ShellPreviewOverlayBrush", palette.Surface);
        Set("TDangerBrush", palette.DangerSurface);
        Set("TDangerInkBrush", palette.DangerInk);
        Set("AWWorkingBrush", palette.Working);
        Set("AWWaitingBrush", palette.Waiting);
        Set("AWFailedBrush", palette.Failed);
        Set("AWAccentBrush", palette.Accent);
        Set("AWIdleBrush", palette.Idle);
        resources["TAccentColor"] = Parse(palette.Accent);
        resources["TLiveColor"] = Parse(palette.Working);
        resources["THoneyColor"] = Parse(palette.Waiting);
        resources["TDangerColor"] = Parse(palette.Failed);
        resources["TDotOffColor"] = Parse(palette.Idle);
        resources["ShellChromeBrush"] = Gradient(palette.ChromeStart, palette.ChromeEnd);
        resources["ShellCanvasBrush"] = Gradient(palette.CanvasStart, palette.CanvasEnd);
        resources["SkinControlRadius"] = new CornerRadius(palette.ControlRadius);
        resources["SkinPanelRadius"] = new CornerRadius(palette.PanelRadius);
        resources["SkinWindowRadius"] = new CornerRadius(palette.WindowRadius);
        // Use installed system fonts. This Windows executable does not bundle Apple's fonts.
        resources["SkinFontFamily"] = new FontFamily(skin == AppearanceSkin.Windows ? "Segoe UI Variable Text, Segoe UI" : "Segoe UI");

        void Set(string key, string value)
        {
            var brush = new SolidColorBrush(Parse(value));
            brush.Freeze();
            resources[key] = brush;
        }
    }

    static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    static Brush Gradient(string start, string end)
    {
        var brush = new LinearGradientBrush(Parse(start), Parse(end), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    sealed record Palette(string Surface, string Panel, string Chip, string Hover, string Selected, string Border,
        string Text, string Muted, string Accent, string OnAccent, string AccentHover, string Working, string Waiting,
        string Failed, string Idle, string ChromeStart, string ChromeEnd, string CanvasStart, string CanvasEnd,
        string Preview, string DangerSurface, string DangerInk, double ControlRadius, double PanelRadius, double WindowRadius);
}
