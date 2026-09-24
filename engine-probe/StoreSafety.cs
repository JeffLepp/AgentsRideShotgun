using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// A settings file or a workspace record that is only locked for a moment - a backup tool, an
/// antivirus scan - must not be taken for a broken one and saved over. And settings written by a
/// newer ARS are kept aside, not thrown away. Everything here runs on files in the probe's
/// own fixture folder; nothing starts a desktop or a window.
/// </summary>
internal static class StoreSafety
{
    internal static int RunStandalone(string output)
    {
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(Path.GetDirectoryName(output)!, "ss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixture);
        var claims = new List<string>();
        string? failure = null;
        try
        {
            LockedSettings(Check, fixture);
            NewerSettings(Check, fixture);
            using var store = WorkspaceStore.UseRootForTests(Path.Combine(fixture, "w"));
            LockedRecord(Check);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        string engine = typeof(WorkspaceStore).Assembly.Location;
        File.WriteAllText(Path.Combine(output, "store-safety.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), engine,
            engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine))),
            fixture, claims, failure, globalInputEventsSent = 0, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException(claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    static void LockedSettings(Action<bool, string> check, string fixture)
    {
        string file = Path.Combine(fixture, "locked", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ \"Schema\": 1, \"CornerPinned\": true, \"FirstRunDone\": true }");
        byte[] before = File.ReadAllBytes(file);
        using var settings = AppSettingsStore.UseFileForTests(file);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            check(!AppSettingsStore.Current.CornerPinned, "A settings file locked at launch reads as the defaults for now");
            bool saved = AppSettingsStore.Update(s => s with { PauseHotkey = "Ctrl+Alt+K" });
            check(!saved && AppSettingsStore.Current.PauseHotkey == "Ctrl+Alt+K",
                "A change made while the file has never been read holds for the session and is not saved");
        }
        check(File.ReadAllBytes(file).AsSpan().SequenceEqual(before),
            "The owner's settings file is untouched after a change made while it was locked");
        check(AppSettingsStore.Current is { CornerPinned: true, FirstRunDone: true },
            "Once the lock is gone the real settings are read, not the cached defaults");
        check(AppSettingsStore.Update(s => s with { Theme = ThemeChoice.Dark })
            && File.ReadAllText(file).Contains("\"CornerPinned\": true") && File.ReadAllText(file).Contains("\"Theme\": \"Dark\""),
            "After one good read a change saves on top of the owner's real settings");
    }

    static void NewerSettings(Action<bool, string> check, string fixture)
    {
        string file = Path.Combine(fixture, "newer", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string newer = "{ \"Schema\": 2, \"CornerPinned\": true, \"SomethingNew\": 7 }";
        File.WriteAllText(file, newer);
        using var settings = AppSettingsStore.UseFileForTests(file);
        check(!AppSettingsStore.Current.CornerPinned, "Settings from a newer schema are not used by this build");
        string kept = Path.Combine(Path.GetDirectoryName(file)!, "settings.newer.json");
        check(File.Exists(kept) && File.ReadAllText(kept) == newer && !File.Exists(file),
            "Settings from a newer schema are kept aside as settings.newer.json, byte for byte");
    }

    static void LockedRecord(Action<bool, string> check)
    {
        StoredWorkspace made = WorkspaceStore.Create("Locked" + Guid.NewGuid().ToString("N")[..6]);
        WorkspaceStore.Update(made.Id, w => w with { Agents = "folder:C:\\code\\shop", Mode = WorkspaceMode.Secure });
        string record = Path.Combine(WorkspaceStore.FolderOf(made.Id), "workspace.json");
        byte[] before = File.ReadAllBytes(record);
        using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            check(WorkspaceStore.Find(made.Id) is null && WorkspaceStore.All().All(w => w.Id != made.Id),
                "A workspace whose record is locked is left out of the read, not recovered");
        }
        check(File.ReadAllBytes(record).AsSpan().SequenceEqual(before),
            "A locked workspace record is not saved over with a stripped one");
        check(WorkspaceStore.Find(made.Id) is { Agents: "folder:C:\\code\\shop", Mode: WorkspaceMode.Secure } found && found.Name == made.Name,
            "Once the lock is gone the record reads back with its project, mode and name");
        File.WriteAllText(record, "{ \"Name\": ");
        check(WorkspaceStore.Find(made.Id) is { Name: var recovered } && recovered == made.Id,
            "A corrupt record is still recovered from its folder name");
        WorkspaceStore.Delete(made.Id);
    }
}
