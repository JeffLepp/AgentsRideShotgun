using System.IO;
using System.Text.Json;

namespace HiveMind.AgentWorkspaces;

internal sealed record WorkspaceAccessPolicy(bool Enabled = false, bool DesktopRequests = true)
{
    // Missing fields in older owner policy retain the existing safe default.
    public bool BlockProgramsAfterWebContent { get; init; } = true;

    /// <summary>
    /// Start this workspace's Chrome when the workspace starts, rather than on the first browse.
    /// Measured 2026-09-07: a cold browse costs about forty seconds, which an agent reads as a hung
    /// tool. Not a permission - it opens no page and reads nothing - so a connecting client never
    /// tightens or widens it, and blocking after browser inspection is unaffected.
    /// </summary>
    public bool PrewarmBrowser { get; init; } = true;

    internal WorkspaceAccessPolicy TightenWith(WorkspaceAccessPolicy requested) => this with
    {
        Enabled = Enabled && requested.Enabled,
        DesktopRequests = DesktopRequests && requested.DesktopRequests,
        BlockProgramsAfterWebContent = BlockProgramsAfterWebContent || requested.BlockProgramsAfterWebContent,
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
    internal static void Publish(string id, WorkspacePipeServer server) => WriteJson(Connection(id),
        new { schema = 1, pipe = server.Name, capability = server.Capability });
    internal static void Withdraw(string id)
    {
        try { File.Delete(Connection(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>The one ticket every connected agent's bridge reads. Its path is what goes in their configuration.</summary>
    internal static string RouterTicket => Path.Combine(Root, "router.json");
    internal static void PublishRouter(WorkspacePipeServer server) =>
        WriteJson(RouterTicket, new { schema = 1, pipe = server.Name, capability = server.Capability });
    internal static void WithdrawRouter()
    {
        try { File.Delete(RouterTicket); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
