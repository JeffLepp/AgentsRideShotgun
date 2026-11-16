using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

internal static class StorageSnapshot
{
    // Read-only delivery measurement. Product mutations remain on the authenticated router.
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        StorageReport report = WorkspaceStorage.Default.Report();
        File.WriteAllText(Path.Combine(output, "storage.json"), JsonSerializer.Serialize(report, WorkspaceStorage.Json));
        File.WriteAllText(Path.Combine(output, "measurement.json"), JsonSerializer.Serialize(new
        {
            report.TotalBytes, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
            workspaceCount = report.Items.Where(i => i.WorkspaceId is not null).Select(i => i.WorkspaceId).Distinct().Count(),
            running = report.Items.Where(i => i.Running).Select(i => i.WorkspaceId).Distinct().ToArray(),
            warningCount = report.Warnings.Length,
        }, WorkspaceStorage.Json));
        return 0;
    }
}
