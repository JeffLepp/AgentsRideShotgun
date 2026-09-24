using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Deskweave.AgentWorkspaces;

/// <summary>A program request carries its own arguments, fixed when it is made: the owner is shown
/// exactly this and approving runs exactly this, so nothing can be swapped in before his click.</summary>
internal sealed record WorkspaceHandoff(string Id, string Kind, string Target, string Reason,
    string State, string Detail, DateTimeOffset Created, string? Sha256 = null, string? Arguments = null);

/// <summary>Requests are data, never permission. Only an owner UI action calls Decide.</summary>
internal sealed class WorkspaceHandoffs(string id, string workspaceFolder)
{
    const long MaxFileBytes = 25 * 1024 * 1024;
    static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".json", ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".docx", ".xlsx", ".pptx" };
    internal static bool IsDocument(string path) => Documents.Contains(Path.GetExtension(path));
    readonly Lock _gate = new();
    readonly List<WorkspaceHandoff> _requests = [];
    internal event Action? Changed;
    internal IReadOnlyList<WorkspaceHandoff> All { get { lock (_gate) return _requests.ToArray(); } }

    /// <summary>
    /// What an approved "program" or "takeover" request does, set by the workspace that owns these
    /// requests because both need its launcher: start a program on the owner's own desktop, or move
    /// one he already has open into the workspace. Null when it went, one sentence when it did not.
    /// </summary>
    internal Func<WorkspaceHandoff, string?>? Perform { get; set; }

    internal WorkspaceHandoff Request(string kind, string target, string reason, string? arguments = null)
    {
        if (target.Length is < 1 or > 2048 || target.Any(char.IsControl)
            || reason.Length is < 1 or > 500 || reason.Any(char.IsControl))
            throw new ArgumentException("Provide a target and a short, single-line reason.");
        if (string.IsNullOrWhiteSpace(arguments)) arguments = null;
        else if (kind is not ("program" or "takeover") || arguments.Length > 1024 || arguments.Any(char.IsControl))
            throw new ArgumentException("Program arguments must be one line of at most 1024 characters.");
        string? hash = null;
        if (kind == "url")
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
                throw new ArgumentException("Only HTTP or HTTPS links without embedded credentials can be requested.");
            target = url.AbsoluteUri;
        }
        else if (kind == "file")
        {
            target = CheckFile(target);
            using var file = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            hash = Convert.ToHexString(SHA256.HashData(file));
        }
        // A program the owner asked for goes to his own desktop, and a program he already has open
        // can be moved in here - both are his click, and both need the workspace's own launcher,
        // which is what Perform is. Neither is a document, so neither is checked as one.
        else if (kind is "program" or "takeover")
        {
            if (Perform is null) throw new ArgumentException("This workspace cannot start programs for the owner.");
        }
        else throw new ArgumentException("Request a program, a document file or an HTTP(S) link, not a command line.");
        WorkspaceHandoff request;
        lock (_gate)
        {
            // Kind is part of what makes a request the same request: "open Notepad on your desktop"
            // and "close your copy of Notepad and start it in here" name the same program and ask
            // opposite things, and answering one must never be taken for answering the other. The
            // arguments are too: the same program with a different command line is a different ask.
            var existing = _requests.FirstOrDefault(r =>
                r.State == "pending" && r.Kind == kind && r.Target == target && r.Sha256 == hash && r.Arguments == arguments);
            if (existing is not null) return existing;
            if (_requests.Count(r => r.State == "pending") >= 8)
                throw new InvalidOperationException("Eight desktop requests are already waiting. Wait for the owner.");
            if (_requests.Count >= 40) _requests.RemoveAll(r => r.State != "pending");
            request = new(Guid.NewGuid().ToString("N"), kind, target, reason, "pending",
                "Waiting for the owner's click in ARS. Nothing has opened on the main desktop.", DateTimeOffset.UtcNow, hash, arguments);
            _requests.Add(request);
        }
        Changed?.Invoke();
        return request;
    }

    string CheckFile(string target)
    {
        string folder = Path.GetFullPath(workspaceFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(target, folder);
        if (full.IndexOf(':', 2) >= 0 || !Documents.Contains(Path.GetExtension(full)))
            throw new ArgumentException("Choose a document or image. Scripts, shortcuts and apps cannot be handed off.");
        if (!File.Exists(full) || new FileInfo(full).Length > MaxFileBytes)
            throw new ArgumentException("The document must exist and be no larger than 25 MB.");
        return full;
    }

    /// <summary>Changed is raised after the lock is let go: its listeners reach the workspace's own
    /// lock, which an agent thread can hold while it waits for this one in Request.</summary>
    internal WorkspaceHandoff Decide(string requestId, bool approve, Action<string>? open = null)
    {
        WorkspaceHandoff decided = Settle(requestId, approve, open);
        if (decided.State != "pending") Changed?.Invoke();
        return decided;
    }

    WorkspaceHandoff Settle(string requestId, bool approve, Action<string>? open)
    {
        lock (_gate)
        {
            int index = _requests.FindIndex(r => r.Id == requestId);
            if (index < 0) throw new ArgumentException("That request no longer exists.");
            WorkspaceHandoff request = _requests[index];
            if (request.State != "pending") return request;
            if (!approve) return Replace(index, request with { State = "denied", Detail = "The owner kept this in the workspace." });
            if (DateTimeOffset.UtcNow - request.Created > TimeSpan.FromMinutes(30))
                return Replace(index, request with { State = "expired", Detail = "Request expired. Ask again with the current result." });
            string? snapshot = null;
            try
            {
                string target = request.Target;
                if (request.Kind == "file")
                {
                    string checkedFile = CheckFile(target);
                    // Copy and recheck the exact requested bytes before shell-opening. This catches
                    // edited files and retargeted links. The copy remains ordinary same-user data.
                    string exports = Path.Combine(WorkspaceAccessStore.ResultsRoot, id, request.Id);
                    Directory.CreateDirectory(exports);
                    snapshot = Path.Combine(exports, Path.GetFileName(checkedFile));
                    using (var source = File.Open(checkedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var destination = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (source.Length > MaxFileBytes) throw new IOException("The document grew beyond the size limit.");
                        source.CopyTo(destination);
                    }
                    using (var frozen = File.OpenRead(snapshot))
                        if (Convert.ToHexString(SHA256.HashData(frozen)) != request.Sha256)
                            throw new IOException("The document changed after the request. Ask for approval again.");
                    target = snapshot;
                }
                if (request.Kind is "program" or "takeover")
                {
                    if (Perform!(request) is { } refused) throw new InvalidOperationException(refused);
                    return Replace(index, request with { State = "opened", Detail = request.Kind == "program"
                        ? "The owner approved. Windows started it on their desktop."
                        : "The owner approved. His copy closed and the workspace started its own." });
                }
                (open ?? OpenOnDesktop)(target);
                return Replace(index, request with { State = "opened", Detail = snapshot is null
                    ? "The owner approved. Windows accepted the open request."
                    : "Windows accepted the open request. Approved copy: " + snapshot });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (snapshot is not null && File.Exists(snapshot)) File.Delete(snapshot);
                return Replace(index, request with { State = "failed", Detail = ex.Message });
            }
        }
    }
    WorkspaceHandoff Replace(int index, WorkspaceHandoff updated)
    {
        _requests[index] = updated;
        return updated;
    }
    internal void CancelPending()
    {
        lock (_gate)
            for (int i = 0; i < _requests.Count; i++)
                if (_requests[i].State == "pending")
                    _requests[i] = _requests[i] with { State = "cancelled", Detail = "Desktop requests or workspace access were turned off." };
        Changed?.Invoke();
    }
    static void OpenOnDesktop(string target)
    {
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
