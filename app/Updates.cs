using Velopack;
using Velopack.Sources;

namespace Deskweave;

/// <summary>
/// Downloads new releases from GitHub in the background. Nothing restarts under a running agent:
/// a downloaded update is applied only when the owner next opens Deskweave with no copy running,
/// never on a --background start by an agent's bridge or Windows (Program.ApplyUpdateOnStart).
/// </summary>
static class Updates
{
    // GitHub redirects a renamed repository, so a rename does not strand installed copies.
    const string Feed = "https://github.com/JeffLepp/Deskweave";

    public static async void Start(Action<string, string> tell)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Feed, null, false));
            if (!manager.IsInstalled) return;   // a dev build in out/ has nothing to update
            string? told = null;
            while (true)
            {
                try
                {
                    if (await manager.CheckForUpdatesAsync() is { } update)
                    {
                        await manager.DownloadUpdatesAsync(update);
                        string version = update.TargetFullRelease.Version.ToString();
                        if (told != version)
                        {
                            told = version;
                            tell("Deskweave " + version + " is ready", "It installs when you quit and open Deskweave again.");
                        }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { App.LogFailure(error); }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { App.LogFailure(error); }
    }
}
