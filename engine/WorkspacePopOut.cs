using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// One of a workspace's windows taken out onto the owner's own desktop: they pulled it off a picture
/// of the workspace, or pressed Open on my desktop. Windows cannot move a running window from one
/// desktop to another, so this starts it again over there - a page in their own browser, an app from
/// its own program, arguments and folder - and closes an app's copy in here, so there is one of it,
/// on their screen. Only the owner's own gesture reaches this; no agent tool can.
/// </summary>
internal static class WorkspacePopOut
{
    /// <summary>What taking one window out would start. Page: the workspace browser's current
    /// page, opened in their browser. Otherwise the program that owns the window.</summary>
    internal sealed record Plan(nint Window, string Title, bool Page, string? Program, string? Arguments,
        string? Folder, int ProcessId);

    // Windows' own furniture, and hosts that are not the program a person would recognise. Starting
    // any of these again on the owner's desktop would do nothing useful or something surprising.
    static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "conhost.exe", "OpenConsole.exe", "WindowsTerminal.exe", "dllhost.exe",
        "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "TextInputHost.exe", "rundll32.exe", "ARS.exe", "Deskweave.exe",
    };

    /// <summary>What taking this window out would do, or null when it cannot be started again on the
    /// owner's desktop: ARS's own screen furniture, consoles, packaged apps, anything whose
    /// command line cannot be read.</summary>
    internal static Plan? For(WorkspaceRuntime runtime, nint window)
    {
        if (runtime.Plane is not { } plane || window == 0) return null;
        // A dialog goes with its app: its owner is what gets started.
        nint root = Native.GetAncestor(window, 3);   // GA_ROOTOWNER
        if (root == 0) root = window;
        Native.GetWindowThreadProcessId(root, out int pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        if (Class(root) is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "PseudoConsoleWindow") return null;
        string title = Title(root);
        if (pid == plane.BrowserProcessId) return new(root, title, true, null, null, null, pid);
        if (!Read(pid, out string program, out string? arguments, out string? folder)) return null;
        return new(root, title.Length > 0 ? title : Path.GetFileNameWithoutExtension(program), false,
            program, arguments, folder, pid);
    }

    /// <summary>
    /// Does it, on the owner's behalf. <paramref name="at"/> is where they let go, in screen pixels,
    /// or null from the button, which leaves placement to the program. Returns one short line for
    /// them, and tells the agents on this workspace, so none of them starts it again in here.
    /// </summary>
    internal static async Task<string> Run(WorkspaceRuntime runtime, Plan plan, (int X, int Y)? at)
    {
        if (plan.Page)
        {
            string address = runtime.Plane is { } plane ? await plane.Address(CancellationToken.None).ConfigureAwait(true) : "";
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? page) || page.Scheme is not ("http" or "https" or "file"))
                return "That page has no address to open";
            try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            { return "Your browser would not open it"; }
            runtime.Plane?.TellAgents($"The owner opened {page.AbsoluteUri} in their own browser. Your copy here is unchanged.");
            return "Opened in your browser";
        }

        string name = plan.Title;
        // A running virtual machine is locked to the window that holds it; a second one only errors.
        if (OneCopy.Contains(Path.GetFileName(plan.Program!)))
            return $"{name} can only run in one place. Close it in here first, then start it on your desktop";
        Process started;
        try
        {
            // Started by ARS itself, which runs on the owner's desktop: the new process lands
            // there, with their own environment rather than the workspace's redirected one.
            var start = new ProcessStartInfo(plan.Program!, plan.Arguments ?? "") { UseShellExecute = false };
            if (plan.Folder is { Length: > 0 } folder && Directory.Exists(folder)) start.WorkingDirectory = folder;
            started = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Windows would not start {name} on your desktop, so it stays here";
        }
        using (started)
        {
            nint shown = await (WindowOfForTests ?? WindowOf)(started, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (shown == 0 && HasExited(started))
                return $"{name} closed straight away on your desktop, so it stays here";
            if (shown != 0)
            {
                if (at is { } point) Place(shown, point);
                SetForegroundWindow(shown);
            }
            // A message box instead of the app: usually "already running". The copy in here stays,
            // or the owner would be left with an error and nothing else. The gate's stand-in has no real window.
            if (shown != 0 && WindowOfForTests is null && !LooksLikeApp(shown))
                return $"{name} showed a message on your desktop instead of opening, so it stays here too";
        }

        // One copy: the agent's goes, the way its own close button would close it, so an app that
        // asks "save changes?" asks in there rather than losing anything.
        if (runtime.Computer is { } computer)
        {
            IReadOnlyList<AgentWindow> windows = await Task.Run(() => computer.Windows()).ConfigureAwait(true);
            foreach (AgentWindow window in windows)
            {
                Native.GetWindowThreadProcessId(window.Handle, out int owner);
                if (owner == plan.ProcessId) computer.Arrange(window.Handle, WindowArrangement.Close);
            }
        }
        runtime.Plane?.TellAgents($"The owner moved \"{name}\" to their own desktop and closed it here. "
            + "Do not start it again in the workspace unless they ask.");
        return $"{name} is on your desktop now · it started fresh";
    }

    static readonly HashSet<string> OneCopy = new(StringComparer.OrdinalIgnoreCase) { "VirtualBoxVM.exe" };

    /// <summary>A resizable or maximizable window is an app; a fixed one without either is a
    /// message. A style heuristic: a fixed-size main window keeps both copies, which loses nothing.</summary>
    static bool LooksLikeApp(nint window)
    {
        const nint maximizeBox = 0x00010000;
        nint style = Native.GetWindowLongPtrW(window, Native.GwlStyle);
        return (style & ((nint)Native.WsThickFrame | maximizeBox)) != 0;
    }

    static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Stands in for waiting on a real window on the owner's desktop, so the gate can take
    /// the rest of the path without putting anything on their screen.</summary>
    internal static Func<Process, TimeSpan, Task<nint>>? WindowOfForTests;

    /// <summary>The first visible top-level window of the new process or one it started (a launcher
    /// such as py.exe hands the window to a child), on the owner's desktop. Gives up at once when
    /// the program and everything it started have gone, rather than waiting out the patience.</summary>
    static async Task<nint> WindowOf(Process started, TimeSpan patience)
    {
        int pid = started.Id;
        long until = Environment.TickCount64 + (long)patience.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            HashSet<int> family = Family(pid);
            nint found = 0;
            EnumWindows((window, _) =>
            {
                if (!Native.IsWindowVisible(window) || Native.GetAncestor(window, 3) != window) return true;
                Native.GetWindowThreadProcessId(window, out int owner);
                if (!family.Contains(owner)) return true;
                found = window;
                return false;
            }, 0);
            if (found != 0) return found;
            if (HasExited(started) && family.Count == 1) return 0;
            await Task.Delay(150).ConfigureAwait(true);
        }
        return 0;
    }

    static HashSet<int> Family(int pid)
    {
        var family = new HashSet<int> { pid };
        nint snapshot = CreateToolhelp32Snapshot(2, 0);   // TH32CS_SNAPPROCESS
        if (snapshot == -1) return family;
        try
        {
            var entry = new ProcessEntry { dwSize = Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                if (entry.th32ParentProcessID == pid) family.Add(entry.th32ProcessID);
        }
        finally { Native.CloseHandle(snapshot); }
        return family;
    }

    /// <summary>Where they let go: the title bar under the pointer, kept on that monitor's work area.</summary>
    static void Place(nint window, (int X, int Y) point)
    {
        if (!Native.GetWindowRect(window, out Native.Rect rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int left = point.X - Math.Min(width / 3, 160), top = point.Y - 14;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        nint monitor = MonitorFromPoint(new Native.Point { X = point.X, Y = point.Y }, 2);   // NEAREST
        if (monitor != 0 && GetMonitorInfoW(monitor, ref info))
        {
            left = Math.Clamp(left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
            top = Math.Clamp(top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        }
        const uint noSize = 0x0001, noZOrder = 0x0004;
        SetWindowPos(window, 0, left, top, 0, 0, noSize | noZOrder);
    }

    static bool Read(int pid, out string program, out string? arguments, out string? folder)
    {
        program = "";
        arguments = folder = null;
        const uint queryLimited = 0x1000, vmRead = 0x0010;
        nint process = Native.OpenProcess(queryLimited | vmRead, false, pid);
        if (process == 0) return false;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref size)) return false;
            program = path.ToString();
            // A packaged app cannot be started from its own executable; Windows has to activate it.
            if (NotApps.Contains(Path.GetFileName(program))
                || program.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
            if (CommandLine(process) is not { } line) return false;
            arguments = AfterProgram(line);
            folder = (IsWow64Process(process, out bool wow) && wow ? null : CurrentDirectory(process))
                ?? Path.GetDirectoryName(program);
            return true;
        }
        finally { Native.CloseHandle(process); }
    }

    /// <summary>Everything after the program on its own command line, exactly as it was written.</summary>
    internal static string? AfterProgram(string line)
    {
        string rest = line.TrimStart();
        if (rest.StartsWith('"'))
        {
            int close = rest.IndexOf('"', 1);
            rest = close < 0 ? "" : rest[(close + 1)..];
        }
        else
        {
            int space = rest.IndexOfAny([' ', '\t']);
            rest = space < 0 ? "" : rest[space..];
        }
        rest = rest.Trim();
        return rest.Length == 0 ? null : rest;
    }

    static string? CommandLine(nint process)
    {
        // ProcessCommandLineInformation: a UNICODE_STRING followed by its own characters.
        const int info = 60;
        int size = 0;
        NtQueryInformationProcess(process, info, 0, 0, ref size);
        if (size <= 0 || size > 1 << 20) return null;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(process, info, buffer, size, ref size) != 0) return null;
            int length = Marshal.ReadInt16(buffer);
            nint text = Marshal.ReadIntPtr(buffer, 8);
            return length <= 0 || text == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The folder the program was started in, read from its process parameters: x64 only,
    /// PEB.ProcessParameters at 0x20 and CurrentDirectory.DosPath at 0x38 of those.</summary>
    static string? CurrentDirectory(nint process)
    {
        nint basic = Marshal.AllocHGlobal(48);
        try
        {
            int size = 48;
            if (NtQueryInformationProcess(process, 0, basic, 48, ref size) != 0) return null;
            nint peb = Marshal.ReadIntPtr(basic, 8);
            if (ReadPointer(process, peb + 0x20) is not { } parameters) return null;
            byte[] path = new byte[16];
            if (!ReadProcessMemory(process, parameters + 0x38, path, 16, out _)) return null;
            int length = BitConverter.ToUInt16(path, 0);
            nint text = (nint)BitConverter.ToInt64(path, 8);
            if (length <= 0 || length > 4096 || text == 0) return null;
            byte[] chars = new byte[length];
            if (!ReadProcessMemory(process, text, chars, length, out _)) return null;
            string folder = Encoding.Unicode.GetString(chars);
            return folder.Length > 3 ? folder.TrimEnd('\\') : folder;
        }
        finally { Marshal.FreeHGlobal(basic); }
    }

    static nint? ReadPointer(nint process, nint at)
    {
        byte[] value = new byte[8];
        return ReadProcessMemory(process, at, value, 8, out _) ? (nint)BitConverter.ToInt64(value, 0) : null;
    }

    static string Title(nint window)
    {
        var text = new StringBuilder(256);
        Native.GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    static string Class(nint window)
    {
        var text = new StringBuilder(128);
        Native.GetClassNameW(window, text, text.Capacity);
        return text.ToString();
    }

    delegate bool WindowVisit(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int cbSize; public Native.Rect Monitor, Work; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisit visit, nint parameter);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(Native.Point point, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(nint process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool IsWow64Process(nint process, out bool wow64);
    [DllImport("kernel32.dll")]
    static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, int size, out nint read);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(nint process, int infoClass, nint info, int size, ref int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, int process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
}
