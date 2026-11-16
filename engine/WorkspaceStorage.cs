using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deskweave.Product;

namespace Deskweave.AgentWorkspaces;

public sealed record StorageFile(string Path, long Bytes, DateTime LastWriteUtc);
public sealed record StorageItem(string Category, string Name, string Path, long Bytes,
    string? WorkspaceId, DateTimeOffset? LastUsed, bool Running, bool UserData,
    [property: System.Text.Json.Serialization.JsonIgnore] StorageFile[] Files);
public sealed record StorageReport(DateTimeOffset MeasuredAt, long TotalBytes, StorageItem[] Items,
    string[] Warnings, StoragePolicy Policy);
public sealed record StoragePolicy
{
    public bool Automatic { get; init; } = true;
    public int CacheCapMb { get; init; } = 256;
    public int LogCapMb { get; init; } = 64;
    public int LogDays { get; init; } = 30;
    public int UnusedDays { get; init; } = 30;
    public StoragePolicy Sane() => this with { CacheCapMb = Math.Clamp(CacheCapMb, 16, 16384),
        LogCapMb = Math.Clamp(LogCapMb, 1, 4096), LogDays = Math.Clamp(LogDays, 1, 3650), UnusedDays = Math.Clamp(UnusedDays, 1, 3650) };
}
public sealed record StorageCleanup(string Category, bool DryRun, long PreviewBytes, long FreedBytes,
    string[] Paths, string[] Skipped, string[] Errors,
    [property: System.Text.Json.Serialization.JsonIgnore] StorageFile[] ReviewedFiles);

/// <summary>Logical file lengths, counted once. Caches are an allowlist, never a whole profile.
/// Cleanup is planned and revalidated under the same admission lock as workspace startup.</summary>
public sealed class WorkspaceStorage
{
    readonly string _data, _install, _workspaces;
    readonly Func<string, bool> _running;
    readonly Func<string, Func<bool>, bool> _idle;
    static readonly SemaphoreSlim Gate = new(1, 1);
    public static WorkspaceStorage Default => new(ProductContext.LocalRoot,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARSApp"),
        WorkspaceStore.Root, IsRunning, WorkspaceRuntime.WithIdleStorage);

    internal WorkspaceStorage(string data, string install, string workspaces, Func<string, bool> running,
        Func<string, Func<bool>, bool> idle)
    { _data = Path.GetFullPath(data); _install = Path.GetFullPath(install); _workspaces = Path.GetFullPath(workspaces); _running = running; _idle = idle; }

    internal static bool IsRunning(string id)
    {
        if (id.Length > 0) return WorkspaceRuntime.Of(id) is not null
            || WorkspaceAccessStore.TicketPipe(WorkspaceAccessStore.Connection(id)) is { } pipe && WorkspacePipeServer.Exists(pipe);
        if (WorkspaceRuntime.AnyRunning) return true;
        try
        {
            return Directory.Exists(WorkspaceAccessStore.Root) && Directory.EnumerateDirectories(WorkspaceAccessStore.Root)
                .Any(folder => WorkspaceAccessStore.TicketPipe(Path.Combine(folder, "connection.json")) is { } pipe && WorkspacePipeServer.Exists(pipe));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    public StoragePolicy Policy
    {
        get
        {
            try { return (JsonSerializer.Deserialize<StoragePolicy>(File.ReadAllText(Path.Combine(_data, "storage-policy.json")), Json) ?? new()).Sane(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }
        set
        {
            Directory.CreateDirectory(_data);
            string path = Path.Combine(_data, "storage-policy.json");
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(pending, JsonSerializer.Serialize(value.Sane(), Json)); File.Move(pending, path, true); }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StorageReport Report()
    {
        var items = new List<StorageItem>();
        var warnings = new List<string>();
        foreach (string folder in Children(_workspaces, warnings).Where(Directory.Exists))
        {
            if (!Safe(folder, _workspaces)) { warnings.Add("Skipped link: " + folder); continue; }
            StoredWorkspace? stored = null;
            try { stored = JsonSerializer.Deserialize<StoredWorkspace>(File.ReadAllText(Path.Combine(folder, "workspace.json"))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { warnings.Add("Workspace record unavailable: " + folder); }
            string id = Path.GetFileName(folder);
            if (id.StartsWith("ARS-", StringComparison.Ordinal)) id = id[4..];
            bool valid = id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
            bool running = !valid || _running(id);
            var groups = Scan(folder, warnings).GroupBy(f => WorkspaceCategory(Path.GetRelativePath(folder, f.Path))).ToArray();
            if (groups.Length == 0) Add("workspace-data", stored?.Name ?? id, folder, [], id, stored?.LastUsed, running, true);
            foreach (var group in groups) Add(group.Key, stored?.Name ?? id, folder, group.ToArray(), id, stored?.LastUsed, running,
                group.Key is "workspace-data" or "browser-profiles");
        }
        Add("settings", "Workspace metadata", _workspaces, Children(_workspaces, warnings)
            .Where(p => File.Exists(p) && Safe(p, _workspaces)).Select(p => new FileInfo(p))
            .Select(f => new StorageFile(f.FullName, f.Length, f.LastWriteTimeUtc)).ToArray());
        // Include orphan files and metadata under the product root without counting workspaces twice.
        foreach (var group in Scan(_data, warnings, _workspaces).GroupBy(f => RootCategory(Path.GetRelativePath(_data, f.Path))))
            Add(group.Key, Label(group.Key), _data, group.ToArray(), userData: group.Key == "workspace-data");
        foreach (var group in Scan(_install, warnings).GroupBy(f => InstallCategory(Path.GetRelativePath(_install, f.Path))))
            Add(group.Key, Label(group.Key), _install, group.ToArray());
        // Roaming ARS settings are small but still included in the product total.
        if (string.Equals(_data, ProductContext.LocalRoot, StringComparison.OrdinalIgnoreCase))
            Add("settings", "Settings and connections", ProductContext.RoamingRoot, Scan(ProductContext.RoamingRoot, warnings).ToArray());
        return new(DateTimeOffset.UtcNow, items.Sum(i => i.Bytes), items.ToArray(), warnings.ToArray(), Policy);

        void Add(string category, string name, string path, StorageFile[] files, string? id = null,
            DateTimeOffset? last = null, bool running = false, bool userData = false)
        { items.Add(new(category, name, path, files.Sum(f => f.Bytes), id, last, running, userData, files)); }
    }

    public static string Label(string category) => category switch
    {
        "workspace-data" => "Workspace files and app data", "browser-profiles" => "Browser profiles and sign-ins",
        "caches" => "Browser caches", "captures" => "Screen captures", "logs" => "Logs", "temp" => "Temporary files",
        "packages" => "Update packages", "versions" => "Previous versions and rollback", "current" => "Installed app",
        "bridge-hosts" => "Agent bridge hosts", _ => "Settings and other ARS files",
    };

    internal static string WorkspaceCategory(string relative)
    {
        string p = relative.Replace('\\', '/');
        string[] parts = p.Split('/');
        if (parts[0].Equals("chrome-profile", StringComparison.OrdinalIgnoreCase))
        {
            // Chromium's known disposable caches only. Cookies, Local Storage, IndexedDB,
            // Service Worker data, Sessions, passwords and downloads are always user data.
            int cache = parts.Length > 2 && (parts[1].Equals("Default", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(parts[1], @"^Profile \d+$")) ? 2 : 1;
            return parts.Length > cache + 1 && new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache", "ShaderCache" }
                .Contains(parts[cache], StringComparer.OrdinalIgnoreCase) ? "caches" : "browser-profiles";
        }
        if (parts[0].Equals("temp", StringComparison.OrdinalIgnoreCase)) return "temp";
        if (parts[0].Equals("evidence", StringComparison.OrdinalIgnoreCase))
            return p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "logs" : "captures";
        if (parts[0].Equals("last-frame.png", StringComparison.OrdinalIgnoreCase)) return "captures";
        return "workspace-data";
    }
    static string RootCategory(string relative)
    {
        string p = relative.Replace('\\', '/');
        if (p.StartsWith("bridge-hosts/", StringComparison.OrdinalIgnoreCase)) return "bridge-hosts";
        if (p.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) || !p.Contains('/') && p.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return "logs";
        if (p.StartsWith("temp/", StringComparison.OrdinalIgnoreCase)) return "temp";
        if (p.StartsWith("agent-workspaces.results/", StringComparison.OrdinalIgnoreCase)) return "workspace-data";
        if (p.StartsWith("captures/", StringComparison.OrdinalIgnoreCase)) return "captures";
        return "settings";
    }
    static string InstallCategory(string relative)
    {
        string p = relative.Replace('\\', '/');
        if (p.StartsWith("current/", StringComparison.OrdinalIgnoreCase)) return "current";
        if (p.StartsWith("packages/", StringComparison.OrdinalIgnoreCase)) return "packages";
        if (p.StartsWith(".private-retained-", StringComparison.OrdinalIgnoreCase) || p.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return "versions";
        return "settings";
    }

    public async Task<StorageCleanup> Clean(string category, bool dryRun = true, bool retention = false, IReadOnlyList<StorageFile>? reviewed = null)
    {
        if (category is not ("caches" or "logs" or "captures" or "temp" or "workspaces" or "packages" or "versions" or "retention"))
            throw new ArgumentException("Choose caches, logs, captures, temp, workspaces, packages, versions, or retention.");
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StorageReport report = Report();
            StoragePolicy policy = report.Policy;
            var paths = new List<string>(); var skipped = new List<string>();
            var errors = new List<string>(report.Warnings.Where(w => !w.StartsWith("Workspace record unavailable:", StringComparison.Ordinal)));
            skipped.AddRange(report.Warnings.Where(w => w.StartsWith("Workspace record unavailable:", StringComparison.Ordinal)));
            var selectedFiles = new List<StorageFile>();
            var approved = reviewed?.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            long preview = 0, freed = 0;
            bool automatic = retention || category == "retention";
            foreach (var workspace in report.Items.Where(i => i.WorkspaceId is not null).GroupBy(i => i.WorkspaceId!))
            {
                StorageItem first = workspace.First();
                if (first.Running) { skipped.Add(first.Name + ": workspace is running"); continue; }
                bool removeWorkspace = category == "workspaces" && first.LastUsed is { } used
                    && used < DateTimeOffset.UtcNow.AddDays(-policy.UnusedDays);
                StorageFile[] selected = removeWorkspace ? workspace.SelectMany(i => i.Files).ToArray()
                    : workspace.Where(i => category == i.Category || automatic && i.Category is "caches" or "logs" or "captures" or "temp")
                        .SelectMany(i => Select(i, automatic, policy)).ToArray();
                if (selected.Length == 0) continue;
                bool accepted = _idle(workspace.Key, () =>
                {
                    if (_running(workspace.Key)) return false;
                    if (removeWorkspace)
                    {
                        try
                        {
                            var current = JsonSerializer.Deserialize<StoredWorkspace>(File.ReadAllText(Path.Combine(first.Path, "workspace.json")));
                            if (current?.LastUsed != first.LastUsed)
                            { skipped.Add(first.Name + ": last use changed before cleanup; skipped"); return true; }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                        { skipped.Add("Workspace record unavailable: " + first.Path); return true; }
                    }
                    foreach (StorageFile file in selected) Delete(file, first.Path);
                    if (removeWorkspace && !dryRun && errors.Count == 0) RemoveEmpty(first.Path, _workspaces);
                    return true;
                });
                if (!accepted) skipped.Add(first.Name + ": started before cleanup; skipped");
            }
            foreach (StorageItem item in report.Items.Where(i => i.WorkspaceId is null))
            {
                // Global temp/captures/logs may be used by any live workspace. Be conservative.
                if ((category == item.Category || automatic) && item.Category is "logs" or "temp" or "captures")
                {
                    bool accepted = _idle("", () =>
                    {
                        if (_running("") || report.Items.Any(i => i.WorkspaceId is { } id && _running(id))) return false;
                        foreach (StorageFile file in Select(item, automatic, policy)) Delete(file, _data);
                        return true;
                    });
                    if (!accepted) skipped.Add(item.Name + ": a workspace is running");
                }
            }
            if (category is "packages" or "versions")
            {
                // An updater owns its files. Automatic retention never races update downloads.
                foreach (StorageFile file in OldInstallFiles(report, category)) Delete(file, _install);
            }
            return new(category, dryRun, preview, freed, paths.ToArray(), skipped.Distinct().ToArray(), errors.ToArray(), selectedFiles.ToArray());

            void Delete(StorageFile file, string root)
            {
                if (approved is not null && (!approved.TryGetValue(file.Path, out StorageFile? before)
                    || before.Bytes != file.Bytes || before.LastWriteUtc != file.LastWriteUtc))
                { skipped.Add("Not in preview or changed: " + file.Path); return; }
                if (!Safe(file.Path, root) || Inside(file.Path, Path.Combine(_install, "current")))
                { errors.Add("Protected or linked path: " + file.Path); return; }
                try
                {
                    var now = new FileInfo(file.Path);
                    if (!now.Exists) return;
                    if (now.Length != file.Bytes || now.LastWriteTimeUtc != file.LastWriteUtc)
                    { skipped.Add("Changed since preview: " + file.Path); return; }
                    preview += now.Length; paths.Add(file.Path); selectedFiles.Add(file);
                    if (dryRun) return;
                    long length = now.Length;
                    now.Delete();
                    freed += length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(file.Path + ": " + e.Message); }
            }
        }
        finally { Gate.Release(); }
    }

    static IEnumerable<StorageFile> Select(StorageItem item, bool retention, StoragePolicy policy)
    {
        if (!retention) return item.Files;
        long cap = item.Category switch { "caches" => policy.CacheCapMb * 1048576L, "logs" => policy.LogCapMb * 1048576L,
            "captures" => 1073741824L, _ => long.MaxValue };
        int days = item.Category switch { "logs" => policy.LogDays, "captures" or "temp" => 7, _ => int.MaxValue };
        DateTime cutoff = days == int.MaxValue ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
        long remaining = item.Bytes;
        var selected = new List<StorageFile>();
        foreach (StorageFile file in item.Files.OrderBy(f => f.LastWriteUtc))
            if (file.LastWriteUtc < cutoff || remaining > cap) { selected.Add(file); remaining -= file.Bytes; }
        return selected;
    }

    IEnumerable<StorageFile> OldInstallFiles(StorageReport report, string category)
    {
        // Only a verified installed version can authorize package/version pruning.
        string current = Path.Combine(_install, "current", "sq.version");
        if (!Safe(current, _install) || !File.Exists(current)) return [];
        Version? installed;
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(current);
            string? value = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
            if (!Version.TryParse(value?.Split('-')[0], out installed)) return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return []; }
        if (category == "packages")
        {
            var packages = report.Items.Where(i => i.Category == "packages").SelectMany(i => i.Files)
                .Select(f => (File: f, Match: Regex.Match(Path.GetFileName(f.Path), @"^ARSApp-(\d+\.\d+\.\d+)-full\.nupkg$", RegexOptions.IgnoreCase)))
                .Where(p => p.Match.Success).Select(p => (p.File, Version: Version.Parse(p.Match.Groups[1].Value))).ToArray();
            Version? rollback = packages.Where(p => p.Version < installed).Select(p => p.Version).OrderDescending().FirstOrDefault();
            return packages.Where(p => p.Version < installed && p.Version != rollback).Select(p => p.File).ToArray();
        }
        var folders = report.Items.Where(i => i.Category == "versions").SelectMany(i => i.Files)
            .GroupBy(f => Path.GetRelativePath(_install, f.Path).Split(Path.DirectorySeparatorChar)[0])
            .OrderByDescending(g => Directory.GetLastWriteTimeUtc(Path.Combine(_install, g.Key))).ToArray();
        // Private recovery archives cannot be identified reliably by version. Always retain them;
        // prune only version-labelled app-* trees older than installed, keeping the newest rollback.
        var old = folders.Select(g => (Group: g, Match: Regex.Match(g.Key, @"^app-(\d+\.\d+\.\d+)$")))
            .Where(p => p.Match.Success).Select(p => (p.Group, Version: Version.Parse(p.Match.Groups[1].Value)))
            .Where(p => p.Version < installed).OrderByDescending(p => p.Version).Skip(1);
        return old.SelectMany(p => p.Group).ToArray();
    }

    static IEnumerable<string> Children(string path, List<string> warnings)
    {
        try { return Directory.Exists(path) ? Directory.GetFileSystemEntries(path) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(path + ": " + e.Message); return []; }
    }
    static IEnumerable<StorageFile> Scan(string root, List<string> warnings, string? exclude = null)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(root));
        while (pending.TryPop(out DirectoryInfo? directory))
        {
            if (!Safe(directory.FullName, root)) { warnings.Add("Skipped link: " + directory.FullName); continue; }
            FileSystemInfo[] entries;
            try { entries = directory.GetFileSystemInfos(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(directory.FullName + ": " + e.Message); continue; }
            foreach (FileSystemInfo entry in entries)
            {
                if (exclude is not null && Inside(entry.FullName, exclude)) continue;
                StorageFile? file = null;
                try
                {
                    // Enumeration already returned attributes, length and timestamps. Validate
                    // ancestors once per directory, then consume that metadata without opening
                    // every file or walking its ancestors again. Deletion still revalidates each path.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add("Skipped link: " + entry.FullName); continue; }
                    if (entry is DirectoryInfo child) { pending.Push(child); continue; }
                    if (entry is FileInfo info) file = new(info.FullName, info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add(entry.FullName + ": " + e.Message); }
                if (file is not null) yield return file;
            }
        }
    }
    internal static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);
    internal static bool Safe(string path, string root)
    {
        if (!Inside(path, root)) return false;
        try
        {
            for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    static void RemoveEmpty(string path, string root)
    {
        if (!Safe(path, root)) return;
        foreach (string directory in Directory.GetDirectories(path)) RemoveEmpty(directory, root);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }
}
