using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deskweave.AgentWorkspaces;

public sealed partial class AgentDesktop
{
    // Explicit per-workspace opt-in. No desktop discovery, game injection or owner input here.
    // Only a window already enumerated on this private desktop can select this provider.
    readonly MinecraftPreview _minecraftPreview = new();

    bool PaintMinecraft(nint window, nint dc, int width, int height, out bool available)
    {
        available = true;
        Native.GetWindowThreadProcessId(window, out int pid);
        if (!_minecraftPreview.Matches(Folder ?? WorkspaceSandbox.FolderFor(Name), pid)) return false;
        Native.SetBkColor(dc, 0x00202020);
        var box = new Native.Rect { Right = width, Bottom = height };
        Native.ExtTextOutW(dc, 0, 0, Native.EtoOpaque, ref box, null, 0, 0);
        var frame = _minecraftPreview.LatestFrame(pid);
        if (frame is null)
        {
            available = false;
            Label(dc, box, "Minecraft capture unavailable or stale");
            return true; // Never substitute a black native frame for a failed configured provider.
        }
        if (!Native.GetClientRect(window, out var client) || client.Right <= 0 || client.Bottom <= 0)
        { available = false; return true; }
        var origin = new Native.Point();
        if (!MinecraftPreview.ClientToScreen(window, ref origin)
            || !Native.GetWindowRect(window, out var outer)) { available = false; return true; }
        int x = origin.X - outer.Left, y = origin.Y - outer.Top;
        int stride = frame.PixelWidth * 4;
        byte[] pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        var header = new MinecraftPreview.BitmapHeader
        {
            Size = 40, Width = frame.PixelWidth, Height = -frame.PixelHeight,
            Planes = 1, BitCount = 32, ImageSize = (uint)pixels.Length,
        };
        int copied = MinecraftPreview.StretchDIBits(dc, x, y, client.Right, client.Bottom,
            0, 0, frame.PixelWidth, frame.PixelHeight, pixels, ref header, 0, Native.SrcCopy);
        available = copied > 0;
        return true;
    }
}

/// <summary>Bounded preview transport, deliberately separate from the low-latency policy stream.</summary>
internal sealed class MinecraftPreview
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const double MaximumAgeMs = 1500;
    internal const int MaximumSourceWidth = 4096, MaximumSourceHeight = 2160;
    internal const long MaximumSourcePixels = (long)MaximumSourceWidth * MaximumSourceHeight;
    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, MaxConnectionsPerServer = 1,
    }) { Timeout = TimeSpan.FromMilliseconds(250) };
    internal sealed record Connection(int GamePid, int Port, string Token);
    sealed record FetchRequest(long Revision, Connection Connection, CancellationTokenSource Cancellation);
    sealed record FetchResult(BitmapSource Image, long Received, double Age);
    readonly object _gate = new();
    readonly Action? _beforeBackgroundPublishForTests;
    Connection? _connection;
    int _unavailablePid;
    bool _configurationInvalid;
    long _revision;
    long _checked;
    BitmapSource? _frame;
    long _received;
    double _age;
    long _nextFetchAt;
    FetchRequest? _pending;

    internal MinecraftPreview(Action? beforeBackgroundPublishForTests = null) =>
        _beforeBackgroundPublishForTests = beforeBackgroundPublishForTests;

    internal bool ConfigurationInvalid { get { lock (_gate) return _configurationInvalid; } }
    internal bool FetchInProgress { get { lock (_gate) return _pending is not null; } }

    internal static Connection Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("protocolVersion").GetInt32() != 1) throw new FormatException();
        int pid = root.GetProperty("gamePid").GetInt32(), port = root.GetProperty("port").GetInt32();
        string token = root.GetProperty("token").GetString() ?? "";
        if (pid <= 0 || port is < 1024 or > 65535
            || !Regex.IsMatch(token, "\\A[A-Za-z0-9_-]{32,128}\\z")) throw new FormatException();
        return new Connection(pid, port, token);
    }

    internal bool Matches(string? folder, int pid)
    {
        FetchRequest? cancel = null;
        bool matches;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now - _checked >= 1000 || _checked == 0)
            {
                _checked = now;
                Connection? connection = null;
                bool disabled = folder is null;
                try
                {
                    if (folder is not null)
                    {
                        using var stream = File.OpenRead(Path.Combine(folder, "minecraft-bridge.json"));
                        if (stream.Length > 4096) throw new FormatException();
                        using var reader = new StreamReader(stream);
                        char[] json = new char[4097];
                        int length = reader.ReadBlock(json, 0, json.Length);
                        if (length > 4096) throw new FormatException();
                        connection = Parse(new string(json, 0, length));
                    }
                }
                catch (FileNotFoundException) { disabled = true; }
                catch (DirectoryNotFoundException) { disabled = true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
                bool invalid = !disabled && connection is null;
                // A removed opt-in disables this provider. An existing but broken opt-in
                // keeps the last selected PID unavailable; a first broken opt-in is surfaced
                // as a workspace configuration fault without guessing a target window.
                _unavailablePid = connection?.GamePid ?? (disabled ? 0 : _connection?.GamePid ?? _unavailablePid);
                if (connection != _connection || invalid != _configurationInvalid)
                {
                    _revision++;
                    _frame = null;
                    _nextFetchAt = 0;
                    cancel = _pending;
                }
                _connection = connection;
                _configurationInvalid = invalid;
            }
            matches = (_connection?.GamePid ?? _unavailablePid) == pid && pid > 0;
        }
        // Cancellation may complete an HTTP continuation, so never invoke it while holding
        // the state lock. A finished fetch can dispose its source before this call arrives.
        try { cancel?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return matches;
    }

    /// <summary>Production paint path: only reads a frozen cache and coalesces background work.</summary>
    internal BitmapSource? LatestFrame(int pid)
    {
        FetchRequest? request = Reserve(pid, throttle: true, out BitmapSource? cached);
        if (request is not null)
            _ = Task.Run(() =>
            {
                FetchResult? result = null;
                try
                {
                    result = Fetch(request);
                    _beforeBackgroundPublishForTests?.Invoke();
                }
                catch (Exception error) when (error is not OutOfMemoryException) { result = null; }
                finally { Complete(request, result); }
            });
        return cached;
    }

    // Synchronous transport seam for headless contract checks. Never call from a desktop pump.
    internal BitmapSource? Frame(int pid)
    {
        FetchRequest? request = Reserve(pid, throttle: false, out BitmapSource? cached);
        return request is null ? cached : Complete(request, Fetch(request));
    }

    FetchRequest? Reserve(int pid, bool throttle, out BitmapSource? cached)
    {
        lock (_gate)
        {
            cached = null;
            if (_connection is not { } connection || connection.GamePid != pid) return null;
            long now = Environment.TickCount64;
            cached = Fresh(now) ? _frame : null;
            if (_pending is not null || (throttle && now < _nextFetchAt)
                || (_frame is not null && now - _received < 100)) return null;
            var request = new FetchRequest(_revision, connection, new CancellationTokenSource());
            _pending = request;
            _nextFetchAt = now + 100; // At most ten attempts/second, including fast failures.
            return request;
        }
    }

    BitmapSource? Complete(FetchRequest request, FetchResult? result)
    {
        try
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, request)) _pending = null;
                if (request.Revision == _revision && request.Connection == _connection
                    && !request.Cancellation.IsCancellationRequested)
                {
                    _frame = result?.Image;
                    if (result is not null) { _received = result.Received; _age = result.Age; }
                }
                return Fresh(Environment.TickCount64) ? _frame : null;
            }
        }
        finally { request.Cancellation.Dispose(); }
    }

    static FetchResult? Fetch(FetchRequest request)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
            deadline.Token.ThrowIfCancellationRequested();
            long started = Environment.TickCount64;
            Connection connection = request.Connection;
            using var message = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{connection.Port}/frame");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
            using var response = Client.Send(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg"
                || response.Content.Headers.ContentLength is > MaximumBytes) return null;
            if (!int.TryParse(response.Headers.GetValues("X-Game-Pid").Single(), out int returnedPid)
                || returnedPid != connection.GamePid
                || !double.TryParse(response.Headers.GetValues("X-Frame-Age-Ms").Single(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double age)
                || !double.IsFinite(age) || age < 0 || age > MaximumAgeMs) return null;
            using var input = response.Content.ReadAsStream();
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = input.ReadAsync(buffer.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult()) != 0)
            {
                if (bytes.Length + read > MaximumBytes) return null;
                bytes.Write(buffer, 0, read);
            }
            if (!TryJpegDimensions(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), out int sourceWidth, out _)) return null;
            bytes.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Min(sourceWidth, 1280); // Downscale only; small policy frames stay small.
            bitmap.StreamSource = bytes;
            bitmap.EndInit();
            if (bitmap.PixelHeight > 2160) return null;
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            long received = Environment.TickCount64;
            return new FetchResult(converted, received, age + received - started);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or InvalidOperationException or FormatException or NotSupportedException or ArgumentException
            or COMException) { return null; }
    }

    bool Fresh(long now) => _frame is not null && _age + now - _received <= MaximumAgeMs;

    // Inspect dimensions before WPF can allocate decoded pixels. A small compressed
    // payload alone does not bound image size, particularly at extreme aspect ratios.
    internal static bool TryJpegDimensions(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) return false;
        int offset = 2;
        while (offset < jpeg.Length)
        {
            if (jpeg[offset++] != 0xff) return false;
            while (offset < jpeg.Length && jpeg[offset] == 0xff) offset++;
            if (offset >= jpeg.Length) return false;
            int marker = jpeg[offset++];
            if (marker is 0xd9 or 0xda or 0x00) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            if (offset + 2 > jpeg.Length) return false;
            int length = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (length < 2 || length > jpeg.Length - offset) return false;
            bool startOfFrame = marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc);
            if (startOfFrame)
            {
                if (length < 8) return false;
                height = (jpeg[offset + 3] << 8) | jpeg[offset + 4];
                width = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
                return width > 0 && height > 0 && width <= MaximumSourceWidth
                    && height <= MaximumSourceHeight && (long)width * height <= MaximumSourcePixels;
            }
            offset += length;
        }
        return false;
    }

    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Native.Point point);
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageSize;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }
    [DllImport("gdi32.dll")] internal static extern int StretchDIBits(nint dc, int x, int y,
        int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        byte[] pixels, ref BitmapHeader info, uint usage, uint operation);
}
