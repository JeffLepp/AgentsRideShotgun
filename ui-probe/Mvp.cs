using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// One state of the real app, photographed in light and dark and set beside the same crop of its
/// reference image (ui-probe/reference/{light,dark}/{Reference}.png at x, y, width, height). Put it
/// on a static method in a Scenes.*.cs file: the method gets a <see cref="SceneContext"/>, builds
/// the state, and returns the window or element to photograph at its real size. A Reference of ""
/// photographs a state no reference image covers.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class SceneAttribute(string name, string reference, int x = 0, int y = 0, int width = 0, int height = 0) : Attribute
{
    public string Name => name;
    public string Reference => reference;
    public Int32Rect Crop => new(x, y, width, height);
}

/// <summary>What a scene can use. Windows it <see cref="Own"/>s and workspaces it makes are gone when it ends.</summary>
sealed class SceneContext(string references) : IDisposable
{
    static readonly Dictionary<string, BitmapSource> Sites = [];
    readonly List<Window> _windows = [];
    readonly List<string> _workspaces = [];

    /// <summary>Right of every screen: a window there lays out and renders but is never seen.</summary>
    public static Point OffScreen => new(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 400, 40);

    public T Own<T>(T window) where T : Window { _windows.Add(window); return ProbeWindow.OffScreen(window); }

    /// <summary>A stored workspace, with no computer running.</summary>
    public StoredWorkspace Workspace(string name)
    {
        StoredWorkspace workspace = WorkspaceStore.Create(name);
        _workspaces.Add(workspace.Id);
        return workspace;
    }

    /// <summary>One of the reference's mini websites (shop, blog, docs, term), 1280x800, to stand in
    /// for a workspace's screen. Views must take a frame from a model a scene can fill, so no
    /// scene needs a running desktop.</summary>
    public BitmapSource Site(string name)
    {
        if (Sites.TryGetValue(name, out BitmapSource? known)) return known;
        return Sites[name] = Mvp.Load(Path.Combine(references, "sites", name + ".png"));
    }

    public Task Settle(int milliseconds = 350) => Task.Delay(milliseconds);

    public void Dispose()
    {
        foreach (Window window in _windows) window.Close();
        foreach (string id in _workspaces) WorkspaceStore.Delete(id);
    }
}

/// <summary>
/// The reference-screen harness. For every scene, in light then
/// dark: {name}.png is the real app, {name}.compare.png is reference | app over gray | difference
/// (red is different), and mvp-report.json gives the size error and how much differs.
/// </summary>
static class Mvp
{
    static readonly ThemeChoice[] Themes = [ThemeChoice.Light, ThemeChoice.Dark];

    internal static async Task Run(string output, string? only)
    {
        string references = References();
        var scenes = typeof(Mvp).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(method => (Method: method, Scene: method.GetCustomAttribute<SceneAttribute>()))
            .Where(found => found.Scene is not null
                && (only is null || found.Scene.Name.StartsWith(only, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(found => found.Scene!.Name, StringComparer.Ordinal)
            .ToArray();
        if (scenes.Length == 0) throw new InvalidOperationException("No scene is named " + (only ?? "anything") + ".");
        var rows = new List<object>();
        // DESKWEAVE_SCENE_SCALE=2 photographs every scene at twice the pixels, for crisp pictures.
        double scale = double.TryParse(Environment.GetEnvironmentVariable("DESKWEAVE_SCENE_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double asked) && asked > 0 ? asked : 1;
        foreach (ThemeChoice theme in Themes)
        {
            // Through the store too: the theme follows the store, so a scene that saves any setting
            // would otherwise snap the painted theme back to Windows' own.
            AppSettingsStore.Update(settings => settings with { Theme = theme });
            AppearanceManager.Apply(theme);
            string name = theme.ToString().ToLowerInvariant();
            string folder = Path.Combine(output, name);
            Directory.CreateDirectory(folder);
            foreach (var (method, scene) in scenes)
            {
                using var context = new SceneContext(references);
                var element = await (Task<FrameworkElement>)method.Invoke(null, [context])!;
                // Display text is snapped to 1x pixels and turns jagged when scaled up.
                if (scale != 1) TextOptions.SetTextFormattingMode(element, TextFormattingMode.Ideal);
                await context.Settle();
                BitmapSource shot = Photograph(element, scale);
                Save(shot, Path.Combine(folder, scene!.Name + ".png"));
                // A scaled shot is for sharp README pictures, not for comparing with a 1x reference.
                rows.Add(scene.Reference.Length == 0 || scale != 1
                    ? new { scene = scene.Name, theme = name, captured = new { width = shot.PixelWidth, height = shot.PixelHeight } }
                    : Compare(scene, name, shot, Path.Combine(references, name, scene.Reference + ".png"),
                        Path.Combine(folder, scene.Name + ".compare.png")));
            }
        }
        File.WriteAllText(Path.Combine(output, "mvp-report.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>At 96 DPI, so one DIP is one pixel and sizes compare directly with the references.
    /// A <paramref name="scale"/> above 1 keeps the DIPs and adds device pixels, which is what a
    /// 125% or 150% monitor does with the same layout (Scenes.Scaling.cs).</summary>
    internal static BitmapSource Photograph(FrameworkElement element, double scale = 1)
    {
        element.UpdateLayout();
        double across = Math.Max(1, element.ActualWidth), down = Math.Max(1, element.ActualHeight);
        var image = new RenderTargetBitmap((int)Math.Round(across * scale), (int)Math.Round(down * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (element is Window) image.Render(element);
        else
        {
            // An element inside a window renders at its own offset; a brush of it does not.
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, across, down));
            image.Render(visual);
        }
        image.Freeze();
        return image;
    }

    static object Compare(SceneAttribute scene, string theme, BitmapSource shot, string referencePath, string comparePath)
    {
        Int32Rect crop = scene.Crop;
        int width = crop.Width, height = crop.Height;
        BitmapSource reference = new FormatConvertedBitmap(new CroppedBitmap(Load(referencePath), crop), PixelFormats.Pbgra32, null, 0);
        // The app over neutral gray, never over the reference: a capture that came out transparent
        // must show as different, not as a perfect match. Rounded corners cost a few gray pixels.
        var over = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var layer = new DrawingVisual();
        using (DrawingContext context = layer.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(128, 128, 128)), null, new Rect(0, 0, width, height));
            context.DrawImage(shot, new Rect(0, 0, shot.PixelWidth, shot.PixelHeight));
        }
        over.Render(layer);
        byte[] expected = Pixels(reference), actual = Pixels(over);
        var heat = new byte[expected.Length];
        double total = 0;
        int changed = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            int d = (Math.Abs(expected[i] - actual[i]) + Math.Abs(expected[i + 1] - actual[i + 1])
                + Math.Abs(expected[i + 2] - actual[i + 2])) / 3;
            total += d;
            if (d > 24) changed++;
            byte fade = (byte)(255 - Math.Min(255, d * 3));
            heat[i] = fade; heat[i + 1] = fade; heat[i + 2] = 255; heat[i + 3] = 255;
        }
        const int gap = 16;
        var sheet = new RenderTargetBitmap(width * 3 + gap * 2, height, 96, 96, PixelFormats.Pbgra32);
        var page = new DrawingVisual();
        using (DrawingContext context = page.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, sheet.PixelWidth, height));
            context.DrawImage(reference, new Rect(0, 0, width, height));
            context.DrawImage(over, new Rect(width + gap, 0, width, height));
            context.DrawImage(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, heat, width * 4),
                new Rect(2 * (width + gap), 0, width, height));
        }
        sheet.Render(page);
        Save(sheet, comparePath);
        double pixels = width * height;
        byte[] own = Pixels(shot);
        int clear = 0;
        for (int i = 3; i < own.Length; i += 4) if (own[i] < 250) clear++;
        return new
        {
            scene = scene.Name,
            theme,
            reference = Path.GetFileName(referencePath),
            crop = new { crop.X, crop.Y, crop.Width, crop.Height },
            captured = new { width = shot.PixelWidth, height = shot.PixelHeight },
            sizeOff = new { width = shot.PixelWidth - width, height = shot.PixelHeight - height },
            // Mean of the per-pixel mean RGB difference, as a share of 255.
            meanDifferencePercent = Math.Round(total / pixels / 255 * 100, 2),
            // Share of pixels whose mean RGB difference is over 24 of 255 (about 9%).
            pixelsOver24Percent = Math.Round(changed / pixels * 100, 2),
            // Share of the app's own pixels that are not fully opaque (rounded corners, or a defect).
            transparentPixelsPercent = Math.Round(clear * 100.0 / Math.Max(1, own.Length / 4), 2),
        };
    }

    static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    internal static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static void Save(BitmapSource image, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }

    internal static string References()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (Directory.Exists(Path.Combine(folder.FullName, "ui-probe", "reference")))
                return Path.Combine(folder.FullName, "ui-probe", "reference");
        throw new DirectoryNotFoundException("ui-probe/reference was not found above " + AppContext.BaseDirectory);
    }
}
