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
/// which is the owner's decision of 2026-08-22, and kept 7 days (MVP_SPEC, History). Settings >
/// History &amp; privacy > Save screenshots Off keeps the log and no frames.
/// </summary>
public sealed class WorkspaceEvidence : IDisposable
{
    const long DefaultCap = 1L << 30;
    static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    readonly string _folder;
    readonly string _frames;
    readonly long _cap;
    readonly Lock _gate = new();
    byte[] _lastFrame = [];
    long _frameBytes;
    // Frame names: the UTC time taken, then a sequence that only grows, so names sort oldest first
    // (as Trim needs) and never repeat, even if the clock steps back.
    string _stamp = "";
    int _sequence;
    bool _disposed;

    public WorkspaceEvidence(string workspaceFolder, long frameCap = DefaultCap)
    {
        _cap = frameCap;
        _folder = Path.Combine(workspaceFolder, "evidence");
        _frames = Path.Combine(_folder, "frames");
        Directory.CreateDirectory(_frames);
        LogPath = Path.Combine(_folder, "actions.log");
        // Pick up where the last session left off rather than overwriting its evidence.
        Expire(workspaceFolder);
        foreach (FileInfo frame in new DirectoryInfo(_frames).GetFiles("*.png"))
        {
            _frameBytes += frame.Length;
            string name = Path.GetFileNameWithoutExtension(frame.Name);
            if (name.Length == StampLength + 7 && string.CompareOrdinal(name, _stamp + "-" + _sequence.ToString("D6", CultureInfo.InvariantCulture)) > 0
                && int.TryParse(name.AsSpan(StampLength + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence))
                (_stamp, _sequence) = (name[..StampLength], sequence);
        }
    }

    // The leading "t" sorts after every digit, so these follow the numbered frames older versions
    // wrote (000001.png, 250000.png) instead of landing between them.
    const string StampFormat = "'t'yyyyMMdd-HHmmss-fffffff";
    const int StampLength = 24;

    /// <summary>
    /// Drops one workspace's frames past their week; the app sweeps every workspace with this, not
    /// only the running ones. Frames are named by when they were taken, so a name is never reused
    /// for a later picture while an old log line still points at it.
    /// </summary>
    internal static void Expire(string workspaceFolder)
    {
        DateTime expired = DateTime.UtcNow - KeptFor;
        var frames = new DirectoryInfo(Path.Combine(workspaceFolder, "evidence", "frames"));
        IEnumerable<FileInfo> old = frames.Exists ? frames.GetFiles("*.png") : [];
        // The picture Recent shows is a screenshot too, and keeps no longer than the rest.
        foreach (FileInfo frame in old.Append(new FileInfo(Path.Combine(workspaceFolder, "last-frame.png"))))
            if (frame.Exists && frame.LastWriteTimeUtc < expired)
                try { frame.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            string frame = after is null || AppSettingsStore.Current.Screenshots == ScreenshotMode.Off
                ? string.Empty : Keep(after);
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

        // Sorts after the numbered frames older versions wrote, and after every earlier one here.
        string now = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        if (string.CompareOrdinal(now, _stamp) > 0) (_stamp, _sequence) = (now, 0);
        else _sequence++;
        string name = _stamp + "-" + _sequence.ToString("D6", CultureInfo.InvariantCulture) + ".png";
        string path = Path.Combine(_frames, name);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (FileStream file = File.Create(path)) encoder.Save(file);
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
        // Counted again from the disk: the hourly sweep may have expired some behind this recorder.
        _frameBytes = oldest.Sum(frame => frame.Length);
        if (_frameBytes <= _cap) return;
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
