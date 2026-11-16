using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

internal static class OwnerIntegrationChecks
{
    internal static async Task Run()
    {
        Program.Check(Deskweave.Program.IsHiveMindHost(["--hivemind"])
            && !Deskweave.Program.IsHiveMindHost([])
            && !Deskweave.Program.IsHiveMindHost(["--hivemind", "--veloapp-uninstall"])
            && !Deskweave.Program.ApplyUpdateOnStart(["--hivemind"], "Local\\ARS.Probe.NotRunning." + Guid.NewGuid()),
            "A HiveMind runtime has an explicit standalone-exclusive launch mode and never auto-applies ARS updates");
        bool originalHosted = App.HostedByHiveMind;
        try
        {
            App.HostedByHiveMind = true;
            Program.Check((await Updates.Request("update")).Contains("through HiveMind", StringComparison.Ordinal),
                "A hosted runtime refuses standalone update requests before contacting an update feed");
            await HostedStartupSettings();
        }
        finally { App.HostedByHiveMind = originalHosted; }

        StoredWorkspace record = WorkspaceStore.Create("Owner attachment fixture");
        WorkspaceAccessStore.Write(record.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
        string profile = Path.Combine(WorkspaceStore.FolderOf(record.Id), "chrome-profile");
        Directory.CreateDirectory(profile);
        string marker = Path.Combine(profile, "fixture-session-marker.txt");
        File.WriteAllText(marker, "same-session");
        string settingsPath = AppSettingsStore.File;
        byte[]? settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        string? shown = null;
        WorkspaceRouter.OwnerShow = workspace => { shown = workspace; return Task.CompletedTask; };
        WorkspaceRouter.Start();
        try
        {
            string ticket = WorkspaceAccessStore.RouterTicket;
            async Task<JsonElement> Ask(string operation, string? workspace = null) =>
                await WorkspaceOwnerClient.RequestAsync(operation, workspace, ticketPath: ticket);
            JsonElement before = await Ask("snapshot");
            Program.Check(before.GetProperty("protocol").GetInt32() == 1
                && before.GetProperty("processId").GetInt32() == Environment.ProcessId
                && before.GetProperty("root").GetString() == WorkspaceStore.Root
                && before.GetProperty("controllerPath").GetString() == Environment.ProcessPath
                && !before.GetRawText().Contains("capability", StringComparison.OrdinalIgnoreCase)
                && WorkspaceRuntime.Of(record.Id) is null,
                "An authenticated owner snapshot identifies the existing controller without starting a workspace or disclosing its capability");
            JsonElement asleep = await Ask("frame", record.Id);
            Program.Check(!asleep.GetProperty("working").GetBoolean()
                && asleep.GetProperty("width").GetInt32() == 0 && asleep.GetProperty("pngBase64").GetString() == ""
                && WorkspaceRuntime.Of(record.Id) is null,
                "Previewing an asleep workspace does not start a replacement session");
            using (WorkspaceRuntime runtime = WorkspaceRuntime.Start(record))
            {
                JsonElement accepted = await WorkspaceOwnerClient.RequestAsync("admit", ticketPath: ticket, clientPid: Environment.ProcessId);
                JsonElement missingPid = await WorkspaceOwnerClient.RequestAsync("admit", ticketPath: ticket);
                int child = runtime.Computer!.Launch(Environment.ProcessPath!, "--owner-admission-child");
                JsonElement denied = await WorkspaceOwnerClient.RequestAsync("admit", ticketPath: ticket, clientPid: child);
                Program.Check(child > 0 && runtime.Computer.OwnsProcess(child)
                    && accepted.GetProperty("allowed").GetBoolean()
                    && !missingPid.GetProperty("allowed").GetBoolean() && !denied.GetProperty("allowed").GetBoolean(),
                    "Canonical admission accepts the owner client, rejects missing identity and refuses an actual workspace-owned child before relay access");
                JsonElement[] observations = await Task.WhenAll(Ask("snapshot"), Ask("show", record.Id));
                JsonElement row = observations[1].GetProperty("workspaces").EnumerateArray().Single(w => w.GetProperty("id").GetString() == record.Id);
                Program.Check(observations.All(o => o.GetProperty("processId").GetInt32() == Environment.ProcessId)
                    && shown == record.Id && row.GetProperty("working").GetBoolean()
                    && row.GetProperty("profilePath").GetString() == profile
                    && ReferenceEquals(WorkspaceRuntime.Of(record.Id), runtime)
                    && File.ReadAllText(marker) == "same-session",
                    "Two independent owner connections observe and show the same runtime and exact existing browser-profile path");
                JsonElement frame = await Ask("frame", record.Id);
                byte[] png = Convert.FromBase64String(frame.GetProperty("pngBase64").GetString()!);
                Program.Check(frame.GetProperty("working").GetBoolean()
                    && frame.GetProperty("width").GetInt32() is > 0 and <= 1280
                    && frame.GetProperty("height").GetInt32() is > 0 and <= 720
                    && png.Length <= 6 * 1024 * 1024 && png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                    && ReferenceEquals(WorkspaceRuntime.Of(record.Id), runtime),
                    "The owner preview returns a bounded PNG of the private desktop without replacing its runtime");
                bool invalidWorkspace = false;
                try { await Ask("show", "missing-owner-fixture"); }
                catch (InvalidOperationException) { invalidWorkspace = true; }
                Program.Check(invalidWorkspace && ReferenceEquals(WorkspaceRuntime.Of(record.Id), runtime),
                    "An invalid owner destination fails without creating a workspace or closing existing work");
                using var sourceTicket = JsonDocument.Parse(File.ReadAllText(ticket));
                string wrongTicket = Path.Combine(Program.Output, "owner-rejected-ticket.json");
                File.WriteAllText(wrongTicket, JsonSerializer.Serialize(new
                {
                    schema = 1, pipe = sourceTicket.RootElement.GetProperty("pipe").GetString(),
                    capability = Convert.ToBase64String(new byte[32]),
                }));
                bool rejected = false;
                try { await WorkspaceOwnerClient.RequestAsync("snapshot", ticketPath: wrongTicket); }
                catch (IOException) { rejected = true; }
                Program.Check(rejected && ReferenceEquals(WorkspaceRuntime.Of(record.Id), runtime),
                    "The owner interface rejects an invalid capability and leaves the shared runtime running");
            }
            Program.Check(settingsBefore is null ? !File.Exists(settingsPath) : File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore),
                "Owner attachment and private previews leave the existing settings file unchanged");

            // This gate runs on a private ARS desktop. Exercise real first-show Loaded ordering
            // with a conflicting saved destination, then reopen the same window/runtime.
            ShellPreferences saved = ShellPreferences.Read();
            StoredWorkspace previous = WorkspaceStore.Create("Previous owner destination");
            var window = new MainWindow { ShowActivated = false };
            try
            {
                new ShellPreferences { Mode = "wide", SelectedWorkspace = previous.Id }.Save();
                await App.ShowOwnerWindow(window, record.Id);
                Program.Check(window.IsLoaded && window.IsVisible && window.SelectedWorkspaceId == record.Id,
                    "The first hosted owner-window display applies the requested workspace after saved preferences load");
                window.Hide();
                await App.ShowOwnerWindow(window, previous.Id);
                Program.Check(window.IsVisible && window.SelectedWorkspaceId == previous.Id,
                    "Reopening the same owner window selects the requested existing workspace");
            }
            finally
            {
                window.Dispose();
                window.Close();
                saved.Save();
                WorkspaceStore.Delete(previous.Id);
            }
        }
        finally
        {
            WorkspaceRouter.OwnerShow = null;
            WorkspaceRouter.Stop();
            WorkspaceStore.Delete(record.Id);
        }
    }

    static async Task HostedStartupSettings()
    {
        var originalSync = SettingsActions.SyncStartup;
        var originalOpen = StartWithWindows.OpenRunKeyForTests;
        int syncCalls = 0, registryOpens = 0;
        SettingsActions.SyncStartup = _ => { syncCalls++; return true; };
        StartWithWindows.OpenRunKeyForTests = () => { registryOpens++; return null; };
        var view = new SettingsView(card: true);
        var window = new Window { Content = view, Width = 340, Height = 650, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Program.Check(Text(view).Any(t => t.StartsWith("Starts with HiveMind", StringComparison.Ordinal)),
                "Hosted card settings identify HiveMind startup instead of promising Windows startup");
            view.Show("general");
            Bound startup = view.BoundControls.Single(bound => bound.Label == "Start with Windows");
            bool before = AppSettingsStore.Current.StartWithWindows;
            startup.Choose(!before);
            startup.Choose(before);
            Program.Check(!startup.Control.IsEnabled && Text(view).Contains("Managed by HiveMind")
                && AppSettingsStore.Current.StartWithWindows == before && syncCalls == 0,
                "Hosted startup settings are disabled, explain their owner and cannot alter the stored choice or invoke startup synchronization");
            Program.Check(!StartWithWindows.Sync(AppSettingsStore.Current with { FirstRunDone = true, StartWithWindows = true })
                && registryOpens == 0,
                "The hosted startup registration guard returns before opening the Windows Run key");
            Program.Check(QuitQuestion.Body.Contains("through HiveMind or ARS", StringComparison.Ordinal)
                && !QuitQuestion.Body.Contains("sign in again", StringComparison.Ordinal),
                "Hosted quit wording does not promise a Windows sign-in restart from a managed setting");
        }
        finally
        {
            window.Close();
            SettingsActions.SyncStartup = originalSync;
            StartWithWindows.OpenRunKeyForTests = originalOpen;
        }
    }

    static IEnumerable<string> Text(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element)
                foreach (string value in Text(element)) yield return value;
    }
}
