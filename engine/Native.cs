using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The Win32 surface behind <see cref="AgentDesktop"/>. Every entry is a plain user32, gdi32 or
/// kernel32 export that ships with Windows - no package, no service, no elevation, no feature.
/// </summary>
static partial class Native
{
    /// <summary>
    /// True only when Windows trusts the file's Authenticode signature and its signing
    /// certificate has the exact expected display name. Trust and publisher identity are separate
    /// gates: a different, validly signed executable must not become something HiveMind executes.
    /// </summary>
    public static bool VerifyAuthenticode(string path, string expectedPublisher)
    {
        if (!File.Exists(path) || !IsAuthenticodeTrusted(path)) return false;
        try
        {
            // SYSLIB0057 recommends X509CertificateLoader for certificate files. It cannot extract
            // the signer embedded in a PE; WinVerifyTrust above has already validated that signer.
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            using var signer = new X509Certificate2(certificate);
#pragma warning restore SYSLIB0057
            return signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
                .Equals(expectedPublisher, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsAuthenticodeTrusted(string path)
    {
        nint filePath = 0;
        nint fileInfo = 0;
        Guid action = GenericVerifyV2;
        var trust = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UIChoice = WtdUiNone,
            // Revocation lists require another network dependency. This gate asks whether Windows
            // trusts the embedded signature on the exact manifest-hashed file.
            RevocationChecks = WtdRevokeNone,
            UnionChoice = WtdChoiceFile,
            StateAction = WtdStateActionVerify,
            ProvFlags = WtdSaferFlag,
            UIContext = WtdUiContextExecute,
        };
        try
        {
            filePath = Marshal.StringToHGlobalUni(path);
            var info = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, fileInfo, fDeleteOld: false);
            trust.FileInfo = fileInfo;
            return WinVerifyTrust(new(-1), ref action, ref trust) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException
            or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (fileInfo != 0)
            {
                trust.StateAction = WtdStateActionClose;
                try { WinVerifyTrust(new(-1), ref action, ref trust); } catch { }
                Marshal.FreeHGlobal(fileInfo);
            }
            if (filePath != 0) Marshal.FreeHGlobal(filePath);
        }
    }

    static Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1,
        WtdStateActionVerify = 1, WtdStateActionClose = 2, WtdSaferFlag = 0x100,
        WtdUiContextExecute = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustFileInfo
    {
        public uint StructSize;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UIContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    static extern int WinVerifyTrust(nint window, ref Guid actionId, ref WinTrustData data);

    public const uint GenericAll = 0x10000000;
    public const int PwRenderFullContent = 0x2;
    public const int SrcCopy = 0x00CC0020;
    public const uint EtoOpaque = 0x0002;
    public const uint WmChar = 0x0102;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmMouseWheel = 0x020A;
    // A title bar, a close button and a scrollbar are non-client area. They ignore WM_LBUTTONDOWN
    // entirely, so a click there has to be hit-tested first and sent as its NC twin.
    public const uint WmNcHitTest = 0x0084;
    public const uint WmNcMouseMove = 0x00A0;
    public const uint WmNcLButtonDown = 0x00A1;
    public const uint WmNcLButtonUp = 0x00A2;
    public const uint WmNcRButtonDown = 0x00A4;
    public const uint WmNcRButtonUp = 0x00A5;
    public const int HtClient = 1;
    // Posting the NC pair is not enough for the caption buttons: DefWindowProc answers them with a
    // modal loop that reads the real cursor, which is on the owner's desktop and nowhere near them.
    // WM_SYSCOMMAND is the same command without the loop. Measured 2026-08-23.
    public const int HtMinButton = 8, HtMaxButton = 9, HtClose = 20;
    public const uint WmSysCommand = 0x0112;
    public const nint ScMinimize = 0xF020, ScMaximize = 0xF030, ScClose = 0xF060, ScRestore = 0xF120;

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(nint window);

    // --- arranging windows: the workspace screen is one monitor and every window stays on it ---

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint window);

    /// <summary>
    /// A window whose thread has stopped pumping. Moving one synchronously would park the desktop
    /// pump behind it, so it is left where it is and the caller is told so.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint window, int command);

    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
        SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;
    public const nint HwndTopmost = -1, HwndNoTopmost = -2;
    public const int SwMaximize = 3, SwMinimize = 6, SwRestore = 9;
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNewConsole = 0x00000010;
    public const uint DeleteAccess = 0x00010000;
    public const uint FileShareRead = 0x00000001, FileShareWrite = 0x00000002,
        FileShareDelete = 0x00000004;
    public const uint OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;
    public const int ErrorSharingViolation = 32, ErrorLockViolation = 33;
    public const int StartfUseStdHandles = 0x00000100;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint MaximumAllowed = 0x02000000;
    public const uint WriteDac = 0x00040000;
    public const uint TokenDuplicate = 0x0002, TokenQuery = 0x0008, TokenAssignPrimary = 0x0001,
        TokenAdjustDefault = 0x0080, TokenAdjustSessionId = 0x0100;
    public const int TokenIntegrityLevel = 25;
    public const uint SeGroupIntegrity = 0x00000020;
    public const int SecurityImpersonation = 2, TokenPrimary = 1;
    public const int SeWindowObject = 6;
    public const int DaclSecurityInformation = 0x00000004, LabelSecurityInformation = 0x00000010;
    public const string LowIntegritySid = "S-1-16-4096";
    public const int SmCxScreen = 0;
    public const int UoiName = 2;
    public const int SmCyScreen = 1;

    // --- the scheduler: a workspace waits on a Windows event, not on a loop (Milestone 3) --------

    public const uint EventObjectShow = 0x8002;
    public const uint EventObjectCreate = 0x8000;
    public const uint WineventOutOfContext = 0x0000;
    public const uint WmQuit = 0x0012;
    public const int ObjIdWindow = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WinEventProc(nint hook, uint eventId, nint window, int objectId,
        int childId, int thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWinEventHook(uint from, uint to, nint module, WinEventProc callback,
        int process, int thread, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out Msg message, nint window, uint first, uint last);

    /// <summary>
    /// The virtual key that produces a character on the current layout, low byte, or -1 for one no
    /// key produces. Needed because a WM_CHAR on its own is not a keystroke: a real key press is a
    /// WM_KEYDOWN, then the character, then a WM_KEYUP, and a page reading keydown sees nothing at
    /// all without the first of those.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanW(char letter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessageW(int thread, uint message, nint wparam, nint lparam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public Point pt;
    }

    public delegate bool EnumDesktopProc(nint window, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateDesktopW(string desktop, string? device, nint devmode, uint flags, uint access, nint attributes);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint OpenDesktopW(string desktop, uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetThreadDesktop(int thread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder info,
        int length, out int needed);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDesktopWindows(nint desktop, EnumDesktopProc callback, nint parameter);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(nint window, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(nint window, nint deviceContext, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(nint window, ref Point point);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(nint window, uint message, nint wparam, nint lparam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint deviceContext, nint gdiObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    public static extern int SetBkColor(nint deviceContext, int color);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ExtTextOutW(nint deviceContext, int x, int y, uint options,
        ref Rect rect, string? text, uint count, nint spacing);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    public const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? application, StringBuilder command,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags,
        nint environment, string? directory, ref StartupInfo startup, out ProcessInfo created);

    // --- clipboard broker (Milestone 1C) -------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    public static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeoutW(nint window, uint message, nint wparam, nint lparam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormatW(string name);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint format, nint handle);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    public static extern nuint GlobalSize(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint Size;
        public uint TickOfLastInput;
    }

    public const uint GlobalMoveable = 0x0042;
    public const uint CfUnicodeText = 13;
    public const uint WmClipboardUpdate = 0x031D;
    public const uint WmCopy = 0x0301, WmPaste = 0x0302;
    public const uint SmtoAbortIfHung = 0x0002;
    public const uint SmtoBlock = 0x0001;
    public const uint WmNull = 0x0000;

    /// <summary>
    /// The window text Windows already holds, with no message to the owning thread. The documented
    /// GetWindowText sends WM_GETTEXT across processes and waits, which is exactly what a workspace
    /// holding an application that has stopped pumping must never do.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int InternalGetWindowText(nint window, StringBuilder text, int max);
    public const int HwndMessage = -3;

    // --- the corner view's global hotkey ------------------------------------------------------
    //
    // One key combination, owned by HiveMind for as long as the module is loaded. NOREPEAT so
    // holding it down is one press, which is what a toggle wants.

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(nint window, int id);

    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008,
        ModNoRepeat = 0x4000;
    public const int WmHotKey = 0x0312;

    // --- low integrity launch (Milestone 1C) -------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes,
        int impersonationLevel, int tokenType, out nint duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(nint token, int tokenClass,
        ref TokenMandatoryLabel info, int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ConvertStringSidToSidW(string sid, out nint result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUserW(nint token, string? application,
        StringBuilder command, nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint flags, nint environment, string? directory, ref StartupInfo startup,
        out ProcessInfo created);

    // SetUserObjectSecurity accepts a label-only descriptor, returns true, and stores nothing.
    // SetSecurityInfo is the call that actually writes a mandatory label on a window object.
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int SetSecurityInfo(nint handle, int objectType, int information,
        nint owner, nint group, nint dacl, byte[]? sacl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetUserObjectSecurity(nint handle, ref int information,
        byte[]? descriptor, int length, out int needed);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetUserObjectSecurity(nint handle, ref int information, byte[] descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint LocalFree(nint memory);

    // --- job object limits (Milestone 1C) -----------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(nint job, int infoClass, nint info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(
        nint job,
        int infoClass,
        out JobBasicAccountingInformationData information,
        int length,
        out int returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(nint process, nint job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inherit, int processId);

    public const int JobBasicAccountingInformation = 1, JobBasicUiRestrictions = 4,
        JobExtendedLimitInformation = 9,
        JobCpuRateControlInformation = 15;
    public const uint JobLimitPriorityClass = 0x00000020, JobLimitJobMemory = 0x00000200,
        JobLimitKillOnJobClose = 0x00002000;
    public const uint JobUiLimitHandles = 0x00000001, JobUiLimitSystemParameters = 0x00000008,
        JobUiLimitDisplaySettings = 0x00000010, JobUiLimitDesktop = 0x00000040,
        JobUiLimitExitWindows = 0x00000080;
    public const uint CpuRateControlEnable = 0x1, CpuRateControlWeightBased = 0x2,
        CpuRateControlHardCap = 0x4;
    public const uint BelowNormalPriorityClass = 0x00004000, NormalPriorityClass = 0x00000020;
    public const uint ProcessSetQuota = 0x0100, ProcessTerminate = 0x0001,
        ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
            ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicUiRestrictionsData
    {
        public uint UIRestrictionsClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicAccountingInformationData
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime,
            ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimitInformationData
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobCpuRateControlInformationData
    {
        public uint ControlFlags;

        /// <summary>
        /// A union in Windows: hundredths of a percent under HARD_CAP, a weight of 1 to 9 under
        /// WEIGHT_BASED, and a packed pair of rates under MIN_MAX_RATE. One field carries whichever
        /// of those the flags asked for.
        /// </summary>
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
