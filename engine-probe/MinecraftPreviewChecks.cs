using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

/// <summary>Headless HTTP/metadata/image-contract checks; never attaches or touches a desktop.</summary>
internal static class MinecraftPreviewChecks
{
    const int GamePid = 12345;
    const string Token = "synthetic_fixture_token_xxxxxxxxxxxxxxxxxxxxxxxx";

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "result.json");
        if (File.Exists(reportPath)) throw new IOException("Use a new preview-check output directory.");
        var checks = new List<object>();
        void Check(string name, Action action)
        {
            var elapsed = Stopwatch.StartNew();
            action();
            checks.Add(new { name, passed = true, elapsedMs = elapsed.Elapsed.TotalMilliseconds });
        }
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        string ConnectionJson(int port = 25001) => JsonSerializer.Serialize(new { protocolVersion = 1, gamePid = GamePid, port, token = Token });
        string Configure(FakeBridge server, string name)
        {
            string folder = Path.Combine(output, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "minecraft-bridge.json"), ConnectionJson(server.Port));
            return folder;
        }
        byte[] image = Jpeg(64, 36);

        Check("connection protocol, PID, port and token validation", () =>
        {
            var parsed = MinecraftPreview.Parse(ConnectionJson());
            Require(parsed.GamePid == GamePid && parsed.Port == 25001, "Valid metadata was rejected.");
            foreach (string invalid in new[] {
                ConnectionJson().Replace("\"protocolVersion\":1", "\"protocolVersion\":2"),
                ConnectionJson().Replace("\"gamePid\":12345", "\"gamePid\":0"),
                ConnectionJson(80), ConnectionJson(65536), ConnectionJson().Replace(Token, "short"), "{}",
            })
            {
                bool rejected = false;
                try { MinecraftPreview.Parse(invalid); }
                catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException) { rejected = true; }
                Require(rejected, "Invalid connection metadata was accepted.");
            }
        });
        Check("JPEG dimensions checked before decoder allocation", () =>
        {
            Require(MinecraftPreview.TryJpegDimensions(image, out int width, out int height) && width == 64 && height == 36, "Real JPEG dimensions were not read.");
            Require(!MinecraftPreview.TryJpegDimensions(DimensionFixture(16, 65535), out _, out _), "Extreme aspect ratio was accepted.");
            Require(!MinecraftPreview.TryJpegDimensions(DimensionFixture(65535, 16), out _, out _), "Extreme width was accepted.");
            Require(!MinecraftPreview.TryJpegDimensions(image.AsSpan(0, 8), out _, out _), "Truncated JPEG metadata was accepted.");
        });
        Check("workspace opt-in, PID binding, authentication, and no upscale", () =>
        {
            using var server = new FakeBridge(image);
            string folder = Configure(server, "workspace-auth");
            var preview = new MinecraftPreview();
            Require(preview.Matches(folder, GamePid), "Configured workspace did not match.");
            Require(!preview.Matches(folder, GamePid + 1), "Other window PID matched.");
            Require(preview.Frame(GamePid + 1) is null && server.Requests == 0, "Mismatched PID contacted the bridge.");
            var frame = preview.Frame(GamePid);
            Require(frame is not null && frame.PixelWidth == 64 && frame.PixelHeight == 36, "Small JPEG was lost or upscaled.");
            Require(server.Authorized && server.LastPath == "/frame", "Bearer-authenticated frame request was not observed.");
            Require(!new MinecraftPreview().Matches(Path.Combine(output, "unconfigured-workspace"), GamePid), "An unconfigured workspace borrowed a connection.");
        });
        Check("broken existing opt-in stays unavailable, recovers, and deletion disables it", () =>
        {
            using var server = new FakeBridge(image);
            string folder = Configure(server, "connection-lifecycle");
            string file = Path.Combine(folder, "minecraft-bridge.json");
            var preview = new MinecraftPreview();
            Require(preview.Matches(folder, GamePid) && preview.Frame(GamePid) is not null, "Valid initial provider did not return a frame.");
            File.WriteAllText(file, "{invalid existing JSON");
            Thread.Sleep(1100); // Exceed the provider's intentional one-second config refresh.
            Require(preview.Matches(folder, GamePid), "Broken existing configuration silently deselected the known provider.");
            int requests = server.Requests;
            Require(preview.Frame(GamePid) is null && server.Requests == requests, "Broken configuration reused a frame or contacted the old endpoint.");
            Require(!new MinecraftPreview().Matches(folder, GamePid), "A first malformed configuration invented a selectable PID.");
            File.WriteAllText(file, ConnectionJson(server.Port));
            Thread.Sleep(1100);
            Require(preview.Matches(folder, GamePid) && preview.Frame(GamePid) is not null, "Restoring valid configuration did not recover the provider.");
            File.Delete(file);
            Thread.Sleep(1100);
            Require(!preview.Matches(folder, GamePid) && preview.Frame(GamePid) is null, "Confirmed opt-in deletion did not disable the provider.");
        });
        Check("cold malformed opt-in reports missing pixels without inventing a PID", () =>
        {
            string folder = Path.Combine(output, "cold-malformed");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "minecraft-bridge.json"), "{incomplete metadata");
            var preview = new MinecraftPreview();
            Require(!preview.Matches(folder, GamePid), "Malformed initial metadata selected an unknown window.");
            Require(preview.ConfigurationInvalid, "Malformed opt-in was hidden from workspace metadata.");
            Require(preview.LatestFrame(GamePid) is null && !preview.FetchInProgress, "Malformed opt-in started a fetch.");
            var state = new WorkspaceScreenState(false, DateTimeOffset.UtcNow, 0, false, Unavailable: 1);
            Require(state.Note.Contains("missing", StringComparison.OrdinalIgnoreCase), "Workspace note omitted missing game pixels.");
        });
        Check("production preview never blocks on a stalled endpoint and coalesces fetches", () =>
        {
            using var server = new FakeBridge(image) { BodyDelayMs = 2000 };
            var preview = new MinecraftPreview();
            Require(preview.Matches(Configure(server, "background-stall"), GamePid), "Fixture connection mismatch.");
            var elapsed = Stopwatch.StartNew();
            for (int index = 0; index < 200; index++)
                Require(preview.LatestFrame(GamePid) is null, "A stalled source supplied a frame.");
            Require(elapsed.ElapsedMilliseconds < 150, "LatestFrame waited for network/body decoding on its caller.");
            Require(preview.FetchInProgress, "The pending background fetch was not tracked.");
            Await(() => server.Requests == 1, 1000, "Background frame request did not reach the fixture.");
            Require(server.Requests == 1, "Repeated preview calls did not coalesce.");
            Await(() => !preview.FetchInProgress, 1000, "Cancelled background request did not finish.");
        });
        Check("a late decoded frame cannot publish after connection replacement", () =>
        {
            using var oldServer = new FakeBridge(image);
            using var replacement = new FakeBridge(Jpeg(80, 45));
            using var fetched = new ManualResetEventSlim();
            using var publish = new ManualResetEventSlim();
            int publications = 0;
            var preview = new MinecraftPreview(() =>
            {
                if (Interlocked.Increment(ref publications) != 1) return;
                fetched.Set();
                if (!publish.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Late publication fixture exceeded its bound.");
            });
            string folder = Configure(oldServer, "background-replacement");
            Require(preview.Matches(folder, GamePid), "Fixture connection mismatch.");
            Require(preview.LatestFrame(GamePid) is null, "Cold nonblocking preview unexpectedly had a frame.");
            try
            {
                Require(fetched.Wait(TimeSpan.FromSeconds(1)), "First response did not reach the pre-publication boundary.");
                var withheldAge = Stopwatch.StartNew();
                File.WriteAllText(Path.Combine(folder, "minecraft-bridge.json"), ConnectionJson(replacement.Port));
                Thread.Sleep(1100);
                Require(preview.Matches(folder, GamePid), "Replacement connection was not selected.");
                Require(preview.LatestFrame(GamePid) is null, "Invalidated response remained visible.");
                Require(replacement.Requests == 0, "Replacement started another fetch while the old worker was still pending.");
                Require(withheldAge.ElapsedMilliseconds < 1400, "Fixture exceeded the old-frame freshness window; this run cannot isolate revision rejection.");
            }
            finally { publish.Set(); }
            Await(() => !preview.FetchInProgress, 1000, "Old background worker did not finish after unblocking.");
            BitmapSource? recovered = null;
            Await(() => { recovered = preview.LatestFrame(GamePid); return recovered is not null; }, 1000, "Replacement did not produce a fresh frame.");
            Require(recovered!.PixelWidth == 80 && recovered.PixelHeight == 45, "A frame from the invalidated endpoint was published.");
        });
        Check("stale age and wrong response PID are rejected", () =>
        {
            foreach (var setup in new[] { (Age: 1501d, Pid: GamePid), (Age: 0d, Pid: GamePid + 1) })
            {
                using var server = new FakeBridge(image) { AgeMs = setup.Age, ReturnedPid = setup.Pid };
                var preview = new MinecraftPreview();
                Require(preview.Matches(Configure(server, "stale-" + setup.Pid + "-" + setup.Age), GamePid), "Fixture connection mismatch.");
                Require(preview.Frame(GamePid) is null, "Stale or mismatched response was returned.");
            }
        });
        Check("transport time is added to source age", () =>
        {
            using var server = new FakeBridge(image) { AgeMs = 1490, BodyDelayMs = 80 };
            var preview = new MinecraftPreview();
            Require(preview.Matches(Configure(server, "transport-age"), GamePid), "Fixture connection mismatch.");
            Require(preview.Frame(GamePid) is null, "A frame aged past the limit during transport was returned.");
        });
        Check("HTTP unauthorized and redirects fail without following", () =>
        {
            foreach (int status in new[] { 401, 302 })
            {
                using var server = new FakeBridge(image) { Status = status };
                var preview = new MinecraftPreview();
                Require(preview.Matches(Configure(server, "http-" + status), GamePid), "Fixture connection mismatch.");
                Require(preview.Frame(GamePid) is null, "An unsuccessful HTTP response became a frame.");
                Require(server.Requests == 1, "A redirect was followed.");
            }
        });
        Check("declared and streamed oversize bodies are rejected", () =>
        {
            using (var server = new FakeBridge([]) { DeclaredBytes = MinecraftPreview.MaximumBytes + 1 })
            {
                var preview = new MinecraftPreview();
                Require(preview.Matches(Configure(server, "declared-limit"), GamePid), "Fixture connection mismatch.");
                Require(preview.Frame(GamePid) is null, "Oversized declared body became a frame.");
            }
            using (var server = new FakeBridge(new byte[MinecraftPreview.MaximumBytes + 1]) { OmitLength = true })
            {
                var preview = new MinecraftPreview();
                Require(preview.Matches(Configure(server, "streamed-limit"), GamePid), "Fixture connection mismatch.");
                Require(preview.Frame(GamePid) is null, "Oversized streamed body became a frame.");
            }
        });
        Check("stalled body is cancelled within a generous outer test bound", () =>
        {
            using var server = new FakeBridge(image) { BodyDelayMs = 2000 };
            var preview = new MinecraftPreview();
            Require(preview.Matches(Configure(server, "stalled-body"), GamePid), "Fixture connection mismatch.");
            var elapsed = Stopwatch.StartNew();
            Require(preview.Frame(GamePid) is null, "Stalled body became a frame.");
            Require(elapsed.ElapsedMilliseconds < 1500, "HTTP/body deadline did not bound this fixture.");
        });
        Check("placeholder frames cannot claim a complete desktop image", () =>
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
            Require(new DesktopFrame(bitmap, 1, 0, false).Complete, "Normal frame completeness changed.");
            Require(!new DesktopFrame(bitmap, 1, 0, false, Unavailable: 1).Complete, "Missing provider was called complete.");
            Require(new WorkspaceScreenState(false, DateTimeOffset.UtcNow, 0, false, Unavailable: 1).Note.Contains("missing", StringComparison.OrdinalIgnoreCase), "Missing provider pixels were absent from workspace status.");
        });

        using var report = new FileStream(reportPath, FileMode.CreateNew);
        JsonSerializer.Serialize(report, new
        {
            passed = checks.Count, failed = 0, checks,
            scope = "Headless synthetic loopback transport and JPEG checks only; no GUI, live game, attachment, settings change, or live throughput claim.",
        }, new JsonSerializerOptions { WriteIndented = true });
        return 0;
    }

    static void Await(Func<bool> ready, int timeoutMs, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            if (ready()) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException(message);
    }

    static byte[] Jpeg(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static byte[] DimensionFixture(int width, int height) =>
        [255, 216, 255, 192, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217];

    sealed class FakeBridge : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _cancel = new();
        readonly Task _loop;
        readonly byte[] _body;
        int _requests;
        internal int Port { get; }
        internal int Requests => Volatile.Read(ref _requests);
        internal bool Authorized { get; private set; }
        internal string? LastPath { get; private set; }
        internal int Status { get; init; } = 200;
        internal int ReturnedPid { get; init; } = GamePid;
        internal double AgeMs { get; init; }
        internal int BodyDelayMs { get; init; }
        internal long? DeclaredBytes { get; init; }
        internal bool OmitLength { get; init; }

        internal FakeBridge(byte[] body)
        {
            _body = body;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (!_cancel.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                    using var stream = client.GetStream();
                    var requestBytes = new List<byte>();
                    byte[] one = new byte[1];
                    while (requestBytes.Count < 16384 && await stream.ReadAsync(one, _cancel.Token) != 0)
                    {
                        requestBytes.Add(one[0]);
                        int count = requestBytes.Count;
                        if (count >= 4 && requestBytes[count - 4] == 13 && requestBytes[count - 3] == 10 && requestBytes[count - 2] == 13 && requestBytes[count - 1] == 10) break;
                    }
                    string request = Encoding.ASCII.GetString(requestBytes.ToArray());
                    Authorized = request.Contains("Authorization: Bearer " + Token + "\r\n", StringComparison.OrdinalIgnoreCase);
                    LastPath = request.Split(' ', 3).ElementAtOrDefault(1);
                    Interlocked.Increment(ref _requests);
                    string headers = $"HTTP/1.1 {Status} Fixture\r\nContent-Type: image/jpeg\r\nX-Game-Pid: {ReturnedPid}\r\nX-Frame-Age-Ms: {AgeMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\nConnection: close\r\n";
                    if (!OmitLength) headers += $"Content-Length: {DeclaredBytes ?? _body.Length}\r\n";
                    if (Status == 302) headers += $"Location: http://127.0.0.1:{Port}/redirect-must-not-be-followed\r\n";
                    headers += "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cancel.Token);
                    if (BodyDelayMs > 0) await Task.Delay(BodyDelayMs, _cancel.Token);
                    await stream.WriteAsync(_body, _cancel.Token);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _cancel.Cancel();
            _listener.Stop();
            _loop.Wait(TimeSpan.FromSeconds(1));
            _cancel.Dispose();
        }
    }
}
