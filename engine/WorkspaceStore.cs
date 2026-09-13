using System.IO;
using System.Text.Json;

namespace HiveMind.AgentWorkspaces;

/// <summary>Which Claude credential source a workspace boss is allowed to use.</summary>
public enum WorkspaceAgentCredentialMode
{
    /// <summary>Use the Claude subscription sign-in and ignore inherited API/provider overrides.</summary>
    Subscription,

    /// <summary>Use the API key or provider configuration already owned by Claude Code.</summary>
    ClaudeConfiguration,
}

/// <summary>
/// What a workspace is allowed to be. Not a security setting: see FREE_ROAM.md.
/// </summary>
public enum WorkspaceMode
{
    /// <summary>
    /// The owner's own desktop, on a second screen he is not looking at. The agent has his files,
    /// his programs, his network and his permissions, and nothing is withheld from it. The boundary
    /// is attention rather than privilege: the point is that an agent working does not take over
    /// the screen the owner is using. This is the product, and it is the default.
    /// </summary>
    Free,

    /// <summary>
    /// The same desktop with the optional restrictions turned on. A smaller promise, honestly
    /// labelled, and never a defence against hostile software - Windows Sandbox and virtual
    /// machines exist and this does not replace them.
    /// </summary>
    Secure,
}

/// <summary>One stored workspace. Everything here survives stopping, restarting and rebooting.</summary>
public sealed record StoredWorkspace
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    /// <summary>
    /// Fast by default, because a workspace that is slower than the owner's own desktop is not the
    /// product. Fast is not a cap - it schedules by weight - so an idle machine is given over to
    /// the agent and taken straight back when the owner wants it. Light is the opt-in for a
    /// workspace that must stay out of the way while he works.
    /// </summary>
    public WorkspacePower Power { get; init; } = WorkspacePower.Fast;

    /// <summary>Free unless the owner chose otherwise when the workspace was created.</summary>
    public WorkspaceMode Mode { get; init; } = WorkspaceMode.Free;

    /// <summary>
    /// Which outside agents Deskweave sends here. Empty: none, just the owner and the boss. See
    /// <see cref="WorkspaceHome"/> for the other values.
    /// </summary>
    public string Agents { get; init; } = string.Empty;
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastUsed { get; init; } = DateTimeOffset.Now;

    /// <summary>How this workspace's boss authenticates. The choice is per workspace.</summary>
    public WorkspaceAgentCredentialMode AgentCredentials { get; init; } =
        WorkspaceAgentCredentialMode.Subscription;

    /// <summary>
    /// Optional provider-reported dollar ceiling for one CLI invocation. Null means HiveMind does
    /// not add a ceiling; it never changes provider account limits.
    /// </summary>
    public long? RunUsageCeilingTokens { get; init; } = WorkspaceAgent.DefaultRunUsageCeilingTokens;

    /// <summary>
    /// Every token this workspace's boss agent has used, added up as each run ends. A workspace
    /// runs with its panel closed and over days, so the only place a total like this can live is
    /// the record; nothing in the process outlives the runs it would be counting.
    /// </summary>
    public long UsageTokensTotal { get; init; }

    /// <summary>
    /// The same runs at API prices, as the CLI reported them. A real charge only for credentials
    /// that are actually billed that way - a subscription is metered in tokens and in nothing else.
    /// </summary>
    public double UsageUsdTotal { get; init; }

    /// <summary>How many finished CLI invocations those two totals cover.</summary>
    public int UsageRuns { get; init; }

    /// <summary>
    /// This record with one finished run added to its totals. A run that used nothing - no CLI on
    /// the PC, not signed in, refused before it reached the model - is not a run and does not move
    /// the count, or a workspace that cannot run at all would report a history of runs it never
    /// made.
    /// </summary>
    public StoredWorkspace WithRun(long tokens, double usd) =>
        tokens <= 0 && usd <= 0 ? this : this with
        {
            UsageTokensTotal = UsageTokensTotal + Math.Max(0, tokens),
            UsageUsdTotal = UsageUsdTotal + (double.IsFinite(usd) && usd > 0 ? usd : 0),
            UsageRuns = UsageRuns + 1,
        };

    /// <summary>
    /// The agent CLI's own conversation for this workspace's mission. It is written down because a
    /// mission that outlives HiveMind has to be picked up by a different process than started it.
    /// </summary>
    public string Session { get; init; } = string.Empty;

    /// <summary>When a parked mission is to be woken. Null when nothing is waiting.</summary>
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>What the agent said it was waiting for, in its own words. Given back to it on waking.</summary>
    public string WakeNote { get; init; } = string.Empty;

    /// <summary>
    /// Whether the parked conversation has web page content in it. This has to be on the record
    /// rather than only on the control plane, because a parked mission tears its desktop down and
    /// wakes against a fresh one - so the flag would clear itself while the page text the flag
    /// exists for is still sitting in the conversation being handed back. That is the bypass, and
    /// it is why the taint travels with the session and not with the process.
    /// </summary>
    public bool SessionReadUntrustedContent { get; init; }

    /// <summary>
    /// How this workspace's mission was last left. Written down because an outcome the owner has
    /// not seen yet must survive closing the panel, closing HiveMind, and the machine restarting.
    /// A record still saying Working when HiveMind starts is a mission that was interrupted.
    /// </summary>
    public MissionState Mission { get; init; } = MissionState.Idle;

    /// <summary>What the mission said about itself when it reached that state.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>When it reached that state, for the card, the panel and the notification.</summary>
    public DateTimeOffset? MissionAt { get; init; }

    /// <summary>
    /// The last state the owner was actually told about. Kept so a restart does not re-announce an
    /// outcome he has already seen, which is the "no duplicate or stale alerts" half of the rule.
    /// </summary>
    public MissionState Announced { get; init; } = MissionState.Idle;
}

/// <summary>
/// The store is the folder tree, not an index file. A stored workspace is a directory under
/// <c>%LOCALAPPDATA%\Deskweave\agent-workspaces</c> holding its own <c>workspace.json</c>; listing
/// them is a directory scan and deleting one is deleting the folder.
///
/// This is the whole of what Milestone 2 has to persist under a desktop object. The original
/// Milestone 2 was written for a disposable Sandbox VM and needed a manifest, a verified installer
/// cache and deterministic reconstruction because the guest was wiped on every stop. A desktop
/// object wipes nothing: the files an agent wrote are still there when the workspace starts again,
/// so there is nothing to rebuild and nothing to promise about what survives.
///
/// There is deliberately no central list to corrupt. A workspace whose <c>workspace.json</c> is
/// missing or unreadable still appears, recovered from its folder name, because the folder is the
/// record of record.
/// </summary>
public static class WorkspaceStore
{
    const string RecordName = "workspace.json";
    const string FramePath = "last-frame.png";

    static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    static readonly Lock Records = new();

    /// <summary>
    /// Everything one workspace's programs and its last mission leave lying about: the redirected
    /// APPDATA, LOCALAPPDATA and TEMP, the workspace's own Chrome profile, HiveMind's evidence of
    /// the mission, and the picture of the screen it was stopped on. Nothing in here was written by
    /// the owner or by an agent, which is what makes clearing it a refresh rather than a delete.
    /// </summary>
    static readonly string[] MachineParts =
        ["appdata", "local", "temp", "chrome-profile", "evidence", FramePath];

    static string? _testRoot;

    /// <summary>
    /// Raised when a workspace is created, deleted or cleared - a change to which workspaces exist,
    /// which nothing else on screen can find out for itself. The dashboard reads its cards from the
    /// folder tree on demand, so before this a workspace deleted in the panel stayed on the
    /// dashboard until HiveMind was restarted.
    ///
    /// Deliberately not raised by <see cref="Save"/>: reading a folder with an unreadable record
    /// saves the recovered one while <see cref="All"/> is still enumerating, so a listener that
    /// re-read the store would call itself.
    /// </summary>
    public static event Action? Changed;

    public static string Root => _testRoot ?? HiveMind.Product.ProductContext.Local("agent-workspaces");

    /// <summary>
    /// Redirects workspace writes for one serialized test. Production callers never use this;
    /// keeping tests out of the owner's live workspace tree also lets them run when LocalAppData
    /// is deliberately unavailable.
    /// </summary>
    internal static IDisposable UseRootForTests(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A test workspace root is required.", nameof(root));
        if (_testRoot is not null)
            throw new InvalidOperationException("A test workspace-root override is already active.");

        string fullRoot = Path.GetFullPath(root);
        _testRoot = fullRoot;
        return new TestRootScope(fullRoot);
    }

    internal static string FolderForDesktop(string desktopName) => Path.Combine(Root, desktopName);

    /// <summary>Every stored workspace, oldest first, so the cards do not reshuffle themselves.</summary>
    public static IReadOnlyList<StoredWorkspace> All()
    {
        if (!Directory.Exists(Root)) return [];
        var found = new List<StoredWorkspace>();
        foreach (string folder in Directory.GetDirectories(Root))
        {
            StoredWorkspace? stored = Read(folder);
            if (stored is not null) found.Add(stored);
        }
        return found.OrderBy(workspace => workspace.Created).ToList();
    }

    /// <summary>Creates a workspace and its low integrity folder. Its id is its folder name.</summary>
    public static StoredWorkspace Create(string name)
    {
        var workspace = new StoredWorkspace { Id = FreeId(name), Name = name.Trim() };
        WorkspaceSandbox.EnsureFolder(AgentDesktop.NameFor(workspace.Id));
        Save(workspace);
        Changed?.Invoke();
        return workspace;
    }

    public static void Save(StoredWorkspace workspace)
    {
        lock (Records)
        {
            string folder = FolderOf(workspace.Id);
            if (!Directory.Exists(folder)) return;
            // Written beside the target and moved over it, so an interrupted save leaves the old record
            // intact rather than half of a new one.
            string pending = Path.Combine(folder, RecordName + ".new");
            File.WriteAllText(pending, JsonSerializer.Serialize(workspace, Format));
            File.Move(pending, Path.Combine(folder, RecordName), overwrite: true);
        }
    }

    /// <summary>One stored workspace by id, or null if it is not there any more.</summary>
    public static StoredWorkspace? Find(string id)
    {
        string folder = FolderOf(id);
        return Directory.Exists(folder) ? Read(folder) : null;
    }

    /// <summary>
    /// Changes part of a record without holding the rest of it. Two things write to a workspace's
    /// record now - a panel writing the name, the mission and the power, and a runtime writing the
    /// conversation and when to wake - and a runtime has no panel to ask, so each reads the file it
    /// is about to change rather than saving a copy it has been carrying.
    /// </summary>
    public static StoredWorkspace? Update(string id, Func<StoredWorkspace, StoredWorkspace> change)
    {
        // The supervisor saves from its worker thread while a panel can save on the UI thread.
        // Keep the complete read/modify/publish transaction together; atomic file replacement
        // by itself does not prevent two writers losing each other's fields or sharing .new.
        lock (Records)
        {
            StoredWorkspace? current = Find(id);
            if (current is null) return null;
            StoredWorkspace changed = change(current);
            Save(changed);
            return changed;
        }
    }

    /// <summary>Deletes the workspace and everything in it. Returns false if Windows held on to it.</summary>
    public static bool Delete(string id)
    {
        string folder = FolderOf(id);
        // Removing what is not there succeeds, and says nothing: deleting the same workspace twice
        // is not two changes, and a listener that redraws for the second one redraws for nothing.
        bool wasThere = Directory.Exists(folder);
        if (!Remove(folder)) return false;
        if (wasThere) Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Puts a workspace back to a clean machine without losing the workspace. Its programs' state,
    /// its browser profile, its temporary files, the evidence of the last mission and the
    /// conversation the agent was holding all go; its id, name, power preset, credential choice,
    /// run ceiling, its own files and its inbox all stay.
    ///
    /// This is the gap between stopping a workspace, which keeps every trace of the last task, and
    /// deleting it, which takes the files with it. Handing one workspace a different job needed one
    /// of those two to be something neither of them is.
    ///
    /// The caller stops the workspace's computer first: a running process holds its own current
    /// directory and Windows will not remove a folder underneath it. Returns false when something
    /// was still held, with <paramref name="workspace"/> set to the cleared record either way, so a
    /// caller can say what actually happened instead of reporting a refresh it did not get.
    /// </summary>
    public static bool FreshStart(string id, out StoredWorkspace? workspace)
    {
        workspace = null;
        string folder = FolderOf(id);
        if (!Directory.Exists(folder)) return false;

        // The record first, and the wake record with it. A parked mission is woken off the disk by
        // a clock that asks no panel's permission, so clearing the wake before the files go removes
        // the window where the waker starts a workspace whose app data is being deleted under it.
        workspace = Update(id, stored => stored with
        {
            Task = string.Empty,
            Session = string.Empty,
            WakeAt = null,
            WakeNote = string.Empty,
            SessionReadUntrustedContent = false,
            LastUsed = DateTimeOffset.Now,
        });
        if (workspace is null) return false;

        bool clean = true;
        foreach (string part in MachineParts) clean &= Remove(Path.Combine(folder, part));
        // Put the redirect targets back, so the next launch has somewhere to write. The workspace
        // folder keeps its (OI)(CI)Low label, so what is recreated inside it is labelled by
        // inheritance rather than by another icacls run.
        WorkspaceSandbox.EnsureFolder(AgentDesktop.NameFor(id));
        Changed?.Invoke();
        return clean;
    }

    /// <summary>
    /// Removes a file or a whole folder, retrying briefly. A process that has only just been killed
    /// can still hold a file open for a moment, and both deleting a workspace and refreshing one
    /// run immediately after something in it was killed.
    /// </summary>
    static bool Remove(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
        return !Directory.Exists(path) && !File.Exists(path);
    }

    public static string FolderOf(string id) =>
        WorkspaceSandbox.FolderFor(AgentDesktop.NameFor(id));

    /// <summary>What this workspace is costing on disk, in bytes.</summary>
    public static long SizeOf(string id, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        string folder = FolderOf(id);
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    /// <summary>Free space on the drive the workspaces live on. Below a gigabyte is worth saying.</summary>
    public static long FreeBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Root) ?? "C:\\").AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>Where the picture of a stopped workspace's last screen lives.</summary>
    public static string LastFrameOf(string id) => Path.Combine(FolderOf(id), FramePath);

    /// <summary>Where files the owner hands to a workspace land.</summary>
    public static string InboxOf(string id) => Path.Combine(FolderOf(id), "inbox");

    /// <summary>
    /// Copies files the owner dropped on the panel into the workspace, and returns how many landed.
    /// Copies rather than moves on purpose: a moved file is one the agent can then change or
    /// destroy, and the owner dropped it to share it, not to hand it over.
    /// </summary>
    public static int Accept(string id, IEnumerable<string> paths)
    {
        string inbox = InboxOf(id);
        if (!Directory.Exists(FolderOf(id))) return 0;
        Directory.CreateDirectory(inbox);
        int landed = 0;
        foreach (string path in paths)
        {
            try
            {
                if (Directory.Exists(path)) { landed += CopyTree(path, Path.Combine(inbox, Path.GetFileName(path))); continue; }
                if (!File.Exists(path)) continue;
                File.Copy(path, Free(inbox, Path.GetFileName(path)), overwrite: false);
                landed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return landed;
    }

    static int CopyTree(string from, string to)
    {
        int copied = 0;
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            try { File.Copy(file, Free(to, Path.GetFileName(file)), overwrite: false); copied++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (string folder in Directory.GetDirectories(from))
            copied += CopyTree(folder, Path.Combine(to, Path.GetFileName(folder)));
        return copied;
    }

    /// <summary>A name that is not taken, so a second drop of the same file does not overwrite the first.</summary>
    static string Free(string folder, string name)
    {
        string target = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), suffix = Path.GetExtension(name);
        for (int copy = 2; File.Exists(target); copy++)
            target = Path.Combine(folder, $"{stem} ({copy}){suffix}");
        return target;
    }

    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes"
    };

    static StoredWorkspace? Read(string folder)
    {
        string id = Path.GetFileName(folder);
        const string prefix = "Deskweave-";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        id = id[prefix.Length..];

        string record = Path.Combine(folder, RecordName);
        if (File.Exists(record))
        {
            try
            {
                StoredWorkspace? stored = JsonSerializer.Deserialize<StoredWorkspace>(File.ReadAllText(record));
                // An id that disagrees with the folder it was found in is the folder's, always: the
                // folder is what the desktop and the low integrity label are named after.
                if (stored is not null && !string.IsNullOrWhiteSpace(stored.Name))
                    return stored with { Id = id };
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        // Recovery: an unreadable or missing record loses the name and the dates, not the workspace.
        var recovered = new StoredWorkspace
        {
            Id = id,
            Name = id,
            Created = Directory.GetCreationTime(folder),
            LastUsed = Directory.GetLastWriteTime(folder)
        };
        try { Save(recovered); } catch (IOException) { }
        return recovered;
    }

    /// <summary>
    /// An id is also a Windows desktop name and a folder name, so it is generated safe rather than
    /// sanitised later: lowercase letters and digits, and short.
    /// </summary>
    static string FreeId(string name)
    {
        string root = new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(16).ToArray());
        if (root.Length == 0) root = "workspace";
        string id = root;
        for (int suffix = 2; Directory.Exists(FolderOf(id)); suffix++) id = root + suffix;
        return id;
    }

    sealed class TestRootScope(string root) : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (string.Equals(_testRoot, root, StringComparison.OrdinalIgnoreCase))
                _testRoot = null;
        }
    }
}
