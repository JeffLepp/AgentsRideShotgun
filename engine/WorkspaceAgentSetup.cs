using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HiveMind.AgentWorkspaces;

enum AgentSetupState { Missing, Installing, SignedOut, SigningIn, Ready, Failed }

enum AgentSetupAction { None, Install, SignIn }

sealed record AgentSetupSnapshot(
    AgentSetupState State,
    string Heading,
    string Detail,
    AgentSetupAction Action = AgentSetupAction.None,
    int? ProgressPercent = null)
{
    public bool IsBusy => State is AgentSetupState.Installing or AgentSetupState.SigningIn;
}

sealed record AgentAuthStatus(
    bool LoggedIn,
    bool PaidSubscription,
    string Method,
    string Subscription,
    string Failure = "",
    bool CliTrusted = true);

sealed record AgentRelease(
    string Version,
    string Platform,
    string FileName,
    string Checksum,
    long Size,
    Uri Download);

/// <summary>
/// The two-button boss-agent setup. Nothing in this type runs at module startup. Looking at the
/// state is a pair of local file checks; the network starts only after the owner presses Set up.
///
/// Installation deliberately does not execute Anthropic's changing PowerShell bootstrap text.
/// HiveMind downloads one versioned Windows binary into its own temporary folder, checks the size
/// and SHA-256 published in that release's manifest, asks Windows to verify the Authenticode
/// signature and the Anthropic publisher, and only then lets that unmodified binary run its own
/// <c>install stable</c> command. A pre-existing native installation is never overwritten by
/// HiveMind. The provider remains the owner of the installed CLI and its updater.
///
/// Sign-in is also the provider's flow. HiveMind starts <c>claude auth login</c>, waits for the
/// provider's browser callback, and reads only the redacted fields from <c>auth status --json</c>.
/// It never reads, stores, copies or intermediates a token.
/// </summary>
static class WorkspaceAgentSetup
{
    const string Channel = "stable";
    const string Publisher = "Anthropic, PBC";
    const string Releases = "https://downloads.claude.ai/claude-code-releases/";

    static readonly object Sync = new();
    static readonly SemaphoreSlim Operation = new(1, 1);
    static readonly HttpClient Web = Client();
    static AgentSetupSnapshot _shown = Detect();

    public static event Action<AgentSetupSnapshot>? Changed;

    /// <summary>The current app-wide setup state. This accessor never starts work or uses network.</summary>
    public static AgentSetupSnapshot Current
    {
        get
        {
            lock (Sync) return _shown;
        }
    }

    /// <summary>The setup surface for one workspace's selected credential mode.</summary>
    internal static AgentSetupSnapshot For(WorkspaceAgentCredentialMode credentials)
    {
        AgentSetupSnapshot current = Current;
        if (credentials == WorkspaceAgentCredentialMode.Subscription) return current;
        return current.State switch
        {
            AgentSetupState.Missing or AgentSetupState.Installing => current,
            AgentSetupState.Failed when current.Action == AgentSetupAction.Install => current,
            _ => new(AgentSetupState.Ready, "Boss agent ready", "Claude CLI / API configuration"),
        };
    }

    /// <summary>
    /// Confirms an existing CLI without model use. Called when the panel is deliberately opened and
    /// before every mission; never by the module's startup hook.
    /// </summary>
    public static async Task VerifyExistingAsync(CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        string? cli = WorkspaceAgent.FindCli();
        if (cli is null) { Publish(Missing()); return; }

        try
        {
            AgentAuthStatus auth = await ReadAuthStatus(cli, cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            Publish(Present(auth));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The panel was hidden or closed. Its next visible session can check again.
        }
    }

    /// <summary>Downloads and installs only after the visible Set up button calls this method.</summary>
    public static async Task InstallAsync()
    {
        if (!await Operation.WaitAsync(0).ConfigureAwait(false)) return;
        string? staged = null;
        string? pending = null;
        try
        {
            string? existing = WorkspaceAgent.FindCli();
            if (existing is not null && Trusted(existing))
            {
                await PaidSubscription(existing, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            string native = NativeCliPath;
            if (File.Exists(native) && !Trusted(native))
                throw new InvalidDataException(
                    "The existing native Claude Code file is not signed by Anthropic. "
                    + "Deskweave will not replace it.");

            Publish(new(AgentSetupState.Installing, "Setting up the boss agent",
                "Checking Anthropic's stable Windows release…", AgentSetupAction.None, 0));

            AgentRelease release = await Release(CancellationToken.None).ConfigureAwait(false);
            if (WorkspaceStore.FreeBytes() < release.Size * 2)
                throw new IOException("There is not enough free space for the verified download and installation.");

            string setup = SetupFolder;
            Directory.CreateDirectory(setup);
            pending = Path.Combine(setup, "claude.exe.part");
            staged = Path.Combine(setup, "claude.exe");
            TryDelete(pending);
            TryDelete(staged);

            await Download(release, pending, CancellationToken.None).ConfigureAwait(false);
            VerifyPayload(pending, release, Trusted);
            File.Move(pending, staged, overwrite: false);

            Publish(new(AgentSetupState.Installing, "Setting up the boss agent",
                $"Verified Anthropic's signature. Installing Claude Code {release.Version}…",
                AgentSetupAction.None, 100));

            ProcessResult installed = await Run(staged, ["install", Channel],
                TimeSpan.FromMinutes(8), CancellationToken.None).ConfigureAwait(false);
            string? cli = WorkspaceAgent.FindCli();
            if (installed.ExitCode != 0 || cli is null || !Trusted(cli))
                throw new InvalidDataException(
                    "Claude Code did not leave a verifiable native installation on this PC.");

            ProcessResult version = await Run(cli, ["--version"],
                TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            if (version.ExitCode != 0 || !version.Output.Contains(release.Version, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Claude Code did not report the version that Anthropic published.");

            Publish(SignedOut("Claude Code is verified and installed. Sign in through Anthropic to continue."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or UnauthorizedAccessException or TimeoutException or JsonException
            or System.ComponentModel.Win32Exception)
        {
            Publish(Failed(AgentSetupAction.Install, InstallFailure(ex)));
        }
        finally
        {
            TryDelete(pending);
            TryDelete(staged);
            TryRemoveEmpty(SetupFolder);
            Operation.Release();
        }
    }

    /// <summary>Starts Anthropic's own browser login after the visible Sign in button is pressed.</summary>
    public static async Task SignInAsync()
    {
        if (!await Operation.WaitAsync(0).ConfigureAwait(false)) return;
        Process? login = null;
        try
        {
            string? cli = WorkspaceAgent.FindCli();
            if (cli is null || !Trusted(cli))
            {
                Publish(Failed(AgentSetupAction.Install,
                    "Claude Code is missing or could not be verified. Set it up again before signing in."));
                return;
            }

            Publish(new(AgentSetupState.SigningIn, "Finish in Anthropic's browser",
                "A browser window should open. Deskweave never sees your password or token."));

            DateTime oldCredential = CredentialStamp();
            var start = StartInfo(cli);
            start.RedirectStandardInput = true;
            start.ArgumentList.Add("auth");
            start.ArgumentList.Add("login");
            login = Process.Start(start) ?? throw new IOException("Claude Code would not start its sign-in flow.");
            Task<string> output = login.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> errors = login.StandardError.ReadToEndAsync(CancellationToken.None);

            DateTimeOffset deadline = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
            AgentAuthStatus last = new(false, false, "", "");
            while (DateTimeOffset.Now < deadline)
            {
                bool changed = CredentialStamp() != oldCredential;
                if (changed || login.HasExited)
                {
                    last = await ReadAuthStatus(cli, CancellationToken.None).ConfigureAwait(false);
                    if (last.PaidSubscription)
                    {
                        Stop(login);
                        Publish(Ready(last.Subscription));
                        return;
                    }
                    if (login.HasExited) break;
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }

            Stop(login);
            string diagnostic = Head(((await errors.ConfigureAwait(false)) + "\n"
                + (await output.ConfigureAwait(false))).Trim());
            string detail = last.LoggedIn
                ? "Anthropic signed this account in, but Claude Code did not report a paid Claude plan."
                : diagnostic.Length > 0 ? "Anthropic did not finish sign-in: " + diagnostic
                : "Anthropic did not finish sign-in. Try again and keep the browser window open.";
            Publish(Failed(AgentSetupAction.SignIn, detail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            Publish(Failed(AgentSetupAction.SignIn,
                "Sign-in did not finish. " + SafeMessage(ex)
                + " Deskweave did not read or store your credentials."));
        }
        finally
        {
            Stop(login);
            login?.Dispose();
            Operation.Release();
        }
    }

    /// <summary>
    /// Strong mission gate. A credentials file says OAuth exists; this asks the unmodified CLI what
    /// it will actually use after billed/provider overrides have been removed.
    /// </summary>
    public static async Task<AgentAuthStatus> PaidSubscription(string cli, CancellationToken cancel)
    {
        AgentAuthStatus auth = await ReadAuthStatus(cli, cancel).ConfigureAwait(false);
        Publish(Present(auth));
        return auth;
    }

    internal static AgentSetupSnapshot Present(AgentAuthStatus auth)
    {
        if (auth.PaidSubscription) return Ready(auth.Subscription);
        if (!auth.CliTrusted) return Failed(AgentSetupAction.Install, auth.Failure);
        return SignedOut(AuthDetail(auth));
    }

    static AgentSetupSnapshot Detect()
    {
        string? cli = WorkspaceAgent.FindCli();
        if (cli is null) return Missing();
        return WorkspaceAgent.OnSubscription() ? Ready() : SignedOut();
    }

    static AgentSetupSnapshot Missing() => new(
        AgentSetupState.Missing,
        "Set up the boss agent",
        "Downloads a verified stable Claude Code release from Anthropic. No administrator or terminal.",
        AgentSetupAction.Install);

    static AgentSetupSnapshot SignedOut(string? detail = null) => new(
        AgentSetupState.SignedOut,
        "Sign in to Claude Code",
        detail ?? "Use your paid Claude plan in Anthropic's browser. Deskweave never handles the credential.",
        AgentSetupAction.SignIn);

    static AgentSetupSnapshot Ready(string subscription = "") => new(
        AgentSetupState.Ready,
        "Boss agent ready",
        subscription.Length > 0 ? $"Claude {subscription} subscription" : "Claude subscription");

    static AgentSetupSnapshot Failed(AgentSetupAction retry, string detail) => new(
        AgentSetupState.Failed,
        retry == AgentSetupAction.Install ? "Boss-agent setup stopped" : "Claude sign-in stopped",
        detail,
        retry);

    static string AuthDetail(AgentAuthStatus auth)
    {
        if (auth.Failure.Length > 0) return auth.Failure;
        if (!auth.LoggedIn) return "Claude Code is installed but not signed in.";
        if (!auth.Method.Equals("claude.ai", StringComparison.OrdinalIgnoreCase))
            return "Claude Code is using a billed or third-party credential. Sign in with a paid Claude plan instead.";
        return "Claude Code did not report a paid Claude plan. Sign in again or check the plan on claude.ai.";
    }

    static string InstallFailure(Exception ex) => ex switch
    {
        HttpRequestException => "Could not download Claude Code from Anthropic. Check the connection and try again.",
        InvalidDataException => SafeMessage(ex),
        UnauthorizedAccessException => "Windows blocked the setup files. Nothing was run.",
        TimeoutException => "Claude Code setup took too long and was stopped.",
        IOException => SafeMessage(ex),
        _ => "Claude Code setup did not finish. " + SafeMessage(ex),
    };

    static string SafeMessage(Exception ex)
    {
        string first = Head(ex.Message);
        return first.Length > 0 ? first : "Try again.";
    }

    static void Publish(AgentSetupSnapshot snapshot)
    {
        lock (Sync) _shown = snapshot;
        Delegate[] listeners = Changed?.GetInvocationList() ?? [];
        foreach (Delegate listener in listeners)
            try { ((Action<AgentSetupSnapshot>)listener)(snapshot); } catch { }
    }

    static HttpClient Client()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CheckCertificateRevocationList = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Deskweave/0.1");
        return client;
    }

    static async Task<AgentRelease> Release(CancellationToken cancel)
    {
        string version = (await Web.GetStringAsync(new Uri(Releases + Channel), cancel)
            .ConfigureAwait(false)).Trim();
        if (!Version.TryParse(version, out _))
            throw new InvalidDataException("Anthropic returned an invalid release version, so nothing was run.");

        string platform = Platform(RuntimeInformation.OSArchitecture);
        string manifest = await Web.GetStringAsync(
            new Uri($"{Releases}{version}/manifest.json"), cancel).ConfigureAwait(false);
        return ParseRelease(version, platform, manifest);
    }

    internal static string Platform(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win32-x64",
        Architecture.Arm64 => "win32-arm64",
        _ => throw new InvalidDataException("Claude Code requires 64-bit Windows on x64 or ARM64."),
    };

    internal static AgentRelease ParseRelease(string version, string platform, string manifest)
    {
        using JsonDocument json = JsonDocument.Parse(manifest);
        JsonElement root = json.RootElement;
        string published = root.GetProperty("version").GetString() ?? "";
        if (!published.Equals(version, StringComparison.Ordinal))
            throw new InvalidDataException("Anthropic's release manifest did not match the selected version.");
        if (!root.GetProperty("platforms").TryGetProperty(platform, out JsonElement item))
            throw new InvalidDataException("Anthropic did not publish this Windows architecture.");

        string file = item.GetProperty("binary").GetString() ?? "";
        string checksum = (item.GetProperty("checksum").GetString() ?? "").ToLowerInvariant();
        long size = item.GetProperty("size").GetInt64();
        if (!file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
            || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit)
            || size < 1_000_000 || size > 1_000_000_000)
            throw new InvalidDataException("Anthropic's release manifest was incomplete, so nothing was run.");

        return new(version, platform, file, checksum, size,
            new Uri($"{Releases}{version}/{platform}/{file}"));
    }

    static async Task Download(AgentRelease release, string path, CancellationToken cancel)
    {
        using HttpResponseMessage response = await Web.GetAsync(release.Download,
            HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            throw new InvalidDataException("The Claude Code download size did not match Anthropic's manifest.");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        int shown = -1;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            copied += read;
            int percent = (int)Math.Clamp(copied * 100 / Math.Max(1, release.Size), 0, 99);
            if (percent == shown) continue;
            shown = percent;
            Publish(new(AgentSetupState.Installing, "Setting up the boss agent",
                $"Downloading Claude Code {release.Version} from Anthropic… {percent}%",
                AgentSetupAction.None, percent));
        }
    }

    internal static void VerifyPayload(string path, AgentRelease release, Func<string, bool> trust)
    {
        var file = new FileInfo(path);
        if (file.Length != release.Size)
            throw new InvalidDataException("The Claude Code download was incomplete, so nothing was run.");
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(release.Checksum)))
            throw new InvalidDataException("The Claude Code checksum did not match Anthropic's manifest, so nothing was run.");
        if (!trust(path))
            throw new InvalidDataException("Windows could not verify that Claude Code was signed by Anthropic, so nothing was run.");
    }

    static bool Trusted(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        && Native.VerifyAuthenticode(path, Publisher);

    /// <summary>Configured credentials still run only through Anthropic's signed native CLI.</summary>
    internal static bool IsTrustedCli(string path) => Trusted(path);

    static async Task<AgentAuthStatus> ReadAuthStatus(string cli, CancellationToken cancel)
    {
        if (!Trusted(cli))
            return new(false, false, "", "",
                "Windows could not verify this Claude Code installation as Anthropic's. Deskweave will not run it.",
                CliTrusted: false);
        try
        {
            ProcessResult result = await Run(cli, ["auth", "status", "--json"],
                TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            string json = ExtractJson(result.Output.Length > 0 ? result.Output : result.Error);
            AgentAuthStatus status = ParseAuth(json);
            return status with
            {
                Failure = status.PaidSubscription || status.LoggedIn ? ""
                    : "Claude Code is installed but not signed in."
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException
            or System.ComponentModel.Win32Exception)
        {
            return new(false, false, "", "", "Claude Code could not confirm its sign-in: " + SafeMessage(ex));
        }
    }

    internal static AgentAuthStatus ParseAuth(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        bool logged = root.TryGetProperty("loggedIn", out JsonElement signed)
            && signed.ValueKind is JsonValueKind.True;
        string method = Text(root, "authMethod");
        string subscription = Text(root, "subscriptionType").ToLowerInvariant();
        bool paid = logged && method.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
            && subscription is "pro" or "max" or "team" or "enterprise";
        return new(logged, paid, method, subscription);
    }

    static string ExtractJson(string text)
    {
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        if (first < 0 || last < first) throw new InvalidDataException("Claude Code returned no authentication status.");
        return text[first..(last + 1)];
    }

    static string Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    static ProcessStartInfo StartInfo(string file)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        WorkspaceAgent.SubscriptionOnly(start);
        return start;
    }

    static async Task<ProcessResult> Run(
        string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancel)
    {
        var start = StartInfo(file);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException(Path.GetFileName(file) + " would not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Stop(process);
            throw new TimeoutException(Path.GetFileName(file) + " took too long.");
        }
        return new(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }

    static void Stop(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    static DateTime CredentialStamp()
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json");
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    internal static string NativeCliPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");

    static string SetupFolder => Path.Combine(WorkspaceStore.Root, ".setup");

    static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void TryRemoveEmpty(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Head(string text)
    {
        string first = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "";
        return first.Length > 220 ? first[..220] : first;
    }

    sealed record ProcessResult(int ExitCode, string Output, string Error);
}
