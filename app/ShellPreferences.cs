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
    public ShellPlacement? Full { get; init; }
    public ShellPlacement? Compact { get; init; }
    public bool Maximized { get; init; }
    public bool Topmost { get; init; }
    public string Mode { get; init; } = "full";
    public string? SelectedWorkspace { get; init; }
    public bool Focus { get; init; }
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

    internal void Restore(Window window, double minimumWidth, double minimumHeight)
    {
        if (!Valid) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => s.DeviceName == Display)
            ?? screens.FirstOrDefault(s => s.WorkingArea.Contains((int)Left, (int)Top))
            ?? System.Windows.Forms.Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var point = new NativePoint(work.Left + work.Width / 2, work.Top + work.Height / 2);
        try
        {
            nint monitor = MonitorFromPoint(point, 2);
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        double availableWidth = work.Width / scale;
        double availableHeight = work.Height / scale;
        window.MinWidth = Math.Min(minimumWidth, availableWidth);
        window.MinHeight = Math.Min(minimumHeight, availableHeight);
        window.Width = Math.Clamp(Width, window.MinWidth, availableWidth);
        window.Height = Math.Clamp(Height, window.MinHeight, availableHeight);
        int width = Math.Min(work.Width, (int)Math.Round(window.Width * scale));
        int height = Math.Min(work.Height, (int)Math.Round(window.Height * scale));
        int left = Math.Clamp((int)Math.Round(Left), work.Left, work.Right - width);
        int top = Math.Clamp((int)Math.Round(Top), work.Top, work.Bottom - height);
        SetWindowPos(handle, 0, left, top, width, height, 0x0004 | 0x0010); // no z-order or activation change
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] readonly struct NativePoint(int x, int y) { public readonly int X = x, Y = y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
