using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HiveMind.Product;

namespace Deskweave;

/// <summary>Small, product-owned shell state. Workspace records remain owned by the engine.</summary>
internal sealed record ShellPreferences
{
    public int Schema { get; init; } = 1;
    public ShellPlacement? Stack { get; init; }
    public ShellPlacement? Wide { get; init; }
    public bool Maximized { get; init; }
    /// <summary>"stack" (340 wide) or "wide" (1200, sidebar and a page).</summary>
    public string Mode { get; init; } = "stack";
    public string? SelectedWorkspace { get; init; }
    static string? _testFile;
    static string FilePath => _testFile ?? ProductContext.Local("shell.json");

    public static ShellPreferences Read()
    {
        try
        {
            string file = FilePath;
            if (!File.Exists(file) || new FileInfo(file).Length > 32_768) return new();
            ShellPreferences? preferences = JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(file));
            return preferences is { Schema: 1 } ? preferences : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public bool Save()
    {
        string path = FilePath;
        string temporary = path + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static IDisposable UseFileForTests(string path)
    {
        if (_testFile is not null) throw new InvalidOperationException("A shell preferences override is already active.");
        _testFile = Path.GetFullPath(path);
        return new FileScope();
    }
    sealed class FileScope : IDisposable { public void Dispose() => _testFile = null; }
}

/// <summary>
/// Position is in native screen pixels; size is in DIPs. Native placement keeps mixed-DPI screens
/// from interpreting one monitor's saved position through another monitor's scale.
/// </summary>
internal sealed record ShellPlacement(double Left, double Top, double Width, double Height, string Display)
{
    internal static ShellPlacement Capture(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out NativeRect bounds);
        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        return new(bounds.Left, bounds.Top, window.ActualWidth, window.ActualHeight, screen.DeviceName);
    }

    internal bool Valid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width > 0 && Height > 0 && Math.Abs(Left) < 1_000_000 && Math.Abs(Top) < 1_000_000;

    /// <summary>Puts the window here, fitted to that monitor. Moves and sizes it in one step before
    /// its size limits change, so switching stack/wide never grows it past the monitor's edge, not
    /// even for a frame. False when there is nothing to place yet.</summary>
    internal bool Restore(Window window, double minimumWidth, double minimumHeight)
    {
        if (!Valid) return false;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => s.DeviceName == Display)
            ?? screens.FirstOrDefault(s => s.WorkingArea.Contains((int)Left, (int)Top))
            ?? Home();
        var work = screen.WorkingArea;
        double scale = ScaleAt(new NativePoint(work.Left + work.Width / 2, work.Top + work.Height / 2), window);
        double availableWidth = work.Width / scale;
        double availableHeight = work.Height / scale;
        double minWidth = Math.Min(minimumWidth, availableWidth), minHeight = Math.Min(minimumHeight, availableHeight);
        double dipWidth = Math.Clamp(Width, minWidth, availableWidth), dipHeight = Math.Clamp(Height, minHeight, availableHeight);
        int width = Math.Min(work.Width, (int)Math.Round(dipWidth * scale));
        int height = Math.Min(work.Height, (int)Math.Round(dipHeight * scale));
        int left = Math.Clamp((int)Math.Round(Left), work.Left, work.Right - width);
        int top = Math.Clamp((int)Math.Round(Top), work.Top, work.Bottom - height);
        // Lowering a limit never resizes; a higher one left in place would clamp the move below.
        window.MinWidth = Math.Min(window.MinWidth, minWidth);
        window.MinHeight = Math.Min(window.MinHeight, minHeight);
        SetWindowPos(handle, 0, left, top, width, height, 0x0004 | 0x0010); // no z-order or activation change
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.Width = dipWidth;
        window.Height = dipHeight;
        return true;
    }

    /// <summary>The monitor a window goes to when it has no saved place: the primary one. The UI
    /// probe points it at another monitor, so a test run leaves the owner's screen alone.</summary>
    internal static Func<System.Windows.Forms.Screen> Home = () => System.Windows.Forms.Screen.PrimaryScreen!;

    /// <summary>Where the reference puts the stack by default: 24 from the top and right of the
    /// primary monitor's work area, 340 wide, the work area's height minus 48.</summary>
    internal static ShellPlacement DefaultStack()
    {
        var screen = Home();
        var work = screen.WorkingArea;
        double scale = ScaleAt(new NativePoint(work.Left + work.Width / 2, work.Top + work.Height / 2), null);
        double width = 340;
        double height = Math.Max(480, work.Height / scale - 48);
        double leftPx = work.Right - width * scale - 24 * scale;
        double topPx = work.Top + 24 * scale;
        return new ShellPlacement(leftPx, topPx, width, height, screen.DeviceName);
    }

    /// <summary>1200 x 826, centered on the primary monitor; fitted with 20 DIP margins on a
    /// smaller work area.</summary>
    internal static ShellPlacement DefaultWide()
    {
        var screen = Home();
        var work = screen.WorkingArea;
        double scale = ScaleAt(new NativePoint(work.Left + work.Width / 2, work.Top + work.Height / 2), null);
        double width = Math.Min(1200, Math.Max(960, work.Width / scale - 40));
        double height = Math.Min(826, Math.Max(600, work.Height / scale - 40));
        double leftPx = work.Left + (work.Width - width * scale) / 2;
        double topPx = work.Top + (work.Height - height * scale) / 2;
        return new ShellPlacement(leftPx, topPx, width, height, screen.DeviceName);
    }

    static double ScaleAt(NativePoint point, Window? window)
    {
        double scale = window is not null ? VisualTreeHelper.GetDpi(window).DpiScaleX : 1.0;
        try
        {
            nint monitor = MonitorFromPoint(point, 2);
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return scale;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal readonly struct NativePoint(int x, int y) { public readonly int X = x, Y = y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
