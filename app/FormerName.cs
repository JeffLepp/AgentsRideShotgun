using System.IO;
using Deskweave.Product;

namespace Deskweave;

/// <summary>
/// ARS was called Deskweave. A PC that ran it has workspaces and settings in
/// %LOCALAPPDATA%\Deskweave and %APPDATA%\Deskweave; they move under ARS's own folders once, at
/// the first launch that holds the one-per-account lock, so nothing else is using them.
/// </summary>
static class FormerName
{
    internal const string Name = "Deskweave";

    internal static void MoveData()
    {
        Move(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Move(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        RenameWorkspaces(Path.Combine(ProductContext.LocalRoot, "agent-workspaces"));
    }

    /// <summary>Each workspace's folder is named after its desktop, which was Deskweave-{id}.</summary>
    internal static void RenameWorkspaces(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root, Name + "-*"))
            {
                string target = Path.Combine(root, "ARS-" + Path.GetFileName(folder)[(Name.Length + 1)..]);
                if (!Directory.Exists(target)) Directory.Move(folder, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"A workspace in {root} could not be renamed for ARS. "
                + "Close anything using that folder, then open ARS again.", ex);
        }
    }

    /// <summary>Moves <paramref name="parent"/>\Deskweave to <paramref name="parent"/>\ARS.</summary>
    internal static void Move(string parent)
    {
        if (string.IsNullOrEmpty(parent)) return;
        string old = Path.Combine(parent, Name), now = Path.Combine(parent, ProductContext.DefaultFolderName);
        if (!Directory.Exists(old)) return;
        try
        {
            if (!Directory.Exists(now)) { Directory.Move(old, now); return; }
            // Something already made the new folder (the renderer's state, an earlier try that
            // stopped halfway): move what it does not have yet, and never overwrite what it does.
            foreach (string entry in Directory.EnumerateFileSystemEntries(old))
            {
                string target = Path.Combine(now, Path.GetFileName(entry));
                if (Path.Exists(target)) continue;
                if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target);
            }
            if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting on an empty folder would look like every workspace was lost.
            throw new IOException($"Your workspaces and settings are still in {old} and could not be moved to {now}. "
                + "Close anything using that folder, then open ARS again.", ex);
        }
    }
}
