using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

internal static class StorageController
{
    internal static WorkspaceStorage? SourceForTests = null;
    internal static WorkspaceStorage Source => SourceForTests ?? WorkspaceStorage.Default;
    static DispatcherTimer? _retention;
    internal static void Start()
    {
        WorkspaceRouter.StorageCommand = Clean;
        WorkspaceRouter.StorageView = async () => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Application.Current.MainWindow is not MainWindow window) return "Hub unavailable.";
            window.OpenStorage();
            return "Storage view opened.";
        });
        _retention = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _retention.Tick += async (_, _) => await Retain();
        _retention.Start();
        _ = Retain();
    }
    internal static void Stop()
    { _retention?.Stop(); WorkspaceRouter.StorageCommand = null; WorkspaceRouter.StorageView = null; }

    internal static Task<StorageCleanup> Clean(string category, bool dryRun) => CleanSelected([category], dryRun);

    internal static Task<StorageCleanup> CleanSelected(string[] categories, bool dryRun, StorageCleanup? plan = null, IReadOnlySet<string>? workspaceIds = null) => Updates.StorageLocked(async () =>
    {
        if (Updates.OtherControllerReason().Length > 0) throw new IOException("Another Windows session is using ARS; cleanup deferred.");
        if (WorkspaceRuntime.Maintenance) throw new IOException("ARS is restarting or applying an update; cleanup deferred.");
        return await Task.Run(() => Source.CleanSelected(categories, dryRun, plan?.ReviewedFiles,
            workspaceIds: workspaceIds, reviewedFolders: plan?.WorkspaceFolders)).ConfigureAwait(false);
    });

    static async Task Retain()
    {
        try
        {
            if (!WorkspaceStorage.Default.Policy.Automatic) return;
            StorageCleanup result = await Clean("retention", false);
            File.WriteAllText(Deskweave.Product.ProductContext.Local("storage-last-retention.json"), JsonSerializer.Serialize(result, WorkspaceStorage.Json));
            // The current assembly has completed startup. A changed successful version permits
            // package pruning; downloads share the same lock and future pending packages survive.
            string marker = Deskweave.Product.ProductContext.Local("storage-successful-version.txt");
            string version = typeof(App).Assembly.GetName().Version!.ToString();
            if (File.Exists(marker) && File.ReadAllText(marker) != version) await Clean("packages", false);
            File.WriteAllText(marker, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { App.LogFailure(e); }
    }

    internal static async Task<int> Command(string? category, bool dryRun, bool view = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            using var ticket = JsonDocument.Parse(File.ReadAllText(WorkspaceAccessStore.RouterTicket));
            using var pipe = new NamedPipeClientStream(".", ticket.RootElement.GetProperty("pipe").GetString()!,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2000, timeout.Token);
            await WorkspacePipeProtocol.Write(pipe, ticket.RootElement.GetProperty("capability").GetString(), 256, timeout.Token);
            if (await WorkspacePipeProtocol.Read(pipe, 256, timeout.Token) != "workspace-pipe/1") return 1;
            await WorkspacePipeProtocol.Write(pipe, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1,
                method = view ? "deskweave/storage-view" : "deskweave/storage", @params = new { category, dryRun } }),
                WorkspacePipeProtocol.MaxRequestBytes, timeout.Token);
            string? body = await WorkspacePipeProtocol.Read(pipe, WorkspacePipeProtocol.MaxResponseBytes, timeout.Token);
            using var response = JsonDocument.Parse(body ?? throw new IOException("Controller closed before replying."));
            if (response.RootElement.TryGetProperty("error", out JsonElement error))
            { Console.Error.WriteLine(error); return 1; }
            JsonElement result = response.RootElement.GetProperty("result");
            Console.WriteLine(JsonSerializer.Serialize(result, WorkspaceStorage.Json));
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 ? 1 : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or TimeoutException or KeyNotFoundException or InvalidOperationException)
        { Console.Error.WriteLine("ARS storage command failed: " + e.Message); return 1; }
    }
}
