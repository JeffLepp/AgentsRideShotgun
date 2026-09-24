using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// WPF's software renderer makes its windows capturable on an uncomposed workspace desktop.
/// ProcessRenderMode runs inside the target application; a launcher cannot set that property
/// in an arbitrary installed program. The remaining documented switch is per user. An unrelated
/// WPF program starting during this short interval can also select software rendering.
/// Coordination recovers that interval; it cannot isolate it.
/// https://learn.microsoft.com/en-us/troubleshoot/developer/dotnet/framework/general/wpf-render-thread-failures
/// </summary>
internal static class WorkspaceRenderMode
{
    internal static TimeSpan StartupBound { get; set; } = TimeSpan.FromSeconds(15);
    internal static TimeSpan DrawingGrace { get; set; } = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    static readonly Lazy<Coordinator> Shared = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return new Coordinator(new WindowsStore(), process.Id, process.StartTime.ToUniversalTime().Ticks);
    });

    internal static IDisposable? Soften()
    {
        try { return Shared.Value.Acquire(); }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    internal static void Recover()
    {
        try { Shared.Value.Recover(); }
        catch (Exception ex) when (Expected(ex)) { }
    }

    internal static void Rest()
    {
        if (Shared.IsValueCreated) Shared.Value.Rest();
    }

    internal static void ReleaseWhenStarted(IDisposable? hold, int pid, Func<int, bool> drawn)
    {
        if (hold is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                long until = Environment.TickCount64 + (long)StartupBound.TotalMilliseconds;
                while (Environment.TickCount64 < until)
                {
                    if (drawn(pid))
                    {
                        await Task.Delay(DrawingGrace).ConfigureAwait(false);
                        return;
                    }
                    if (Gone(pid)) return;
                    await Task.Delay(Poll).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Expected(ex)) { }
            finally { hold.Dispose(); }
        });
    }

    static bool Gone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return true; }
    }

    static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or SecurityException or JsonException or ArgumentException or InvalidOperationException
        or System.ComponentModel.Win32Exception;

    // An unsupported kind is distinct from absence and must never be overwritten.
    internal readonly record struct Setting(bool Exists, int? Dword)
    {
        internal bool Forced => Exists && Dword == 1;
        internal bool Supported => !Exists || Dword is not null;
        internal static Setting Previous(int? number) => new(number is not null, number);
    }

    /// <summary>Fault-test seam. Production storage never follows a probe's WorkspaceStore root.</summary>
    internal interface IStore
    {
        IDisposable? Enter();
        string? ReadJournal();
        void WriteJournal(string json);
        void DeleteJournal();
        Setting ReadSetting();
        void WriteSetting(Setting setting);
        bool IsAlive(int pid, long started);
        string? ReadLegacy();
        bool LegacyOwnerMayBeAlive();
        void DeleteLegacy();
    }

    internal sealed class Coordinator(IStore store, int pid, long started)
    {
        readonly Lock _gate = new();
        readonly string _owner = Guid.NewGuid().ToString("N");
        readonly HashSet<string> _active = [];

        internal IDisposable? Acquire()
        {
            lock (_gate)
            {
                string token = Guid.NewGuid().ToString("N");
                try
                {
                    using IDisposable? turn = store.Enter();
                    if (turn is null) return null;
                    Journal? journal = Read();
                    bool changeSetting = false;
                    if (journal is not null)
                    {
                        Prune(journal);
                        if (journal.Holds.Count == 0) { Restore(journal); journal = null; }
                    }
                    if (journal is null)
                    {
                        RecoverLegacy();
                        Setting previous = store.ReadSetting();
                        if (!previous.Supported) return null;
                        journal = new(2, previous.Dword, []);
                        changeSetting = !previous.Forced;
                    }
                    else if (!store.ReadSetting().Forced)
                    {
                        // A change made outside this coordinator belongs to its writer.
                        return null;
                    }
                    if (journal.Holds.Count >= 4096) return null;
                    _active.Add(token);
                    journal.Holds.Add(new(_owner, token, pid, started));
                    // Persist and flush the recovery record before changing the registry.
                    Save(journal);
                    if (changeSetting) store.WriteSetting(new(true, 1));
                    return new Token(this, token);
                }
                catch (Exception ex) when (Expected(ex))
                {
                    _active.Remove(token);
                    // A failed write may already have reached the OS. Roll back if possible;
                    // otherwise retain the journal for the next recovery attempt.
                    Recover();
                    return null;
                }
            }
        }

        internal void Recover()
        {
            lock (_gate)
            {
                try
                {
                    using IDisposable? turn = store.Enter();
                    if (turn is null) return;
                    Journal? journal = Read();
                    if (journal is null) { RecoverLegacy(); return; }
                    Prune(journal);
                    if (journal.Holds.Count == 0) Restore(journal);
                    else Save(journal);
                }
                catch (Exception ex) when (Expected(ex)) { }
            }
        }

        internal void Rest()
        {
            lock (_gate) { _active.Clear(); Recover(); }
        }

        void Release(string token)
        {
            lock (_gate)
            {
                // Identity, not a count: late or repeated disposal cannot release a new hold.
                if (!_active.Remove(token)) return;
                Recover();
            }
        }

        Journal? Read()
        {
            string? json = store.ReadJournal();
            if (json is null) return null;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            int? previous = Previous(root, 2);
            if (!root.TryGetProperty("Holds", out JsonElement holds) || holds.ValueKind != JsonValueKind.Array
                || holds.GetArrayLength() > 4096) throw new InvalidDataException("Invalid renderer owners.");
            var owners = new List<Hold>();
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement hold in holds.EnumerateArray())
            {
                if (!hold.TryGetProperty("Owner", out JsonElement owner) || owner.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(owner.GetString(), "N", out _)
                    || !hold.TryGetProperty("Token", out JsonElement token) || token.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(token.GetString(), "N", out _)
                    || !hold.TryGetProperty("Pid", out JsonElement process) || !process.TryGetInt32(out int id) || id <= 0
                    || !hold.TryGetProperty("Started", out JsonElement start) || !start.TryGetInt64(out long ticks) || ticks <= 0
                    || !tokens.Add(token.GetString()!))
                    throw new InvalidDataException("Invalid renderer owner.");
                owners.Add(new(owner.GetString()!, token.GetString()!, id, ticks));
            }
            return new(2, previous, owners);
        }

        static int? Previous(JsonElement root, int schema)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Schema", out JsonElement version) || !version.TryGetInt32(out int value) || value != schema
                || !root.TryGetProperty("Previous", out JsonElement previous))
                throw new InvalidDataException("Invalid renderer recovery record.");
            if (previous.ValueKind == JsonValueKind.Null) return null;
            if (previous.ValueKind == JsonValueKind.Number && previous.TryGetInt32(out int number)) return number;
            throw new InvalidDataException("Invalid previous renderer setting.");
        }

        void Prune(Journal journal) => journal.Holds.RemoveAll(hold =>
            hold.Owner == _owner ? !_active.Contains(hold.Token) : !store.IsAlive(hold.Pid, hold.Started));

        void Save(Journal journal) => store.WriteJournal(JsonSerializer.Serialize(journal));

        void Restore(Journal journal)
        {
            // Commit released owners before restoring, so failed restoration remains retryable
            // while the process is alive and its token has already been disposed.
            Save(journal);
            Setting current = store.ReadSetting();
            Setting previous = Setting.Previous(journal.Previous);
            if (current.Forced && current != previous) store.WriteSetting(previous);
            // If the owner changed the setting, preserve that value. A failed restore never gets
            // here, so its recovery record is retained.
            store.DeleteJournal();
        }

        void RecoverLegacy()
        {
            string? json = store.ReadLegacy();
            if (json is null) return;
            using JsonDocument document = JsonDocument.Parse(json);
            int? previous = Previous(document.RootElement, 1);
            // Schema 1 had no identities. Do not guess while an older app or probe may own it.
            if (store.LegacyOwnerMayBeAlive()) throw new IOException("An older renderer owner may still be running.");
            if (store.ReadSetting().Forced) store.WriteSetting(Setting.Previous(previous));
            store.DeleteLegacy();
        }

        sealed record Hold(string Owner, string Token, int Pid, long Started);
        sealed record Journal(int Schema, int? Previous, List<Hold> Holds);
        sealed class Token(Coordinator owner, string id) : IDisposable
        {
            int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release(id); }
        }
    }

    sealed class WindowsStore : IStore
    {
        const string KeyPath = @"SOFTWARE\Microsoft\Avalon.Graphics";
        const string ValueName = "DisableHWAcceleration";
        static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Deskweave.Product.ProductContext.DefaultFolderName);
        static readonly string JournalPath = Path.Combine(Root, "renderer-state.json");
        static readonly string LegacyPath = Path.Combine(Root, "agent-workspaces.access", "renderer.json");
        static readonly string MutexName = @"Global\Deskweave.WpfRenderer." + WindowsIdentity.GetCurrent().User!.Value;

        public IDisposable? Enter()
        {
            var mutex = new Mutex(false, MutexName);
            try
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) return new MutexTurn(mutex);
                mutex.Dispose();
                return null;
            }
            catch { mutex.Dispose(); throw; }
        }

        public string? ReadJournal() => ReadFile(JournalPath);
        public string? ReadLegacy() => ReadFile(LegacyPath);
        static string? ReadFile(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Renderer recovery record is too large.");
            return File.ReadAllText(path);
        }

        public void WriteJournal(string json)
        {
            Directory.CreateDirectory(Root);
            string temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, JournalPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (Expected(ex)) { }
            }
        }

        public void DeleteJournal() => File.Delete(JournalPath);
        public void DeleteLegacy() => File.Delete(LegacyPath);

        public Setting ReadSetting()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null || !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)) return new(false, null);
            return key.GetValueKind(ValueName) == RegistryValueKind.DWord && key.GetValue(ValueName) is int value
                ? new(true, value) : new(true, null);
        }

        public void WriteSetting(Setting setting)
        {
            if (setting.Exists)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                key.SetValue(ValueName, setting.Dword!.Value, RegistryValueKind.DWord);
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
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
            // Access denied does not prove a dead owner.
            catch (System.ComponentModel.Win32Exception) { return true; }
        }

        public bool LegacyOwnerMayBeAlive()
        {
            foreach (string name in new[] { "ARS", "Deskweave", "Deskweave.Probe", "Deskweave.UiProbe" })
                foreach (Process process in Process.GetProcessesByName(name))
                    using (process) if (process.Id != Environment.ProcessId) return true;
            return false;
        }

        sealed class MutexTurn(Mutex mutex) : IDisposable
        {
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
