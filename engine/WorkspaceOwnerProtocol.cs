using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Deskweave.AgentWorkspaces;

/// <summary>Read-only owner observation plus explicit opening of the existing controller UI.</summary>
internal static class WorkspaceOwnerProtocol
{
    internal static async Task<object> Handle(string operation, string? workspace, CancellationToken cancel, int? clientPid = null)
    {
        if (operation is not ("snapshot" or "show" or "frame" or "admit")) throw new ArgumentException("Unsupported owner operation.");
        if (operation == "admit")
            return new
            {
                protocol = 1, processId = Environment.ProcessId,
                // A relay must obtain this from GetNamedPipeClientProcessId, never an MCP
                // message. The canonical controller alone owns the complete workspace jobs.
                allowed = clientPid is > 0 && !WorkspaceRouter.InsideAWorkspace(clientPid.Value),
            };
        if (!string.IsNullOrEmpty(workspace) && !WorkspaceStore.All().Any(w => w.Id == workspace))
            throw new ArgumentException("That workspace no longer exists.");
        if (operation == "frame")
        {
            if (string.IsNullOrEmpty(workspace)) throw new ArgumentException("Select a workspace to preview.");
            return await Task.Run(() => Frame(workspace), cancel).ConfigureAwait(false);
        }
        if (operation == "show")
        {
            if (WorkspaceRouter.OwnerShow is not { } show) throw new InvalidOperationException("The controller's owner interface is unavailable.");
            await show(string.IsNullOrEmpty(workspace) ? null : workspace).ConfigureAwait(false);
        }
        return new
        {
            protocol = 1,
            processId = Environment.ProcessId,
            version = typeof(WorkspaceRuntime).Assembly.GetName().Version?.ToString() ?? "unknown",
            controllerPath = Environment.ProcessPath,
            root = WorkspaceStore.Root,
            workspaces = WorkspaceStore.All().Select(w => new
            {
                id = w.Id, name = w.Name, agents = w.Agents,
                working = WorkspaceRuntime.Of(w.Id) is not null,
                profilePath = Path.Combine(WorkspaceStore.FolderOf(w.Id), "chrome-profile"),
            }).ToArray(),
        };
    }

    static object Frame(string workspace)
    {
        WorkspaceRuntime? runtime = WorkspaceRuntime.Of(workspace);
        BitmapSource? image = runtime?.Computer?.Capture(1280, 720).Image;
        if (runtime is not null && image is null) throw new InvalidOperationException("The workspace preview is unavailable.");
        string encoded = "";
        if (image is not null)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            png.Save(stream);
            if (stream.Length > 6 * 1024 * 1024) throw new InvalidOperationException("Workspace preview exceeds its size limit.");
            encoded = Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        return new
        {
            protocol = 1, processId = Environment.ProcessId, workspace,
            working = runtime is not null, width = image?.PixelWidth ?? 0, height = image?.PixelHeight ?? 0,
            pngBase64 = encoded,
        };
    }
}
