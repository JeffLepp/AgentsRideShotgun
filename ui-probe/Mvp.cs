using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// One state of the real app, photographed in light and dark. Put it on a static method in a
/// Scenes.*.cs file: the method gets a <see cref="SceneContext"/>, builds the state, and returns
/// the window or element to photograph at its real size.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class SceneAttribute(string name) : Attribute
{
    public string Name => name;
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

    /// <summary>One of the mini websites in ui-probe/reference/sites (shop, blog, docs, term), 1280x800, to stand in
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
/// The screen harness. For every scene, in light then dark, {name}.png is the real app, and
/// mvp-report.json lists each picture's size.
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
                rows.Add(new { scene = scene.Name, theme = name, captured = new { width = shot.PixelWidth, height = shot.PixelHeight } });
            }
        }
        File.WriteAllText(Path.Combine(output, "mvp-report.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>At 96 DPI, so one DIP is one pixel.
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
