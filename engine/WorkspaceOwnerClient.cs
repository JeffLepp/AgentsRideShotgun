using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// Owner interfaces attach to the existing controller; disposing a request never stops a
/// workspace. The ticket stays on disk and its capability is never returned to the caller.
/// </summary>
public static class WorkspaceOwnerClient
{
    public static string CanonicalTicket => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARS", "agent-workspaces.access", "router.json");

    public static async Task<JsonElement> RequestAsync(string operation, string? workspace = null,
        CancellationToken cancellationToken = default, string? ticketPath = null, int? clientPid = null)
    {
        if (operation is not ("snapshot" or "show" or "frame" or "admit")) throw new ArgumentException("Unsupported owner operation.", nameof(operation));
        string path = ticketPath ?? CanonicalTicket;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The controller ticket path must be absolute.", nameof(ticketPath));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var ticketFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (ticketFile.Length > 4096) throw new InvalidDataException("Invalid controller ticket.");
        using var ticket = await JsonDocument.ParseAsync(ticketFile, cancellationToken: timeout.Token).ConfigureAwait(false);
        JsonElement root = ticket.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported controller ticket.");
        string pipe = root.GetProperty("pipe").GetString() ?? "";
        string capability = root.GetProperty("capability").GetString() ?? "";
        if (!pipe.StartsWith("Deskweave.Workspace.", StringComparison.Ordinal) || pipe.Length > 128)
            throw new InvalidDataException("Invalid controller pipe.");
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
        await WorkspacePipeProtocol.Write(client, capability, 256, timeout.Token).ConfigureAwait(false);
        if (await WorkspacePipeProtocol.Read(client, 256, timeout.Token).ConfigureAwait(false) != "workspace-pipe/1")
            throw new IOException("The controller did not authenticate this connection.");
        await WorkspacePipeProtocol.Write(client, JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "deskweave/owner", @params = new { operation, workspace, clientPid },
        }), WorkspacePipeProtocol.MaxRequestBytes, timeout.Token).ConfigureAwait(false);
        string? reply = await WorkspacePipeProtocol.Read(client, WorkspacePipeProtocol.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
        if (reply is null) throw new IOException("The controller closed before acknowledging the request.");
        using var response = JsonDocument.Parse(reply);
        if (response.RootElement.TryGetProperty("error", out JsonElement error))
            throw new InvalidOperationException(error.GetProperty("message").GetString() ?? "Controller request failed.");
        JsonElement result = response.RootElement.GetProperty("result");
        if (result.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("Unsupported owner interface protocol.");
        return result.Clone();
    }
}
