using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

// stdout belongs exclusively to MCP. The host-issued capability arrives through the child
// environment or a connection ticket, never a persisted provider configuration or an argument.
const string NotOpen = "Deskweave isn't open.";
const string NotAnswering = "Deskweave isn't answering.";
const string Closed = "Deskweave closed while this was running.";
const string Mismatched = "This MCP entry doesn't match the Deskweave on this PC. Connect the agent again from Deskweave's Settings.";

string? capability = Environment.GetEnvironmentVariable("DESKWEAVE_WORKSPACE_PIPE_KEY");
Environment.SetEnvironmentVariable("DESKWEAVE_WORKSPACE_PIPE_KEY", null);
string? ticket = null;
string fixedPipe = "";
if (args.Length == 2 && args[0] == "--workspace" && Path.IsPathFullyQualified(args[1])) ticket = args[1];
else if (args.Length == 1 && args[0].StartsWith("Deskweave.Workspace.", StringComparison.Ordinal)
    && !string.IsNullOrEmpty(capability)) fixedPipe = args[0];
else return await Say(Mismatched, 2);

// Where the agent is working, so Deskweave can give each project its own workspace. Claude Code
// names its project; other clients start their servers in theirs.
string cwd = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } project
    ? project : Environment.CurrentDirectory;
string context = "{\"jsonrpc\":\"2.0\",\"method\":\"deskweave/context\",\"params\":{\"cwd\":\""
    + JsonEncodedText.Encode(cwd) + "\"}}";

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var output = new Lock();
string? hello = null;

// A Deskweave that is starting (at sign-in, or for another agent a moment ago) gets as long as one
// this bridge starts itself; one not running at all is started in the background. Both stay under
// Codex's 10 s MCP startup timeout. Past that, the client hears one line rather than a long hang.
(Link? link, string why) = await Reach(TimeSpan.FromSeconds(9));
if (link is null) return await Say(why switch
{
    NotOpen => why + " Open Deskweave, then reconnect this MCP server.",
    NotAnswering => why + " Restart Deskweave, then reconnect this MCP server.",
    _ => why,
}, why == Mismatched ? 2 : 1);
if (link.Send(context, new(null, "deskweave/context", Discard: true)) is { } sent) await sent;

try
{
    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
    var buffer = new char[4096];
    var line = new StringBuilder();
    while (true)
    {
        int read = await reader.ReadAsync(buffer);
        // The provider closed its end: this session is over, whatever Deskweave is doing.
        if (read == 0) return 0;
        for (int i = 0; i < read; i++)
        {
            char next = buffer[i];
            if (next != '\n')
            {
                if (line.Length >= WorkspacePipeProtocol.MaxRequestBytes)
                    return await Say("An MCP request was larger than Deskweave accepts.", 1);
                line.Append(next);
                continue;
            }
            string message = line.ToString().TrimEnd('\r');
            line.Clear();
            if (message.Length > 0) await Forward(message);
        }
    }
}
finally { link?.Dispose(); }

// One request from the agent. Deskweave closing or restarting mid-session does not end the session:
// the next request finds it again, and until then each request is answered with one line.
async Task Forward(string message)
{
    Expect expect = Read(message);
    if (expect.Method == "initialize") hello = message;
    if (link is null || link.Broken)
    {
        link?.Dispose();
        (link, why) = await Reach(TimeSpan.FromSeconds(1));
        if (link is not null)
        {
            // A new session on Deskweave's side: it hears where the agent works and who it is again.
            if (link.Send(context, new(null, "deskweave/context", Discard: true)) is { } sending) await sending;
            if (hello is not null && expect.Method != "initialize"
                && link.Send(hello, Read(hello) with { Discard = true }) is { } greeting) await greeting;
        }
    }
    if (link?.Send(message, expect) is { } forwarding) await forwarding;
    else Unreachable(expect, link is null ? why : Closed);
}

void Unreachable(Expect expect, string reason)
{
    if (expect.Id is null || expect.Discard) return;
    string text = reason switch
    {
        NotAnswering => reason + " Ask the owner to restart Deskweave, then try again.",
        Mismatched => reason,
        _ => reason + " Ask the owner to open Deskweave, then try again.",
    };
    string reply = expect.Method == "tools/call"
        ? "{\"jsonrpc\":\"2.0\",\"id\":" + expect.Id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\""
            + JsonEncodedText.Encode(text) + "\"}],\"isError\":true}}"
        : "{\"jsonrpc\":\"2.0\",\"id\":" + expect.Id + ",\"error\":{\"code\":-32000,\"message\":\"" + JsonEncodedText.Encode(text) + "\"}}";
    Write(reply);
}

void Write(string json)
{
    lock (output) stdout.WriteLine(json);
}

static Expect Read(string message)
{
    try
    {
        using var json = JsonDocument.Parse(message);
        if (json.RootElement.ValueKind != JsonValueKind.Object) return new(null, "", false);
        string method = json.RootElement.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "" : "";
        string? id = json.RootElement.TryGetProperty("id", out JsonElement raw)
            && raw.ValueKind is JsonValueKind.Number or JsonValueKind.String ? raw.GetRawText() : null;
        return new(id, method, false);
    }
    catch (JsonException) { return new(null, "", false); }
}

// Joins Deskweave, starting it first when it is not running: an agent that needs a screen should not
// have to ask the owner to open an app. A Deskweave that is running but still starting gets the
// patience. Only the real install's router ticket is started or waited on that long; any other
// ticket (a probe's fixture store) gets two seconds and never starts an app.
async Task<(Link?, string)> Reach(TimeSpan patience)
{
    if (App() is not { } app) return await Open(TimeSpan.FromSeconds(Math.Min(patience.TotalSeconds, 2)));
    bool running = Running();
    (Link? found, string reason) = await Open(running ? patience : TimeSpan.Zero);
    if (found is null && reason == NotOpen && !running && Launch(app)) (found, reason) = await Open(TimeSpan.FromSeconds(9));
    return (found, reason);
}

// The Deskweave this bridge was installed with, when the ticket is that install's own router ticket.
string? App()
{
    string app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Deskweave.exe"));
    string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deskweave", "agent-workspaces.access", "router.json");
    return ticket is not null && string.Equals(Path.GetFullPath(ticket), home, StringComparison.OrdinalIgnoreCase)
        && File.Exists(app) ? app : null;
}

// Starts it in the tray the way Start with Windows does, so nothing opens on the owner's screen.
// Provider variables from the agent's session are put back to what the owner's account has, so
// the app sees the same config homes as when Windows starts it.
static bool Launch(string app)
{
    var start = new ProcessStartInfo(app, "--background") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
    foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
        || k.StartsWith("CODEX", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DESKWEAVE_", StringComparison.OrdinalIgnoreCase)).ToList())
    {
        string? own = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        if (own is null) start.Environment.Remove(name); else start.Environment[name] = own;
    }
    try { Process.Start(start)?.Dispose(); return true; }
    catch (Win32Exception) { return false; }
}

// Whether a Deskweave holds its one-per-account lock, starting or running. Must match App.xaml.cs.
static bool Running()
{
    string user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.UserDomainName + "\\" + Environment.UserName)))[..20];
    if (!Mutex.TryOpenExisting("Local\\Deskweave.App." + user, out Mutex? instance)) return false;
    instance.Dispose();
    return true;
}

// Finds the pipe the ticket names and joins it. The ticket is read again on every attempt: a
// Deskweave that restarted serves a new pipe under a new ticket.
async Task<(Link?, string)> Open(TimeSpan patience)
{
    long until = Environment.TickCount64 + (long)patience.TotalMilliseconds;
    while (true)
    {
        (string pipeName, string? key, string reason) = ticket is null ? (fixedPipe, capability, NotOpen) : Ticket(ticket);
        if (reason == Mismatched) return (null, reason);
        if (key is not null && Served(pipeName))
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                // What is left of the wait, not a fresh ten seconds: a pipe that shows up at the end
                // of a startup wait must not push the answer past the client's own timeout.
                using var bound = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                    Math.Clamp(until - Environment.TickCount64, 500, 10000)));
                await pipe.ConnectAsync(bound.Token);
                await WorkspacePipeProtocol.Write(pipe, key, 256, bound.Token);
                if (await WorkspacePipeProtocol.Read(pipe, 256, bound.Token) == "workspace-pipe/1")
                    return (new Link(pipe, Write, Unreachable), "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
                or OperationCanceledException or InvalidDataException or DecoderFallbackException) { }
            pipe.Dispose();
            reason = NotAnswering;
        }
        if (Environment.TickCount64 >= until) return (null, reason);
        await Task.Delay(200);
    }
}

// Deskweave writes a ticket whole or not at all, so one it cannot read is not a half-written one:
// it is from a different version, and waiting will not change it.
static (string Pipe, string? Key, string Reason) Ticket(string path)
{
    try
    {
        var file = new FileInfo(path);
        if (!file.Exists) return ("", null, NotOpen);
        if (file.Length > 4096) return ("", null, Mismatched);
        using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
        JsonElement root = json.RootElement;
        string pipe = root.GetProperty("pipe").GetString() ?? "";
        string? key = root.GetProperty("capability").GetString();
        return root.GetProperty("schema").GetInt32() == 1 && pipe.StartsWith("Deskweave.Workspace.", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(key) ? (pipe, key, NotOpen) : ("", null, Mismatched);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ("", null, NotOpen); }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
    { return ("", null, Mismatched); }
}

// Asks Windows whether anyone serves the pipe, without connecting: a ticket left by a Deskweave
// that crashed names a pipe that is gone, and waiting on it only delays the answer.
static bool Served(string pipe)
{
    if (pipe.Length == 0) return false;
    if (WaitNamedPipe(@"\\.\pipe\" + pipe, 1)) return true;
    return Marshal.GetLastWin32Error() is not (2 or 123 or 161);
}

static async Task<int> Say(string line, int code)
{
    await Console.Error.WriteLineAsync(line);
    return code;
}

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WaitNamedPipeW")]
static extern bool WaitNamedPipe(string name, uint timeoutMs);

/// <summary>What the agent is waiting for from one request: its id as written, and whether anyone reads the reply.</summary>
sealed record Expect(string? Id, string Method, bool Discard);

/// <summary>
/// One connection to Deskweave. Deskweave answers every request frame with exactly one frame, in
/// order, an empty one for a notification, so replies are matched by position. When the pipe goes,
/// every request still waiting is answered at once instead of being left to time out.
/// </summary>
sealed class Link : IDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly Queue<Expect> _waiting = new();
    readonly Action<string> _write;
    readonly Action<Expect, string> _unreachable;
    readonly CancellationTokenSource _stop = new();
    bool _broken;

    internal Link(NamedPipeClientStream pipe, Action<string> write, Action<Expect, string> unreachable)
    {
        (_pipe, _write, _unreachable) = (pipe, write, unreachable);
        _ = Task.Run(Pump);
    }

    internal bool Broken { get { lock (_waiting) return _broken; } }

    /// <summary>Sends one message, or returns null when the connection is already gone.</summary>
    internal Task? Send(string message, Expect expect)
    {
        lock (_waiting)
        {
            if (_broken) return null;
            _waiting.Enqueue(expect);
        }
        return Deliver(message);
    }

    async Task Deliver(string message)
    {
        try { await WorkspacePipeProtocol.Write(_pipe, message, WorkspacePipeProtocol.MaxRequestBytes, _stop.Token); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
            or InvalidDataException) { Break(); }
    }

    async Task Pump()
    {
        try
        {
            while (await WorkspacePipeProtocol.Read(_pipe, WorkspacePipeProtocol.MaxResponseBytes, _stop.Token) is { } json)
            {
                Expect? expect;
                lock (_waiting) _waiting.TryDequeue(out expect);
                if (json.Length > 0 && expect?.Discard != true) _write(json);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
            or InvalidDataException or DecoderFallbackException) { }
        Break();
    }

    void Break()
    {
        Expect[] left;
        lock (_waiting)
        {
            if (_broken) return;
            _broken = true;
            left = [.. _waiting];
            _waiting.Clear();
        }
        _stop.Cancel();
        _pipe.Dispose();
        foreach (Expect expect in left) _unreachable(expect, "Deskweave closed while this was running.");
    }

    public void Dispose()
    {
        lock (_waiting) _broken = true;
        _stop.Cancel();
        _pipe.Dispose();
    }
}
