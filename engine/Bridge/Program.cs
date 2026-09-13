using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

// stdout belongs exclusively to MCP. The host-issued capability arrives through the child
// environment, never a persisted provider configuration or an argument to this bridge.
string? capability = Environment.GetEnvironmentVariable("DESKWEAVE_WORKSPACE_PIPE_KEY");
Environment.SetEnvironmentVariable("DESKWEAVE_WORKSPACE_PIPE_KEY", null);
string pipeName;
try
{
    if (args.Length == 2 && args[0] == "--workspace" && Path.IsPathFullyQualified(args[1]))
    {
        var file = new FileInfo(args[1]);
        if (!file.Exists || file.Length > 4096) throw new IOException();
        using var ticket = JsonDocument.Parse(File.ReadAllText(file.FullName));
        if (ticket.RootElement.GetProperty("schema").GetInt32() != 1) return 2;
        pipeName = ticket.RootElement.GetProperty("pipe").GetString() ?? "";
        capability = ticket.RootElement.GetProperty("capability").GetString();
    }
    else if (args.Length == 1) pipeName = args[0];
    else return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
    or KeyNotFoundException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync("Deskweave isn't running. Open Deskweave, then restart this MCP connection.");
    return 1;
}
if (!pipeName.StartsWith("Deskweave.Workspace.", StringComparison.Ordinal)) return 2;
if (string.IsNullOrEmpty(capability)) return 2;
using var stop = new CancellationTokenSource();
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
try
{
    await pipe.ConnectAsync(10000, stop.Token);
    await WorkspacePipeProtocol.Write(pipe, capability, 256, stop.Token);
    if (await WorkspacePipeProtocol.Read(pipe, 256, stop.Token) != "workspace-pipe/1") return 3;
    // Where the agent is working, so Deskweave can give each project its own workspace. Claude
    // Code names its project; other clients start their servers in theirs.
    string cwd = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } project
        ? project : Environment.CurrentDirectory;
    await WorkspacePipeProtocol.Write(pipe, "{\"jsonrpc\":\"2.0\",\"method\":\"deskweave/context\",\"params\":{\"cwd\":\""
        + JsonEncodedText.Encode(cwd) + "\"}}", WorkspacePipeProtocol.MaxRequestBytes, stop.Token);
    Task input = Input();
    Task output = Output();
    Task finished = await Task.WhenAny(input, output);
    // The provider's EOF or the host's disconnect ends this adapter. Console stdin can have a
    // non-cancellable read on Windows; waiting for both pumps would keep an orphan bridge alive.
    await finished;
    stop.Cancel();
    pipe.Dispose();
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
    or OperationCanceledException or ObjectDisposedException)
{
    await Console.Error.WriteLineAsync("Deskweave isn't reachable. Open Deskweave, then restart this MCP connection.");
    return 1;
}

async Task Input()
{
    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
    var buffer = new char[4096];
    var line = new StringBuilder();
    while (!stop.IsCancellationRequested)
    {
        int read = await reader.ReadAsync(buffer, stop.Token);
        if (read == 0) return;
        for (int i = 0; i < read; i++)
        {
            char next = buffer[i];
            if (next == '\n')
            {
                if (line.Length > 0)
                    await WorkspacePipeProtocol.Write(pipe, line.ToString().TrimEnd('\r'),
                        WorkspacePipeProtocol.MaxRequestBytes, stop.Token);
                line.Clear();
            }
            else
            {
                if (line.Length >= WorkspacePipeProtocol.MaxRequestBytes)
                    throw new InvalidDataException("Workspace request exceeds its limit.");
                line.Append(next);
            }
        }
    }
}

async Task Output()
{
    using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    while (!stop.IsCancellationRequested)
    {
        string? json = await WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxResponseBytes, stop.Token);
        if (json is null) return;
        if (json.Length > 0) await writer.WriteLineAsync(json.AsMemory(), stop.Token);
    }
}
