using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// What an agent did to a workspace, kept inside that workspace's own folder. Deleting the
/// workspace deletes its evidence and nothing outside the folder is ever written.
///
/// The log is text and is never dropped. Frames are capped at 1 GB per workspace, oldest first,
/// which is the owner's decision of 2026-08-22.
/// </summary>
public sealed class WorkspaceEvidence : IDisposable
{
    const long DefaultCap = 1L << 30;

    readonly string _folder;
    readonly string _frames;
    readonly long _cap;
    readonly Lock _gate = new();
    byte[] _lastFrame = [];
    long _frameBytes;
    int _nextFrame = 1;
    bool _disposed;

    public WorkspaceEvidence(string workspaceFolder, long frameCap = DefaultCap)
    {
        _cap = frameCap;
        _folder = Path.Combine(workspaceFolder, "evidence");
        _frames = Path.Combine(_folder, "frames");
        Directory.CreateDirectory(_frames);
        LogPath = Path.Combine(_folder, "actions.log");
        // Pick up where the last session left off rather than overwriting its evidence.
        foreach (FileInfo frame in new DirectoryInfo(_frames).GetFiles("*.png"))
        {
            _frameBytes += frame.Length;
            if (int.TryParse(Path.GetFileNameWithoutExtension(frame.Name), out int number) && number >= _nextFrame)
                _nextFrame = number + 1;
        }
    }

    public string LogPath { get; }

    /// <summary>Bytes of frames on disk right now, against the cap.</summary>
    public long FrameBytes { get { lock (_gate) return _frameBytes; } }

    /// <summary>Frames deleted to stay under the cap, this session.</summary>
    public int FramesDropped { get; private set; }

    /// <summary>Frames not written because the screen had not changed.</summary>
    public int FramesUnchanged { get; private set; }

    /// <summary>
    /// One action and, when the screen actually changed, the picture that proves it. Pass the frame
    /// as captured; encoding only happens for a frame that is going to be kept.
    /// </summary>
    public void Note(string action, string detail, string outcome, BitmapSource? after = null)
    {
        if (_disposed) return;
        lock (_gate)
        {
            string frame = after is null ? string.Empty : Keep(after);
            var line = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append('\t')
                .Append(action).Append('\t')
                .Append(One(detail)).Append('\t')
                .Append(One(outcome));
            if (frame.Length > 0) line.Append('\t').Append(frame);
            try { File.AppendAllText(LogPath, line.Append('\n').ToString(), Encoding.UTF8); }
            catch (IOException) { /* evidence must never take the action down with it */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string One(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    string Keep(BitmapSource frame)
    {
        byte[] pixels;
        try
        {
            int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return string.Empty; }

        // ponytail: SHA256 over the raw pixels, about 8 ms for a 1280x800 frame. A perceptual hash
        // would also skip a frame that only changed by a blinking caret; swap it in if the frame
        // count ever becomes the problem.
        byte[] hash = SHA256.HashData(pixels);
        if (_lastFrame.AsSpan().SequenceEqual(hash)) { FramesUnchanged++; return string.Empty; }
        _lastFrame = hash;

        string name = _nextFrame.ToString("000000", CultureInfo.InvariantCulture) + ".png";
        string path = Path.Combine(_frames, name);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (FileStream file = File.Create(path)) encoder.Save(file);
            _nextFrame++;
            _frameBytes += new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
        Trim();
        return "frames/" + name;
    }

    /// <summary>Oldest first, until the frames fit. The log is text and is never touched here.</summary>
    void Trim()
    {
        if (_frameBytes <= _cap) return;
        FileInfo[] oldest = new DirectoryInfo(_frames).GetFiles("*.png");
        Array.Sort(oldest, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        // The newest frame is never dropped. A cap smaller than one frame would otherwise delete the
        // evidence of the action that was just taken, which is the one thing worth keeping.
        foreach (FileInfo frame in oldest.Take(Math.Max(0, oldest.Length - 1)))
        {
            if (_frameBytes <= _cap) return;
            long size = frame.Length;
            try { frame.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            _frameBytes -= size;
            FramesDropped++;
        }
    }

    public void Dispose() => _disposed = true;
}
