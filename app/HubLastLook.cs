using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// The picture a workspace that is not running shows: the last one its screen had, kept in its own
/// folder when it went to sleep. Without this every Recent row is the same empty rectangle and the
/// hub cannot tell one old workspace from another - which is exactly what they are there to do.
///
/// Read once per workspace and kept until the file itself changes, off the UI thread, and decoded
/// to thumbnail width rather than full screen size: a sidebar row is 64x40, and decoding a whole
/// 1920x1080 frame for it is what made opening a recent workspace feel slow.
/// </summary>
internal static class HubLastLook
{
    /// <summary>Wide enough for the stack's card at any window size, small enough to decode in a
    /// blink. The card draws it UniformToFill, so extra detail past this is never seen.</summary>
    const int ThumbnailWidth = 480;

    readonly record struct Held(DateTime Written, long Length, BitmapSource? Image);

    static readonly Dictionary<string, Held> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gives <paramref name="entry"/> its stored picture when it has one and needs it: a workspace
    /// with no computer running always shows the last look it had, and a running one only until its
    /// own live frame arrives. Never blocks: a picture not read yet arrives on a later tick.
    /// </summary>
    internal static void Fill(HubEntry entry, bool working)
    {
        if (working && entry.Preview is not null) return;
        string path = WorkspaceStore.LastFrameOf(entry.Id);
        FileInfo file;
        try { file = new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        if (!file.Exists)
        {
            // The frame was swept (they keep for a week) or never taken. Whatever is on the card is
            // no longer this workspace's own look, so it does not keep standing in for one.
            if (Cache.Remove(entry.Id) && !working) entry.Preview = null;
            return;
        }
        if (Cache.TryGetValue(entry.Id, out Held held) && held.Written == file.LastWriteTimeUtc && held.Length == file.Length)
        {
            if (held.Image is not null && !ReferenceEquals(entry.Preview, held.Image)) entry.Preview = held.Image;
            return;
        }
        if (!Loading.Add(entry.Id)) return;
        DateTime written = file.LastWriteTimeUtc;
        long length = file.Length;
        string id = entry.Id;
        System.Windows.Threading.Dispatcher dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(() => Read(path)).ContinueWith(read => dispatcher.BeginInvoke(() =>
        {
            Loading.Remove(id);
            BitmapSource? image = read.IsCompletedSuccessfully ? read.Result : null;
            Cache[id] = new Held(written, length, image);
            if (image is not null) entry.Preview = image;
        }));
    }

    /// <summary>The full-size stored frame for the workspace page, read off the UI thread.</summary>
    internal static Task<BitmapSource?> FullAsync(string id) => Task.Run(() => Read(WorkspaceStore.LastFrameOf(id), whole: true));

    static BitmapSource? Read(string path, bool whole = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // OnLoad above: the whole picture is read here and the file closed with it, so a
            // workspace writing a newer frame over it is never blocked by the hub holding it open.
            image.UriSource = new Uri(path);
            if (!whole) image.DecodePixelWidth = ThumbnailWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
