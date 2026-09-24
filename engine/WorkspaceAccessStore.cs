using System.IO;
using System.Text.Json;

namespace Deskweave.AgentWorkspaces;

internal sealed record WorkspaceAccessPolicy(bool Enabled = false, bool DesktopRequests = true)
{
    /// <summary>
    /// Start this workspace's Chrome when the workspace starts, rather than on the first browse.
    /// Measured: a cold browse costs about forty seconds, which an agent reads as a hung
    /// tool. Not a permission - it opens no page and reads nothing - so a connecting client never
    /// tightens or widens it.
    /// </summary>
    public bool PrewarmBrowser { get; init; } = true;

    internal WorkspaceAccessPolicy TightenWith(WorkspaceAccessPolicy requested) => this with
    {
        Enabled = Enabled && requested.Enabled,
        DesktopRequests = DesktopRequests && requested.DesktopRequests,
    };
}

/// <summary>Owner policy and live connection tickets must NOT live in the Low-writable workspace.</summary>
internal static class WorkspaceAccessStore
{
    internal static string Root => WorkspaceStore.Root + ".access";
    internal static string ResultsRoot => WorkspaceStore.Root + ".results";
    internal static string Folder(string id)
    {
        if (id.Length is < 1 or > 80 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid workspace identity.", nameof(id));
        return Path.Combine(Root, id);
    }
    internal static string Connection(string id) => Path.Combine(Folder(id), "connection.json");
    internal static WorkspaceAccessPolicy Read(string id)
    {
        try
        {
            string path = Path.Combine(Folder(id), "access.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            return JsonSerializer.Deserialize<WorkspaceAccessPolicy>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Write(string id, WorkspaceAccessPolicy policy) =>
        WriteJson(Path.Combine(Folder(id), "access.json"), policy);
    internal static void Publish(string id, WorkspacePipeServer server) => WriteJson(Connection(id), Ticket(server));
    internal static void Withdraw(string id)
    {
        try { File.Delete(Connection(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>Removes a workspace's ticket only while it still names this server, or nothing live.</summary>
    internal static void Withdraw(string id, WorkspacePipeServer? server) => WithdrawTicket(Connection(id), server);
    /// <summary>The one ticket every connected agent's bridge reads. Its path is what goes in their configuration.</summary>
    internal static string RouterTicket => Path.Combine(Root, "router.json");
    internal static void PublishRouter(WorkspacePipeServer server) => WriteJson(RouterTicket, Ticket(server));
    internal static void WithdrawRouter(WorkspacePipeServer server) => WithdrawTicket(RouterTicket, server);

    static object Ticket(WorkspacePipeServer server) => new { schema = 1, pipe = server.Name, capability = server.Capability };

    /// <summary>The pipe a ticket names, or null when there is no readable ticket.</summary>
    internal static string? TicketPipe(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            using var ticket = JsonDocument.Parse(File.ReadAllText(path));
            return ticket.RootElement.TryGetProperty("pipe", out JsonElement pipe) && pipe.ValueKind == JsonValueKind.String
                && pipe.GetString() is { } name && name.StartsWith("Deskweave.Workspace.", StringComparison.Ordinal) ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Whether a ticket has to be written for this server: it is missing, unreadable, or names a pipe
    /// nobody serves any more. A ticket naming another live pipe is left alone, so two ARS instances on
    /// one account (two Windows sessions) never take turns overwriting each other's.
    /// </summary>
    internal static bool NeedsTicket(string path, WorkspacePipeServer server) =>
        TicketPipe(path) is not { } pipe || pipe != server.Name && !WorkspacePipeServer.Exists(pipe);

    /// <summary>
    /// Connection tickets left by an ARS that stopped without withdrawing them: a crash, a
    /// killed process, a restart. A bridge reading one would wait on a pipe nobody serves. Runs at
    /// startup, when nothing of this process's own can be among them.
    /// </summary>
    internal static int SweepStale()
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            var tickets = Directory.EnumerateDirectories(Root).Select(folder => Path.Combine(folder, "connection.json")).Append(RouterTicket);
            foreach (string ticket in tickets)
            {
                if (!File.Exists(ticket) || TicketPipe(ticket) is { } pipe && WorkspacePipeServer.Exists(pipe)) continue;
                try { File.Delete(ticket); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    static void WithdrawTicket(string path, WorkspacePipeServer? server)
    {
        if (TicketPipe(path) is { } pipe && pipe != server?.Name && WorkspacePipeServer.Exists(pipe)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // UTF-8 with no byte order mark: the same bytes File.WriteAllText put here before.
    static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // The rename is atomic, but the contents are not on the disk yet when it runs: Windows can
            // record the rename while the bytes are still in the file cache, and a power loss then leaves
            // a ticket that is named right and empty. A bridge reading one waits out its whole connect
            // timeout for a pipe it never learns. Flushing to the device first means the disk holds
            // either the old file or the whole new one.
            byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(value));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
