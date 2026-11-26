using System.IO;
using System.Text.Json;

namespace Deskweave.AgentWorkspaces;

internal sealed record CategoryUsage(string Category, long Bytes);
internal sealed record WorkspaceUsage(string Id, string Name, long Bytes, long ProfileBytes, long CacheBytes, bool Running, DateTimeOffset? LastUsed);
internal sealed record StorageUsage(DateTimeOffset MeasuredAt, long TotalBytes, CategoryUsage[] Categories, WorkspaceUsage[] Workspaces, string[] Warnings)
{
    internal static StorageUsage From(StorageReport report) => new(report.MeasuredAt, report.TotalBytes,
        report.Items.GroupBy(i => i.Category).Select(g => new CategoryUsage(g.Key, g.Sum(i => i.Bytes)))
            .OrderByDescending(c => c.Bytes).ToArray(),
        report.Items.Where(i => i.WorkspaceId is not null).GroupBy(i => i.WorkspaceId).Select(g =>
        {
            var first = g.First();
            return new WorkspaceUsage(first.WorkspaceId!, first.Name, g.Sum(i => i.Bytes), g.Where(i => i.Category == "browser-profiles").Sum(i => i.Bytes),
                g.Where(i => i.Category == "caches").Sum(i => i.Bytes), first.Running, first.LastUsed);
        }).OrderByDescending(w => w.Bytes).ToArray(), report.Warnings);
}

public sealed partial class WorkspaceStorage
{
    static readonly object UsageLock = new();

    // Display only. The cleanup engine always measures and checks live files independently.
    internal StorageUsage Usage(bool refresh = false)
    {
        lock (UsageLock)
        {
            string path = Path.Combine(_data, "storage-usage-v1.json");
            if (!refresh)
            {
                try
                {
                    using var input = File.OpenRead(path);
                    var saved = JsonSerializer.Deserialize<StorageUsage>(input, Json);
                    if (saved is { MeasuredAt: var when, TotalBytes: >= 0, Categories: not null, Workspaces: not null, Warnings: not null }
                        && when != default && saved.Categories.All(c => c is { Category: not null, Bytes: >= 0 })
                        && saved.Workspaces.All(w => w is { Id.Length: > 0, Name: not null, Bytes: >= 0 })
                        && saved.Warnings.All(w => w is not null)) return saved;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }

            var usage = StorageUsage.From(Report());
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_data);
                using (var output = File.Create(pending)) JsonSerializer.Serialize(output, usage, Json);
                File.Move(pending, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* The measurement is still usable. */ }
            finally
            {
                try { File.Delete(pending); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return usage;
        }
    }
}
