using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deskweave.AgentWorkspaces;
using Microsoft.Win32.SafeHandles;

internal static class WorkspaceMemoryProbe
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string folder = Path.Combine(output, "workspace-memory");
        Directory.CreateDirectory(folder);
        using (var invalidJob = new SafeFileHandle(-1, ownsHandle: false))
            check(WorkspaceLimits.ProcessIds(invalidJob) is null, "A failed job query reports failure, not an empty process list");
        check(ProcessMemory.PrivateWorkingSetBytes(0) is null,
            "A failed process memory read reports failure, not zero");
        check(ProcessMemory.NativePrivateWorkingSetBytes(0) is null,
            "A failed native fallback reports failure, not zero");
        using (var limits = new WorkspaceLimits(WorkspacePower.Light))
        {
            check(limits.PrivateWorkingSetBytes([]) == 0, "A genuinely empty job measures zero bytes");
            limits.Dispose();
            check(limits.PrivateWorkingSetBytes([]) is null, "An unavailable job reports a failed sample");
        }
        using var desktop = AgentDesktop.Create("Memory-" + Guid.NewGuid().ToString("N")[..8]);
        check(desktop.PrivateWorkingSetBytes() == 0, "An empty workspace reports zero private working set without throwing");
        ulong app = Launch("app", browser: false);
        ulong? single = desktop.PrivateWorkingSetBytes();
        check(Near(single, app), $"Workspace private working set {single} matches its fixture's independent page count {app}");
        ulong browser = Launch("browser", browser: true);
        ulong? both = desktop.PrivateWorkingSetBytes();
        check(Near(both, app + browser), $"Workspace total {both} includes both application and browser jobs ({app + browser} oracle bytes)");
        using var empty = AgentDesktop.Create("EmptyMemory-" + Guid.NewGuid().ToString("N")[..8]);
        check(empty.PrivateWorkingSetBytes() == 0, "An empty workspace excludes another workspace's live processes");
        desktop.Dispose();
        check(desktop.PrivateWorkingSetBytes() is null, "A disposed workspace reports unavailable memory without throwing");

        ulong Launch(string name, bool browser)
        {
            string report = Path.Combine(folder, name + ".json");
            string arguments = "--memory-fixture \"" + report + "\"";
            int pid = browser ? desktop.LaunchBrowser(Environment.ProcessPath!, arguments)
                : desktop.Launch(Environment.ProcessPath!, arguments);
            check(pid > 0, "Memory fixture starts in the " + name + " job");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(report) && wait.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(25);
            check(File.Exists(report), "Memory fixture publishes its private resident page count");
            ulong own = JsonSerializer.Deserialize<ulong>(File.ReadAllText(report));
            check(own > 0, "Independent fixture private working set is positive");
            nint process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
            try
            {
                check(process != 0, "Memory probe opens the fixture with query-limited rights only");
                ulong? fallback = ProcessMemory.NativePrivateWorkingSetBytes(process);
                check(Near(fallback, own), $"Native fallback {fallback} matches the fixture's independent private working set {own}");
            }
            finally { if (process != 0) Native.CloseHandle(process); }
            return own;
        }
    }

    // Allow working-set trimming and JIT/JSON activity between snapshots, but not a missing job,
    // a machine-wide figure or the fixture's untouched commit.
    static bool Near(ulong? actual, ulong expected) => actual is > 0 && expected > 0
        && Math.Abs((double)actual.Value - expected) <= expected * 0.25 + 4 * 1024 * 1024;

    internal static int Fixture(string report)
    {
        byte[] resident = new byte[32 * 1024 * 1024];
        for (int i = 0; i < resident.Length; i += Environment.SystemPageSize) resident[i] = 1;
        // Commit much more than we touch, so substituting PrivateUsage fails the oracle bound.
        nint untouched = VirtualAlloc(0, 128 * 1024 * 1024, 0x3000, 4);
        if (untouched == 0) throw new InvalidOperationException("Memory fixture allocation failed");
        try
        {
            ulong own = OwnPrivateWorkingSet();
            File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(own));
            File.Move(report + ".tmp", report);
            Thread.Sleep(TimeSpan.FromMinutes(5));
            GC.KeepAlive(resident);
            return 0;
        }
        finally { VirtualFree(untouched, 0, 0x8000); }
    }

    static ulong OwnPrivateWorkingSet()
    {
        // Independent oracle: count non-shareable resident pages in this process itself. The
        // pseudo-handle needs no OpenProcess or extra rights on another process.
        for (int size = 256 * 1024; size <= 16 * 1024 * 1024; size *= 2)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryWorkingSet(-1, buffer, size))
                {
                    if (Marshal.GetLastWin32Error() == 24) continue; // ERROR_BAD_LENGTH
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                int count = checked((int)Marshal.ReadIntPtr(buffer));
                ulong pages = 0;
                for (int i = 0; i < count; i++)
                    if ((Marshal.ReadIntPtr(buffer, (i + 1) * IntPtr.Size).ToInt64() & 0x100) == 0) pages++;
                return pages * (ulong)Environment.SystemPageSize;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Fixture working set exceeded the oracle buffer bound");
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool QueryWorkingSet(nint process, nint buffer, int size);
    [DllImport("kernel32.dll")]
    static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll")]
    static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
