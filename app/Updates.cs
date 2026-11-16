using System.Diagnostics;
using System.IO;
using Deskweave.AgentWorkspaces;
using Velopack;
using Velopack.Sources;

namespace Deskweave;

/// <summary>Download normally; apply with Velopack only after an explicit request and an atomic idle gate.</summary>
static class Updates
{
    const string Feed = "https://github.com/JeffLepp/AgentsRideShotgun";
    static UpdateManager? _manager;
    static VelopackAsset? _target;
    static readonly SemaphoreSlim Downloads = new(1, 1);
    static bool _busy;
    static string? _requested;
    static string _status = "No restart requested.";
    static Action? _shutdown;
    static Action<string, string>? _tell;
    static System.Windows.Threading.DispatcherTimer? _retry;
    internal static string Status => _status;
    internal static Action<string>? ScheduleForTests;

    internal static async Task<T> StorageLocked<T>(Func<Task<T>> action)
    {
        await Downloads.WaitAsync().ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { Downloads.Release(); }
    }

    internal static void Start(Action<string, string> tell, Action shutdown)
    {
        _tell = tell;
        _shutdown = shutdown;
        WorkspaceRouter.LifecycleCommand = async (operation, source) =>
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Request(operation, source)).Task.Unwrap();
        _retry = new() { Interval = TimeSpan.FromSeconds(2) };
        _retry.Tick += (_, _) => TryRestart();
        _retry.Start();
        _ = Poll();
    }

    internal static void Stop()
    {
        _retry?.Stop();
        WorkspaceRouter.LifecycleCommand = null;
    }

    internal static async Task<string> Request(string operation, string? source = null)
    {
        if (App.HostedByHiveMind) return "This workspace runtime is bundled with HiveMind; update it through HiveMind.";
        if (operation == "status") return _status;
        if (operation is not ("restart" or "update")) return "Unknown lifecycle command.";
        if (_busy || _requested is not null || WorkspaceRuntime.Maintenance) return _status;
        _busy = true;
        try
        {
            if (operation == "update")
            {
                if (source is not null) source = LocalSource(source);
                SetStatus("Checking and downloading the requested update feed.");
                await Downloads.WaitAsync();
                try
                {
                    // A local Velopack feed is supported for reviewed/private releases; never a payload swap.
                    _manager = source is null ? new UpdateManager(new GithubSource(Feed, null, false)) : new UpdateManager(source);
                    if (!_manager.IsInstalled) return SetStatus("Update unavailable: this is a development build.");
                    _target = null;
                    if (await _manager.CheckForUpdatesAsync() is { } update)
                    {
                        await _manager.DownloadUpdatesAsync(update);
                        _target = update.TargetFullRelease;
                    }
                    // A requested local feed must never silently apply a cached package from a different feed.
                    if (_target is null && source is null) _target = _manager.UpdatePendingRestart;
                    if (_target is null) return SetStatus("ARS is up to date; no update selected from the requested feed.");
                }
                finally { Downloads.Release(); }
            }
            _requested = operation;
            TryRestart();
            return _status;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { App.LogFailure(error); return SetStatus("Update/restart failed: " + error.Message); }
        finally { _busy = false; }
    }

    internal static string DeferredReason()
    {
        if (WorkspaceRuntime.AnyRunning)
            return "Deferred: save work, close workspace apps, then stop each running workspace. ARS will retry automatically.";
        string other = OtherControllerReason();
        if (other.Length > 0) return other;
        // Legacy clients can still run from current/. Do not let the updater terminate their MCP transport.
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (Process process in Process.GetProcessesByName("ARS.WorkspaceBridge"))
        using (process)
        {
            try
            {
                if (ControllerProcess.ImagePath(process.Id) is { } path && Path.GetFullPath(path).StartsWith(current, StringComparison.OrdinalIgnoreCase))
                    return "Deferred: a legacy bridge is running from the install tree. Reconnect that MCP client using ARS --bridge-path.";
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { return "Deferred: cannot establish bridge identity safely."; }
        }
        return "";
    }

    // Singleton locks are session-local. Velopack replaces a per-user install, so a
    // controller in another Windows session must not lose its workspace apps either.
    internal static string OtherControllerReason()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (Process process in Process.GetProcessesByName("ARS"))
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.SessionId == Process.GetCurrentProcess().SessionId) continue;
                if (ControllerProcess.ImagePath(process.Id) is { } path && Path.GetFullPath(path).StartsWith(current, StringComparison.OrdinalIgnoreCase))
                    return "Deferred: another Windows session is using this ARS install. Save and stop its workspaces and controller first.";
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { return "Deferred: cannot safely establish whether another controller is using this install."; }
        }
        return "";
    }

    internal static void TryRestart()
    {
        if (_requested is null || WorkspaceRuntime.Maintenance) return;
        string reason = DeferredReason();
        if (reason.Length > 0) { SetStatus(reason); return; }
        if (!WorkspaceRuntime.BeginMaintenance()) { SetStatus("Deferred: a workspace started. Waiting for it to stop."); return; }
        try
        {
            if (ScheduleForTests is { } schedule) schedule(_requested);
            else if (_requested == "update")
                _manager!.WaitExitThenApplyUpdates(_target!, silent: true, restart: true,
                    restartArgs: [StartWithWindows.Background]);
            else ControllerLifecycle.ScheduleRestart();
            SetStatus("Accepted: ARS is restarting; existing MCP bridges reconnect on their next request.");
            _requested = null;
            // Return the command response before withdrawing the router. No new workspaces can enter.
            _ = CloseAfterReply();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WorkspaceRuntime.CancelMaintenance();
            _requested = null;
            App.LogFailure(error);
            SetStatus("Update/restart failed: " + error.Message);
        }
    }

    // Enforce the same local-feed contract for CLI and authenticated pipe clients.
    // Custom feeds are explicitly trusted developer input, not a replacement public source.
    internal static string LocalSource(string source)
    {
        if (source.Length < 3 || !char.IsAsciiLetter(source[0]) || source[1] != ':'
            || source[2] is not ('\\' or '/'))
            throw new ArgumentException("An update source must be an absolute local folder.", nameof(source));
        string folder = Path.GetFullPath(source);
        if (new DriveInfo(Path.GetPathRoot(folder)!).DriveType == DriveType.Network)
            throw new ArgumentException("An update source cannot be a mapped network drive.", nameof(source));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The local update feed folder does not exist.");
        return folder;
    }

    static async Task CloseAfterReply() { await Task.Delay(500); _shutdown?.Invoke(); }
    static string SetStatus(string status)
    {
        if (_status != status) { _status = status; _tell?.Invoke("ARS update/restart", status); }
        return status;
    }

    static async Task Poll()
    {
        while (true)
        {
            if (!_busy && _requested is null && !WorkspaceRuntime.Maintenance)
            {
                await Downloads.WaitAsync();
                try
                {
                    var manager = new UpdateManager(new GithubSource(Feed, null, false));
                    if (!manager.IsInstalled) return;
                    if (await manager.CheckForUpdatesAsync() is { } update)
                    {
                        await manager.DownloadUpdatesAsync(update);
                        if (!_busy && _requested is null && !WorkspaceRuntime.Maintenance)
                            SetStatus("ARS " + update.TargetFullRelease.Version + " is ready. Choose Install update when workspaces stop; running workspaces defer it.");
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { App.LogFailure(error); }
                finally { Downloads.Release(); }
            }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }
}
