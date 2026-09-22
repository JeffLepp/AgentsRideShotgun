using System.Runtime.InteropServices;

namespace Deskweave.AgentWorkspaces;

/// <summary>How much of the owner's machine one workspace may take. Two presets, one toggle.</summary>
public enum WorkspacePower
{
    /// <summary>
    /// Stays out of the owner's way, as a hard cap it may not exceed even on an idle machine. The
    /// opt-in, for a workspace that must be unfeelable while he works.
    /// </summary>
    Light,

    /// <summary>
    /// What a workspace runs at unless he says otherwise. Scheduled by weight rather than capped,
    /// so it uses whatever the machine is not using and yields the moment he wants it back.
    /// </summary>
    Fast
}

/// <summary>
/// A job object around one workspace: every process it starts inherits a CPU ceiling, a memory
/// ceiling and a priority class, and all of them die when the job handle closes. No elevation.
///
/// The cap is a hard cap, so the owner's machine stays responsive with three agents building at
/// once - which is the whole reason this exists.
/// </summary>
sealed class WorkspaceLimits : IDisposable
{
    readonly nint _job;
    bool _disposed;

    public WorkspacePower Power { get; private set; }

    public WorkspaceLimits(WorkspacePower power, bool restrictUserObjects = true)
    {
        _job = Native.CreateJobObjectW(0, null);
        if (_job == 0)
            throw new InvalidOperationException(
                $"Windows would not create the workspace job object (error {Marshal.GetLastWin32Error()}).");
        if (restrictUserObjects) Write(Native.JobBasicUiRestrictions, new Native.JobBasicUiRestrictionsData
        {
            // File access remains the normal user's. These restrictions apply only to USER objects:
            // These remove ordinary access to foreign USER handles, new desktops, display/system
            // settings and session exit. They are defense in depth, not a hostile same-user
            // boundary: the measured owner token can still attach its own thread to Default.
            UIRestrictionsClass = Native.JobUiLimitHandles | Native.JobUiLimitSystemParameters
                | Native.JobUiLimitDisplaySettings | Native.JobUiLimitDesktop
                | Native.JobUiLimitExitWindows
        });
        Apply(power);
    }

    /// <summary>Retunes a running workspace. The toggle takes effect without restarting anything.</summary>
    public void Apply(WorkspacePower power)
    {
        Power = power;
        ulong physical = PhysicalMemory();
        var extended = new Native.JobExtendedLimitInformationData
        {
            BasicLimitInformation = new Native.JobBasicLimitInformation
            {
                LimitFlags = Native.JobLimitPriorityClass | Native.JobLimitJobMemory
                    | Native.JobLimitKillOnJobClose,
                PriorityClass = power == WorkspacePower.Light
                    ? Native.BelowNormalPriorityClass
                    : Native.NormalPriorityClass
            },
            JobMemoryLimit = (nuint)MemoryLimit(power, physical)
        };
        Write(Native.JobExtendedLimitInformation, extended);

        if (TryWrite(Native.JobCpuRateControlInformation, CpuControl(power, Environment.ProcessorCount)))
            return;
        // A Windows that will not schedule by weight still gets a workspace, at the cap every build
        // before this one used. A slow agent beats a workspace that refuses to start.
        if (power == WorkspacePower.Fast
            && TryWrite(Native.JobCpuRateControlInformation, HardCap(Environment.ProcessorCount)))
            return;
        throw new InvalidOperationException(
            $"Windows refused the workspace CPU limit (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// How much memory one workspace may hold, in bytes.
    ///
    /// A share of physical memory rather than a fixed number of gigabytes, because this ships to
    /// strangers' PCs and 4 GB is a third of one machine and all of another. But a pure share is
    /// what breaks the small ones: a quarter of an 8 GB laptop is 2 GB for the application under
    /// test *and* the workspace's Chrome, and Chrome alone will pass that. The job limit is not a
    /// soft target - crossing it fails the allocation, so the owner sees the program he asked the
    /// agent to test die for no visible reason and blames the product.
    ///
    /// So the share has a floor, the way the processor share has one in cores: enough to hold a
    /// browser and an ordinary application at all. The ceiling keeps that floor from swallowing a
    /// machine too small to give it - on a PC that cannot spare the floor, the workspace gets what
    /// there is and the owner keeps the rest.
    /// </summary>
    internal static ulong MemoryLimit(WorkspacePower power, ulong physical)
    {
        bool light = power == WorkspacePower.Light;
        ulong share = physical / (light ? 4ul : 2ul);
        ulong floor = (light ? 3ul : 4ul) * Gigabyte;
        ulong ceiling = light ? physical / 2 : physical * 3 / 4;
        return Math.Max(Gigabyte / 2, Math.Min(Math.Max(share, floor), ceiling));
    }

    const ulong Gigabyte = 1024 * 1024 * 1024;

    /// <summary>
    /// How a workspace competes for the processor, which is not the same question at the two power
    /// settings.
    ///
    /// Light is a hard cap. It may not exceed its share even on a machine doing nothing else, and
    /// that is the point: the owner is working and must not be able to feel an agent.
    ///
    /// Fast is not a cap at all. Measured 2026-09-07 on a four-core Windows 10 machine: a large WPF app
    /// took 57.4 s to show a window under the flat quarter and 21.5 s at 80%, against seconds on the
    /// owner's own desktop, and the hard cap was the whole of that difference - the machine was
    /// idle and the workspace was forbidden from using it. Weight-based scheduling constrains a job
    /// only against other weight-based jobs, so a workspace gets everything nobody else wants and
    /// gives it straight back when someone does. That is what "he is not using the machine and
    /// wants the agent to get on with it" was always supposed to mean.
    /// </summary>
    internal static Native.JobCpuRateControlInformationData CpuControl(WorkspacePower power, int processors) =>
        power == WorkspacePower.Fast
            ? new()
            {
                ControlFlags = Native.CpuRateControlEnable | Native.CpuRateControlWeightBased,
                CpuRate = FastWeight,
            }
            : HardCap(processors);

    /// <summary>
    /// Windows takes a weight of 1 to 9 and divides the processor between weight-based jobs in
    /// proportion. Nothing else on this PC uses one, so the number only decides how two workspaces
    /// running at Fast share what is left: the middle of the range gives them equal claims.
    /// </summary>
    const uint FastWeight = 5;

    static Native.JobCpuRateControlInformationData HardCap(int processors) => new()
    {
        ControlFlags = Native.CpuRateControlEnable | Native.CpuRateControlHardCap,
        // Percent of total machine CPU, in hundredths of a percent.
        CpuRate = CpuPercent(processors) * 100,
    };

    /// <summary>
    /// Light's share of the machine, as a percentage, but never less than a workable number of
    /// cores. A flat 25% is a quarter of the machine on paper and one core on a four-core PC, and
    /// that one core is the whole workspace: the application under test, its browser, and every
    /// command the agent runs.
    ///
    /// So the floor is expressed in cores rather than percent. A quarter of the machine or two
    /// cores' worth, whichever is more - unchanged at eight cores and above, where a quarter is
    /// already two cores, and only more generous on the small machines where it has to be.
    /// </summary>
    internal static uint CpuPercent(int processors) =>
        Math.Clamp(Math.Max(25u, (uint)Math.Ceiling(200d / Math.Max(1, processors))), 5u, 50u);

    /// <summary>
    /// Puts a newly created, still-suspended process under this workspace's ceiling. The caller
    /// must refuse to resume it when this returns false; allowing even one instruction first lets
    /// a fast launcher create a descendant outside the job.
    /// </summary>
    public bool TryTake(nint process) => !_disposed && Native.AssignProcessToJobObject(_job, process);

    /// <summary>Whether a process belongs to this workspace, including ones an agent started.</summary>
    public bool Owns(int processId)
    {
        nint process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return false;
        bool inJob = Native.IsProcessInJob(process, _job, out bool result) && result;
        Native.CloseHandle(process);
        return inJob;
    }

    void Write<T>(int infoClass, T value) where T : struct
    {
        if (!TryWrite(infoClass, value))
            throw new InvalidOperationException(
                $"Windows refused workspace limit {infoClass} (error {Marshal.GetLastWin32Error()}).");
    }

    bool TryWrite<T>(int infoClass, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            return Native.SetInformationJobObject(_job, infoClass, buffer, size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Percent of physical memory in use right now, 0-100; 0 when Windows won't say.</summary>
    internal static uint MemoryLoad()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    internal static ulong PhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 8UL * 1024 * 1024 * 1024;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_job == 0) return;

        // KILL_ON_JOB_CLOSE remains the final backstop, but an explicit termination lets teardown
        // observe when every inherited child is actually gone. CloseHandle alone queues the kills
        // and can return while a descendant still owns the workspace as its current directory.
        Native.TerminateJobObject(_job, 0);
        long waitUntil = Environment.TickCount64 + 2000;
        int size = Marshal.SizeOf<Native.JobBasicAccountingInformationData>();
        while (Environment.TickCount64 < waitUntil
               && Native.QueryInformationJobObject(_job, Native.JobBasicAccountingInformation,
                   out Native.JobBasicAccountingInformationData accounting, size, out _)
               && accounting.ActiveProcesses != 0)
            Thread.Sleep(10);

        Native.CloseHandle(_job);
    }
}

/// <summary>
/// A nested job for one tracked command. The workspace job remains the resource boundary; this
/// child job supplies an independently cancellable process tree without giving a command a route
/// around the workspace's CPU, memory, desktop, or shutdown limits.
/// </summary>
sealed class WorkspaceProcessGroup : IDisposable
{
    readonly nint _job;
    bool _disposed;

    public WorkspaceProcessGroup()
    {
        _job = Native.CreateJobObjectW(0, null);
        if (_job == 0)
            throw new InvalidOperationException(
                $"Windows would not create the command job object (error {Marshal.GetLastWin32Error()}).");

        var limits = new Native.JobExtendedLimitInformationData
        {
            BasicLimitInformation = new Native.JobBasicLimitInformation
            {
                LimitFlags = Native.JobLimitKillOnJobClose
            }
        };
        int size = Marshal.SizeOf<Native.JobExtendedLimitInformationData>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!Native.SetInformationJobObject(_job, Native.JobExtendedLimitInformation, buffer, size))
                throw new InvalidOperationException(
                    $"Windows refused the command job limit (error {Marshal.GetLastWin32Error()}).");
        }
        catch
        {
            Native.CloseHandle(_job);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Called while the root process is suspended, after the workspace job assignment.</summary>
    public bool TryTake(nint process) => !_disposed && Native.AssignProcessToJobObject(_job, process);

    /// <summary>Kills this command and every descendant it placed in the nested job.</summary>
    public bool Terminate(uint exitCode) => !_disposed && Native.TerminateJobObject(_job, exitCode);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_job != 0) Native.CloseHandle(_job); // KILL_ON_JOB_CLOSE is the final backstop.
    }
}
