using System.IO;
using System.Reflection;
using System.Windows;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>
/// Settings' Scratch files row measured and cleared a folder that never exists (it asked for the
/// folder of a desktop named after the bare id), so it read 0 KB and Clear did nothing. And a
/// workspace page's Delete announced the workspace gone even when Windows kept it. Both run on the
/// real code against the probe's own store; no window is shown.
/// </summary>
static class StorageChecks
{
    internal static Task Run()
    {
        StoredWorkspace scratch = WorkspaceHome.EnsureScratch();
        string folder = WorkspaceStore.FolderOf(scratch.Id);
        File.WriteAllBytes(Path.Combine(folder, "note.txt"), new byte[4096]);
        Program.Check(SettingsActions.ScratchBytes() >= 4096, "The Scratch files row measures Scratch's own folder");
        SettingsActions.ClearScratch();
        Program.Check(!File.Exists(Path.Combine(folder, "note.txt")) && File.Exists(Path.Combine(folder, "workspace.json")),
            "Clear empties Scratch's files and keeps its record");

        StoredWorkspace held = WorkspaceStore.Create("Held open");
        string file = Path.Combine(WorkspaceStore.FolderOf(held.Id), "held.txt");
        File.WriteAllText(file, "open");
        var view = new WorkspaceFullView();
        string? deleted = null;
        view.Deleted += id => deleted = id;
        WorkspaceFullView.ConfirmDeleteForTests = () => true;
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                typeof(WorkspaceFullView).GetMethod("DeleteWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [held.Id]);
            Program.Check(deleted is null && view.HeaderError.Visibility == Visibility.Visible
                && view.HeaderError.Text.StartsWith("Couldn't delete", StringComparison.Ordinal),
                "A delete Windows would not finish says so on the page instead of announcing the workspace gone");
        }
        finally
        {
            WorkspaceFullView.ConfirmDeleteForTests = null;
            WorkspaceStore.Delete(held.Id);
        }
        return Task.CompletedTask;
    }
}
