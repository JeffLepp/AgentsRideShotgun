using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.AgentWorkspaces;
using Coordinator = HiveMind.AgentWorkspaces.WorkspaceRenderMode.Coordinator;
using Setting = HiveMind.AgentWorkspaces.WorkspaceRenderMode.Setting;

/// <summary>Renderer transactions tested without reading or writing the owner's registry.
/// Child dispatch belongs before Program's Probe.Turn mutex:
/// if (args.Length == 3 &amp;&amp; args[0] == "--render-mode-child")
///     return RenderModeProbe.Child(args[1], args[2]);</summary>
internal static class RenderModeProbe
{
    internal static void Run(Action<bool, string> check, string output)
    {
        foreach (int? previous in new int?[] { null, 0, 1, 37, -1 })
        {
            var store = new MemoryStore { Value = Setting.Previous(previous) };
            var owner = Owner(store, 10);
            IDisposable? hold = owner.Acquire();
            check(hold is not null && store.Value.Forced, "Renderer holds software mode after journaling the prior setting");
            hold!.Dispose();
            check(store.Value == Setting.Previous(previous) && store.Journal is null,
                "Renderer restores exact DWORD or absence: " + (previous?.ToString() ?? "absent"));
        }

        var shared = new MemoryStore();
        var first = Owner(shared, 11);
        var second = Owner(shared, 12);
        var observer = Owner(shared, 13);
        IDisposable a = first.Acquire()!;
        IDisposable b = second.Acquire()!;
        observer.Recover();
        check(shared.Value.Forced, "Recovery preserves another live process's renderer hold");
        a.Dispose();
        check(shared.Value.Forced, "Releasing one process preserves another process's renderer hold");
        b.Dispose();
        check(shared.Value == Setting.Previous(0) && shared.Journal is null,
            "The final overlapping renderer release restores the original baseline");

        var crash = new MemoryStore();
        _ = Owner(crash, 14).Acquire();
        crash.Processes.Remove(14);
        Owner(crash, 15).Recover();
        check(crash.Value == Setting.Previous(0) && crash.Journal is null,
            "A dead renderer owner is recovered without retaining the forced value");
        var reused = new MemoryStore();
        _ = Owner(reused, 16, 1).Acquire();
        reused.Processes[16] = 2;
        Owner(reused, 17).Recover();
        check(reused.Value == Setting.Previous(0), "PID reuse cannot keep an abandoned renderer hold alive");

        var noJournal = new MemoryStore { FailJournal = true };
        check(Owner(noJournal, 18).Acquire() is null && noJournal.SettingWrites == 0 && noJournal.Value == Setting.Previous(0),
            "Journal write failure refuses software mode before touching the setting");
        var noSetting = new MemoryStore { FailSetting = true };
        check(Owner(noSetting, 19).Acquire() is null && noSetting.Value == Setting.Previous(0),
            "A denied setting write returns no renderer token");
        var partial = new MemoryStore { ChangeThenFailOnce = true };
        check(Owner(partial, 20).Acquire() is null && partial.Value == Setting.Previous(0) && partial.Journal is null,
            "A setting write that changes state then fails is rolled back from its durable journal");

        var deniedRestore = new MemoryStore();
        var restoreOwner = Owner(deniedRestore, 21);
        IDisposable restoring = restoreOwner.Acquire()!;
        deniedRestore.FailSetting = true;
        restoring.Dispose();
        check(deniedRestore.Value.Forced && deniedRestore.Journal is not null,
            "Failed registry restoration retains the recovery record");
        check(restoreOwner.Acquire() is null && deniedRestore.Journal is not null,
            "A new launch cannot replace the baseline while restoration is failing");
        deniedRestore.FailSetting = false;
        restoreOwner.Recover();
        check(deniedRestore.Value == Setting.Previous(0) && deniedRestore.Journal is null,
            "A released token's failed restore can retry while its process remains alive");

        var deniedDelete = new MemoryStore();
        var deleteOwner = Owner(deniedDelete, 22);
        IDisposable deleting = deleteOwner.Acquire()!;
        deniedDelete.FailDelete = true;
        deleting.Dispose();
        check(deniedDelete.Value == Setting.Previous(0) && deniedDelete.Journal is not null,
            "Failure deleting a recovery record does not lose the original value");
        deniedDelete.FailDelete = false;
        deleteOwner.Recover();
        check(deniedDelete.Journal is null && deniedDelete.Value == Setting.Previous(0),
            "Recovery completes a previously failed journal cleanup");

        foreach (string malformed in new[] { "null", "{}", "{", """{"Schema":2,"Holds":[]}""",
            """{"Schema":8,"Previous":null,"Holds":[]}""", """{"Schema":2,"Previous":null,"Holds":[{}]}""" })
        {
            var bad = new MemoryStore { Journal = malformed, Value = new(true, 1) };
            var owner = Owner(bad, 23);
            owner.Recover();
            check(owner.Acquire() is null && bad.SettingWrites == 0 && bad.Journal == malformed,
                "Malformed renderer recovery data cannot remove an owner's setting");
        }
        var unsupported = new MemoryStore { Value = new(true, null) };
        check(Owner(unsupported, 24).Acquire() is null && unsupported.SettingWrites == 0 && unsupported.Journal is null,
            "A renderer registry value with an unsupported kind is preserved");
        var edited = new MemoryStore();
        var editingOwner = Owner(edited, 25);
        IDisposable editing = editingOwner.Acquire()!;
        edited.Value = new(true, 9);
        check(Owner(edited, 26).Acquire() is null, "A launch does not overwrite an external change during a renderer hold");
        editing.Dispose();
        check(edited.Value == new Setting(true, 9) && edited.Journal is null,
            "Final release preserves an external renderer setting change");

        var rest = new MemoryStore();
        var restartOwner = Owner(rest, 27);
        IDisposable old = restartOwner.Acquire()!;
        restartOwner.Rest();
        IDisposable fresh = restartOwner.Acquire()!;
        Parallel.For(0, 8, _ => old.Dispose());
        check(rest.Value.Forced, "Old and duplicate token disposal after Rest cannot release a new hold");
        Parallel.For(0, 8, _ => fresh.Dispose());
        check(rest.Value == Setting.Previous(0) && rest.Journal is null,
            "Concurrent duplicate release restores exactly once without holder underflow");
        var busy = new MemoryStore { RefuseTurn = true };
        check(Owner(busy, 28).Acquire() is null && busy.SettingWrites == 0,
            "An unavailable global transaction lock refuses renderer changes");

        foreach (string malformed in new[] { "null", "{}", """{"Schema":1}""", """{"Schema":4,"Previous":null}""" })
        {
            var legacy = new MemoryStore { Legacy = malformed, Value = new(true, 1) };
            Owner(legacy, 29).Recover();
            check(legacy.Value.Forced && legacy.SettingWrites == 0 && legacy.Legacy == malformed,
                "Incomplete legacy recovery records never mean an absent prior value");
        }
        var legacyLive = new MemoryStore { Legacy = """{"Schema":1,"Previous":0}""", Value = new(true, 1), LegacyAlive = true };
        var migrator = Owner(legacyLive, 30);
        check(migrator.Acquire() is null && legacyLive.Value.Forced && legacyLive.Legacy is not null,
            "An older live product process prevents unsafe legacy recovery");
        legacyLive.LegacyAlive = false;
        migrator.Recover();
        check(legacyLive.Value == Setting.Previous(0) && legacyLive.Legacy is null,
            "A valid legacy record restores only after its older owner is gone");

        AcrossProcesses(check, Path.Combine(output, "renderer-fixture-" + Guid.NewGuid().ToString("N")));
    }

    static Coordinator Owner(MemoryStore store, int pid, long started = 1)
    {
        store.Processes[pid] = started;
        return new(store, pid, started);
    }

    static void AcrossProcesses(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder);
        var store = new FileStore(folder);
        store.WriteSetting(Setting.Previous(0));
        using Process first = StartChild(folder, "first");
        Process? second = null;
        Process? crash = null;
        try
        {
            check(Wait(() => File.Exists(Path.Combine(folder, "first.ready"))), "First renderer child acquired its isolated hold");
            second = StartChild(folder, "second");
            check(Wait(() => File.Exists(Path.Combine(folder, "second.ready"))), "Second renderer child acquired an overlapping hold");
            File.WriteAllText(Path.Combine(folder, "first.release"), "");
            check(first.WaitForExit(5000) && first.ExitCode == 0 && store.ReadSetting().Forced,
                "Separate processes share a baseline until the last renderer holder exits");
            File.WriteAllText(Path.Combine(folder, "second.release"), "");
            check(second.WaitForExit(5000) && second.ExitCode == 0 && store.ReadSetting() == Setting.Previous(0),
                "The last renderer child restores the baseline across processes");
            crash = StartChild(folder, "crash");
            check(Wait(() => File.Exists(Path.Combine(folder, "crash.ready"))), "Crash-recovery renderer child acquired its hold");
            crash.Kill();
            check(crash.WaitForExit(5000), "Only the renderer fixture child was stopped for crash recovery");
            using var self = Process.GetCurrentProcess();
            new Coordinator(store, self.Id, self.StartTime.ToUniversalTime().Ticks).Recover();
            check(store.ReadSetting() == Setting.Previous(0) && store.ReadJournal() is null,
                "A different process restores a crashed renderer owner's baseline");
        }
        finally
        {
            foreach (Process? child in new[] { first, second, crash })
            {
                if (child is null) continue;
                if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
            }
            second?.Dispose();
            crash?.Dispose();
        }
    }

    static Process StartChild(string folder, string name)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--render-mode-child");
        start.ArgumentList.Add(folder);
        start.ArgumentList.Add(name);
        return Process.Start(start)!;
    }

    internal static int Child(string folder, string name)
    {
        if (!Path.IsPathFullyQualified(folder) || name is not ("first" or "second" or "crash")) return 2;
        using var self = Process.GetCurrentProcess();
        var owner = new Coordinator(new FileStore(folder), self.Id, self.StartTime.ToUniversalTime().Ticks);
        using IDisposable? hold = owner.Acquire();
        if (hold is null) return 3;
        File.WriteAllText(Path.Combine(folder, name + ".ready"), "");
        return Wait(() => File.Exists(Path.Combine(folder, name + ".release")), 45000) ? 0 : 4;
    }

    static bool Wait(Func<bool> condition, int milliseconds = 15000)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    sealed class MemoryStore : WorkspaceRenderMode.IStore
    {
        readonly object _turn = new();
        internal readonly Dictionary<int, long> Processes = [];
        internal Setting Value = Setting.Previous(0);
        internal string? Journal, Legacy;
        internal bool FailJournal, FailSetting, ChangeThenFailOnce, FailDelete, RefuseTurn, LegacyAlive;
        internal int SettingWrites;
        public IDisposable? Enter()
        {
            if (RefuseTurn) return null;
            Monitor.Enter(_turn);
            return new Turn(() => Monitor.Exit(_turn));
        }
        public string? ReadJournal() => Journal;
        public void WriteJournal(string json)
        {
            if (FailJournal) throw new IOException("simulated journal write failure");
            Journal = json;
        }
        public void DeleteJournal()
        {
            if (FailDelete) throw new IOException("simulated journal delete failure");
            Journal = null;
        }
        public Setting ReadSetting() => Value;
        public void WriteSetting(Setting setting)
        {
            SettingWrites++;
            if (FailSetting) throw new UnauthorizedAccessException("simulated registry denial");
            Value = setting;
            if (ChangeThenFailOnce) { ChangeThenFailOnce = false; throw new IOException("simulated post-write failure"); }
        }
        public bool IsAlive(int pid, long started) => Processes.TryGetValue(pid, out long actual) && actual == started;
        public string? ReadLegacy() => Legacy;
        public bool LegacyOwnerMayBeAlive() => LegacyAlive;
        public void DeleteLegacy() => Legacy = null;
    }

    /// <summary>Real process and named-mutex behavior with files standing in for the registry.</summary>
    sealed class FileStore(string folder) : WorkspaceRenderMode.IStore
    {
        string Journal => Path.Combine(folder, "journal.json");
        string Value => Path.Combine(folder, "setting.json");
        public IDisposable? Enter()
        {
            var mutex = new Mutex(false, @"Local\Deskweave.RenderModeProbe." + Path.GetFileName(folder));
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new Turn(() => { mutex.ReleaseMutex(); mutex.Dispose(); });
            mutex.Dispose();
            return null;
        }
        public string? ReadJournal() => File.Exists(Journal) ? File.ReadAllText(Journal) : null;
        public void WriteJournal(string json) => Atomic(Journal, json);
        public void DeleteJournal() => File.Delete(Journal);
        public Setting ReadSetting() => JsonSerializer.Deserialize<Setting>(File.ReadAllText(Value));
        public void WriteSetting(Setting setting) => Atomic(Value, JsonSerializer.Serialize(setting));
        static void Atomic(string path, string text)
        {
            string part = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(part, text);
            File.Move(part, path, overwrite: true);
        }
        public bool IsAlive(int pid, long started)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public string? ReadLegacy() => null;
        public bool LegacyOwnerMayBeAlive() => false;
        public void DeleteLegacy() { }
    }

    sealed class Turn(Action release) : IDisposable { public void Dispose() => release(); }
}
