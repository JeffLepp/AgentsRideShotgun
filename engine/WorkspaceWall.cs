using System.Runtime.InteropServices;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// What an agent's screen shows behind its windows. Full desktop (the default): a soft wall,
/// two radial glows over a base, in the app's light or dark theme, so the
/// corner shows something that looks like a PC rather than a flat fill. Simple: the flat fill.
/// Computed once per screen size and theme off the capture path, then copied under every whole-screen
/// frame; there is no timer and no extra process. The owner's own wallpaper is deliberately not used: agents send
/// their screenshots to their providers, and a wallpaper can be personal.
/// </summary>
internal static class WorkspaceWall
{
    static readonly Lock Gate = new();
    // One bitmap per screen size and theme, so switching theme doesn't recompute; a handful at most.
    static readonly Dictionary<(int Width, int Height, bool Dark), nint> Made = [];
    static readonly HashSet<(int, int, bool)> Making = [];

    /// <summary>Fills a capture canvas before its windows are drawn on it. Never waits: a size or
    /// theme seen for the first time is built on a pool thread, and the frames until then get its
    /// base colour.</summary>
    internal static void Paint(nint canvasDc, nint screenDc, int width, int height)
    {
        var whole = new Native.Rect { Right = width, Bottom = height };
        bool simple = AppSettingsStore.Current.AgentScreen == AgentScreenLook.Simple;
        bool dark = !simple && Dark();
        var key = (width, height, dark);
        lock (Gate)
        {
            if (!simple && Made.TryGetValue(key, out nint wall))
            {
                nint source = Native.CreateCompatibleDC(screenDc);
                nint previous = Native.SelectObject(source, wall);
                Native.BitBlt(canvasDc, 0, 0, width, height, source, 0, 0, Native.SrcCopy);
                Native.SelectObject(source, previous);
                Native.DeleteDC(source);
                return;
            }
            if (!simple && Making.Add(key)) _ = Task.Run(() => Build(key));
        }
        Native.SetBkColor(canvasDc, simple ? 0x00201A14 : dark ? 0x00221410 : 0x00F5ECE6);
        Native.ExtTextOutW(canvasDc, 0, 0, Native.EtoOpaque, ref whole, null, 0, 0);
    }

    static void Build((int Width, int Height, bool Dark) key)
    {
        nint screenDc = Native.GetDC(0);
        try
        {
            nint bitmap = Make(screenDc, key.Width, key.Height, key.Dark);
            lock (Gate)
            {
                if (Made.Count >= 4) { foreach (nint old in Made.Values) Native.DeleteObject(old); Made.Clear(); }
                Made[key] = bitmap;
                Making.Remove(key);
            }
        }
        finally { Native.ReleaseDC(0, screenDc); }
    }

    /// <summary>The app's theme: Settings' choice, or Windows' own app setting when it follows Windows.</summary>
    static bool Dark() => AppSettingsStore.Current.Theme switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,
        _ => Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };

    static nint Make(nint screenDc, int width, int height, bool dark)
    {
        byte[] pixels = Pixels(width, height, dark);
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        nint bitmap = Native.CreateCompatibleBitmap(screenDc, width, height);
        SetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref info, 0);
        return bitmap;
    }

    /// <summary>
    /// The wall: a glow at 16%/18% (1100x700 at a 1440-wide screen) and one at
    /// 88%/86% (900x700), each fading to nothing at 60% of its radius, over a base colour.
    /// </summary>
    internal static byte[] Pixels(int width, int height, bool dark)
    {
        (byte R, byte G, byte B) baseColor = dark ? ((byte)0x0E, (byte)0x14, (byte)0x22) : ((byte)0xE6, (byte)0xEC, (byte)0xF5);
        (byte R, byte G, byte B) first = dark ? ((byte)0x1C, (byte)0x2E, (byte)0x52) : ((byte)0xC3, (byte)0xD4, (byte)0xF1);
        (byte R, byte G, byte B) second = dark ? ((byte)0x18, (byte)0x26, (byte)0x4A) : ((byte)0xD2, (byte)0xDD, (byte)0xF3);
        double scale = width / 1440.0;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double r = baseColor.R, g = baseColor.G, b = baseColor.B;
                Blend(ref r, ref g, ref b, second, Glow(x, y, width * 0.88, height * 0.86, 900 * scale, 700 * scale));
                Blend(ref r, ref g, ref b, first, Glow(x, y, width * 0.16, height * 0.18, 1100 * scale, 700 * scale));
                // A 4x4 ordered dither: the glows change by less than one step over many pixels, and
                // rounding alone draws visible rings. Deterministic, so every frame is identical.
                double d = Bayer[(y & 3) * 4 + (x & 3)] / 16.0 - 0.47;
                int i = (y * width + x) * 4;
                pixels[i] = Channel(b + d); pixels[i + 1] = Channel(g + d); pixels[i + 2] = Channel(r + d); pixels[i + 3] = 255;
            }
        return pixels;
    }

    static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    static double Glow(double x, double y, double cx, double cy, double rx, double ry)
    {
        double d = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2));
        return d >= 0.6 ? 0 : 1 - d / 0.6;
    }

    static void Blend(ref double r, ref double g, ref double b, (byte R, byte G, byte B) over, double alpha)
    {
        r += (over.R - r) * alpha; g += (over.G - g) * alpha; b += (over.B - b) * alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll")]
    static extern int SetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
