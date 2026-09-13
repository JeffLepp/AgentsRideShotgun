using System.IO;
using System.Windows;
using System.Windows.Media;
using HiveMind.AgentWorkspaces;
using Microsoft.Win32;

namespace Deskweave;

/// <summary>
/// Light and dark, with the exact tokens from design/MVP_SPEC.md. Follows Windows' app theme
/// unless Settings > General > Theme forces one. Every brush is replaced in the application
/// resources, so anything bound with DynamicResource repaints in place; no window, workspace or
/// agent restarts.
/// </summary>
internal static class AppearanceManager
{
    // Name, light, dark. Alpha is the first byte: #17141E32 is rgba(20,30,50,.09).
    static readonly (string Key, string Light, string Dark)[] Tokens =
    [
        ("WindowBrush", "#F7F8FA", "#1B1F26"),
        ("SidebarBrush", "#F1F3F6", "#171A20"),
        ("CardBrush", "#FFFFFF", "#232831"),
        ("InkBrush", "#15181D", "#EEF1F6"),
        ("MutedInkBrush", "#5E6674", "#9DA6B5"),
        ("FaintInkBrush", "#8E96A3", "#6E7787"),
        ("HairlineBrush", "#17141E32", "#12FFFFFF"),
        ("ControlBorderBrush", "#29141E32", "#24FFFFFF"),
        ("AccentBrush", "#2E6BF6", "#7EA6FF"),
        ("AccentHoverBrush", "#2459D6", "#9BBBFF"),
        ("OnAccentBrush", "#FFFFFF", "#0B1A3A"),
        ("AccentSoftBrush", "#1A2E6BF6", "#217EA6FF"),
        ("NeedsYouBrush", "#E09A2B", "#F0B455"),
        ("NeedsYouInkBrush", "#A8650A", "#F0B455"),
        ("NeedsYouSoftBrush", "#21E09A2B", "#24F0B455"),
        ("AsleepBrush", "#B0B7C3", "#586070"),
        ("GlassBrush", "#E0FAFBFD", "#DB1E222A"),
        ("GlassEdgeBrush", "#F2FFFFFF", "#14FFFFFF"),
        ("ToggleOffBrush", "#8E96A3", "#6E7787"),
        ("HoverBrush", "#0F141E32", "#12FFFFFF"),
        ("PressedBrush", "#1A141E32", "#1CFFFFFF"),
        ("ChipBrush", "#F1F3F6", "#2A303A"),
        ("PreviewBrush", "#EEF1F5", "#20252D"),
        ("DangerBrush", "#C42B1C", "#E0564A"),
        ("DangerInkBrush", "#B42318", "#F5A3A0"),
        ("DangerSoftBrush", "#FBE9E9", "#3A2326"),
        ("AccentColor", "#2E6BF6", "#7EA6FF"),
        ("NeedsYouColor", "#E09A2B", "#F0B455"),
        ("DangerColor", "#C42B1C", "#E0564A"),
        ("AsleepColor", "#B0B7C3", "#586070"),
    ];

    // The engine's pages and the pre-MVP shell still use these names; they get the same values.
    static readonly (string Alias, string Of)[] Aliases =
    [
        ("ShellSurfaceBrush", "WindowBrush"), ("ShellCanvasBrush", "WindowBrush"), ("ShellChromeBrush", "WindowBrush"),
        ("ShellPanelBrush", "CardBrush"), ("ShellChipBrush", "ChipBrush"), ("ShellHoverBrush", "HoverBrush"),
        ("ShellSelectedBrush", "AccentSoftBrush"), ("ShellBorderBrush", "HairlineBrush"), ("ShellDividerBrush", "HairlineBrush"),
        ("ShellTextBrush", "InkBrush"), ("ShellInkBrush", "InkBrush"), ("ShellMutedBrush", "MutedInkBrush"),
        ("ShellHintBrush", "MutedInkBrush"), ("ShellIconBrush", "MutedInkBrush"), ("ShellAccentBrush", "AccentBrush"),
        ("ShellAccentHoverBrush", "AccentHoverBrush"), ("ShellPreviewBrush", "PreviewBrush"), ("ShellPreviewOverlayBrush", "GlassBrush"),
        ("TDangerBrush", "DangerSoftBrush"), ("TDangerInkBrush", "DangerInkBrush"),
        ("AWWorkingBrush", "AccentBrush"), ("AWWaitingBrush", "NeedsYouBrush"), ("AWFailedBrush", "DangerBrush"),
        ("AWAccentBrush", "AccentBrush"), ("AWIdleBrush", "AsleepBrush"),
        // Engine/Palette.xaml builds its own state brushes from these colors.
        ("TAccentColor", "AccentColor"), ("TLiveColor", "AccentColor"), ("THoneyColor", "NeedsYouColor"),
        ("TDangerColor", "DangerColor"), ("TDotOffColor", "AsleepColor"),
    ];

    static bool _listening;

    /// <summary>What Settings asked for.</summary>
    internal static ThemeChoice Choice { get; private set; }

    /// <summary>What is on screen now.</summary>
    internal static bool Dark { get; private set; }

    /// <summary>After every repaint, on the UI thread. For code that draws with brushes it looked up.</summary>
    internal static event Action? Changed;

    internal static void Apply(ThemeChoice choice)
    {
        Application application = Application.Current ?? throw new InvalidOperationException("Appearance requires the app dispatcher.");
        application.Dispatcher.VerifyAccess();
        Choice = Enum.IsDefined(choice) ? choice : ThemeChoice.FollowWindows;
        Dark = Choice == ThemeChoice.Dark || Choice == ThemeChoice.FollowWindows && WindowsIsDark();
        ResourceDictionary resources = application.Resources;
        foreach (var (key, light, dark) in Tokens)
        {
            var color = (Color)ColorConverter.ConvertFromString(Dark ? dark : light);
            if (key.EndsWith("Color", StringComparison.Ordinal)) { resources[key] = color; continue; }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
        foreach (var (alias, of) in Aliases) resources[alias] = resources[of];
        if (!_listening) Listen(application);
        Changed?.Invoke();
    }

    /// <summary>Windows' own setting for apps: AppsUseLightTheme 0 is dark. Missing means light.</summary>
    internal static bool WindowsIsDark()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { return false; }
    }

    static void Listen(Application application)
    {
        _listening = true;
        SystemEvents.UserPreferenceChanged += WindowsChanged;
        AppSettingsStore.Changed += SettingsChanged;
        application.Exit += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= WindowsChanged;
            AppSettingsStore.Changed -= SettingsChanged;
        };
    }

    // Windows raises this on its own thread for every kind of preference; the app theme is General.
    static void WindowsChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Choice == ThemeChoice.FollowWindows && WindowsIsDark() != Dark) Apply(Choice);
        });
    }

    // Settings only writes the store; the theme follows it from here.
    static void SettingsChanged(AppSettings settings) =>
        Application.Current?.Dispatcher.BeginInvoke(() => { if (settings.Theme != Choice) Apply(settings.Theme); });
}
