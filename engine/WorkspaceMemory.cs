using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deskweave.AgentWorkspaces;

sealed partial class WorkspaceLimits
{
    readonly Lock _memoryGate = new();

    // Duplicate only while the original is known to be live. Sampling must neither race a reused
    // job handle nor hold up UI-thread teardown for the duration of a process walk.
    internal ulong? PrivateWorkingSetBytes(HashSet<int> seen)
    {
        SafeFileHandle job;
        lock (_memoryGate)
        {
            if (_disposed || !DuplicateHandle(-1, _job, -1, out job, 0, false, 2)) return null;
        }
        using (job)
        {
            ulong total = 0;
            int[]? ids = ProcessIds(job);
            if (ids is null) return null;
            foreach (int pid in ids)
            {
                nint process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
                if (process == 0)
                {
                    // A vanished PID is safe to skip; access denied is an incomplete sample.
                    if (Marshal.GetLastWin32Error() == 87 /* ERROR_INVALID_PARAMETER */) continue;
                    return null;
                }
                try
                {
                    // Recheck the opened handle: a PID can have been recycled outside our job.
                    if (!IsProcessInJob(process, job, out bool belongs))
                    {
                        if (HasExited(process)) continue;
                        return null;
                    }
                    if (!belongs || !seen.Add(pid)) continue;
                    ulong? bytes = ProcessMemory.PrivateWorkingSetBytes(process);
                    if (bytes is null)
                    {
                        if (HasExited(process)) continue;
                        return null;
                    }
                    total += bytes.Value;
                }
                finally { Native.CloseHandle(process); }
            }
            return total;
        }
    }

    static bool HasExited(nint process) => Native.GetExitCodeProcess(process, out uint code)
        && code != 259 /* STILL_ACTIVE */;

    internal static int[]? ProcessIds(SafeFileHandle job)
    {
        int capacity = 64;
        // A rapidly spawning job can outgrow each snapshot. Bound retries and memory, then try
        // again on the next UI cadence rather than leaving an unbounded worker behind.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int size = 8 + capacity * IntPtr.Size;
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buffer, 0, 0);
                Marshal.WriteInt32(buffer, 4, 0);
                bool ok = QueryInformationJobObject(job, 3 /* JobObjectBasicProcessIdList */, buffer, size, out _);
                if (!ok && Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */) return null;
                int assigned = Marshal.ReadInt32(buffer);
                int count = Marshal.ReadInt32(buffer, 4);
                if (ok && count >= 0 && count <= capacity && count >= assigned)
                {
                    var ids = new int[count];
                    for (int i = 0; i < count; i++)
                        ids[i] = unchecked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
                    return ids;
                }
                if (assigned > 1_048_576) return null;
                capacity = Math.Max(capacity * 2, assigned);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, nint buffer, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(nint process, SafeFileHandle job, out bool belongs);
}

internal static class ProcessMemory
{
    /// <summary>Resident private bytes, not private commit or the shared-inclusive working set.</summary>
    internal static ulong? PrivateWorkingSetBytes(nint process)
    {
        uint size = (uint)Marshal.SizeOf<MemoryCountersEx2>();
        var counters = new MemoryCountersEx2 { Size = size, PrivateWorkingSetSize = nuint.MaxValue };
        // EX2 is documented for Win10/11 22H2 + September 2023 updates. Older implementations
        // may accept a larger buffer but fill only EX: check both cb and the unwritten sentinel.
        if (GetProcessMemoryInfo(process, ref counters, size) && counters.Size == size
            && counters.PrivateWorkingSetSize != nuint.MaxValue)
            return counters.PrivateWorkingSetSize;

        // ProcessVmCounters / VM_COUNTERS_EX2 supplies the same resident-private metric on
        // Windows 10 1809+. Its native layout is recorded in System Informer's phnt/ntpsapi.h.
        // No PROCESS_VM_READ, working-set page walk, or PrivateUsage (commit) substitution.
        return NativePrivateWorkingSetBytes(process);
    }

    // Also exercised directly by engine-probe so a modern machine tests the older-Windows route.
    internal static ulong? NativePrivateWorkingSetBytes(nint process) =>
        NtQueryInformationProcess(process, 3, out VmCountersEx2 vm,
            Marshal.SizeOf<VmCountersEx2>(), out int returned) >= 0
            && returned == Marshal.SizeOf<VmCountersEx2>() ? vm.PrivateWorkingSetSize : null;

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCountersEx2
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VmCountersEx2
    {
        public nuint PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }

    [DllImport("psapi.dll")]
    static extern bool GetProcessMemoryInfo(nint process, ref MemoryCountersEx2 counters, uint size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(nint process, int kind, out VmCountersEx2 counters,
        int size, out int returned);
}
