using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

/// <summary>One window living on an agent's desktop.</summary>
/// <param name="Responding">
/// False when the window's thread did not answer a bounded message. Its title is then whatever
/// could be read without waiting, and nothing synchronous is sent to it.
/// </param>
public sealed record AgentWindow(nint Handle, string Title, string ClassName, int X, int Y,
    int Width, int Height, bool Responding = true);

/// <summary>
/// One capture attempt. A frame can come back with windows missing from it, because a window whose
/// thread has stopped pumping cannot be printed without parking whoever asked for the picture.
/// </summary>
/// <param name="Image">The composed picture, or null when nothing could be photographed.</param>
/// <param name="Windows">Windows on the screen when the attempt was made.</param>
/// <param name="Unresponsive">How many of them were skipped because their thread was not answering.</param>
/// <param name="TimedOut">The desktop pump did not answer inside the caller's bound at all.</param>
public sealed record DesktopFrame(BitmapSource? Image, int Windows, int Unresponsive, bool TimedOut)
{
    /// <summary>Whether this frame shows everything that is on the screen.</summary>
    public bool Complete => Image is not null && Unresponsive == 0 && !TimedOut;
}

/// <summary>
/// What became of one piece of typing.
/// </summary>
/// <param name="Landed">
/// False means the text is not in the application. Never true merely because characters were sent.
/// </param>
/// <param name="Target">The control it went to, named the way the owner would name it.</param>
/// <param name="How">Which route carried it, or why nothing did.</param>
/// <param name="Verified">
/// False when the control publishes no way to read it back. The text was delivered and the
/// application probably took it, but this cannot prove it, and saying so is the point.
/// </param>
public sealed record TypedText(bool Landed, string Target, string How, bool Verified = true)
{
    /// <summary>The owner took the workspace mid-type. Nothing happened and nothing is claimed.</summary>
    public static readonly TypedText Discarded = new(false, "", "discarded; the owner has the workspace");

    /// <summary>The desktop pump ran out of patience, which means an application has wedged it.</summary>
    public static readonly TypedText Wedged =
        new(false, "", "the workspace stopped answering; nothing is known to have been typed");

    /// <summary>One line for the agent and the evidence log, in that order of importance.</summary>
    public override string ToString() => (Landed, Verified) switch
    {
        (true, true) => $"typed into {Target}, {How}, and it is there",
        (true, false) => $"typed into {Target}, {How}. That window publishes no way to read it back,"
            + " so this is delivery and not proof - look before relying on it",
        _ when Target.Length == 0 => How,
        _ => $"not typed: {How}. The keyboard was on {Target}",
    };
}

/// <summary>
/// A real Windows desktop object that belongs to one agent. Apps launched into it render, run and
/// are driveable at native speed, but their windows never appear on the owner's screen and cannot
/// receive or steal the owner's mouse and keyboard - the desktop is the Windows UI security boundary.
///
/// Measured on 2026-08-21 on Windows 10 Pro N 19045 with no hypervisor, no elevation and no feature
/// install: two apps launched, both captured at full resolution, typing delivered by window message.
/// SendInput returned 0, which is why <see cref="TypeText"/> posts WM_CHAR instead.
/// </summary>
public sealed partial class AgentDesktop : IDisposable
{
    // ponytail: every user32 call below is desktop-affine, so one thread owns the handle and all
    // work is marshalled to it. A pool would need SetThreadDesktop per rental for no gain.
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;
    // Keep the exact kernel process handles until teardown. A historical PID is not an ownership
    // token: Windows can reuse it after a short-lived launcher exits, making PID-based cleanup
    // capable of terminating an unrelated owner process.
    readonly List<nint> _borrowedProcesses = [];
    readonly nint _desktop;
    readonly bool _ownsDesktop;
    readonly WorkspaceSandbox? _sandbox;
    readonly WorkspaceLimits? _limits;
    WorkspaceLimits? _browserLimits;
    readonly object _lifecycle = new();
    Timer? _muter;
    nint _lastClicked;
    Native.Point _lastPoint;
    long _lease = 1;
    bool _disposed;

    public string Name { get; }

    AgentDesktop(string name, nint desktop, bool ownsDesktop = true, WorkspaceSandbox? sandbox = null,
        WorkspaceLimits? limits = null)
    {
        Name = name;
        _desktop = desktop;
        _ownsDesktop = ownsDesktop;
        _sandbox = sandbox;
        _limits = limits;
        _thread = new Thread(Pump) { IsBackground = true, Name = $"agent-desktop-{name}" };
        _thread.Start();
        // Fails on an STA thread that already owns a window, so the pump stays MTA for its whole life.
        if (!Run(() => Native.SetThreadDesktop(_desktop)))
            throw new InvalidOperationException($"Could not bind to desktop {name}.");
    }

    /// <summary>Creates the desktop, or opens it if this session already made one by that name.</summary>
    /// <param name="mode">
    /// Free removes the USER-object restrictions from the workspace's job. Those restrictions refuse
    /// window handles created outside the job and are a real functional difference from the owner's
    /// own desktop, which is the one thing a workspace must not be. Chrome has always been launched
    /// without them for exactly this reason. Measurement probes that exist to observe the
    /// restrictions must pass Secure explicitly.
    /// </param>
    public static AgentDesktop Create(string name, WorkspacePower power = WorkspacePower.Fast,
        WorkspaceMode mode = WorkspaceMode.Free)
    {
        nint desktop = Native.CreateDesktopW(name, null, 0, 0, Native.GenericAll, 0);
        if (desktop == 0) desktop = Native.OpenDesktopW(name, 0, false, Native.GenericAll);
        if (desktop == 0)
            throw new InvalidOperationException(
                $"Windows refused a desktop named {name} (error {Marshal.GetLastWin32Error()}).");

        // Browser renderers can lower their own tokens. The desktop accepts them just as the
        // ordinary Default desktop does; applications themselves retain the launching user's token.
        WorkspaceSandbox.OpenToLowIntegrity(name);
        WorkspaceSandbox sandbox;
        WorkspaceLimits limits;
        try
        {
            sandbox = WorkspaceSandbox.For(name);
            limits = new WorkspaceLimits(power, restrictUserObjects: mode == WorkspaceMode.Secure);
        }
        catch
        {
            Native.CloseDesktop(desktop);
            throw;
        }
        // From here on, anything copied on this desktop belongs to this workspace and not to the
        // owner. Registering before the first launch means there is no window where it does not.
        ClipboardBroker.Shared.Register(name);
        return new AgentDesktop(name, desktop, ownsDesktop: true, sandbox, limits);
    }

    /// <summary>How much of the machine this workspace may use. Changes take effect immediately.</summary>
    public WorkspacePower Power
    {
        get => _limits?.Power ?? WorkspacePower.Light;
        set
        {
            lock (_lifecycle)
            {
                _limits?.Apply(value);
                _browserLimits?.Apply(value);
            }
        }
    }

    /// <summary>The default working folder. Other files retain normal Windows access.</summary>
    public string? Folder => _sandbox?.Folder;

    /// <summary>How many of this workspace's audio sessions have been silenced so far.</summary>
    public int AudioSessionsMuted { get; private set; }

    /// <summary>Whether an exact live process belongs to this workspace's containment job.</summary>
    internal bool OwnsProcess(int processId) => _limits?.Owns(processId) == true
        || _browserLimits?.Owns(processId) == true;

    /// <summary>
    /// Binds to the desktop this process is already running on. This is how a tool launched into a
    /// workspace sees and drives that workspace: it is already there, so it opens nothing new.
    /// </summary>
    public static AgentDesktop Current()
    {
        // GetThreadDesktop hands back a borrowed handle. Closing it would break the whole process,
        // which is why ownership is tracked instead of assumed.
        nint desktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        if (desktop == 0)
            throw new InvalidOperationException(
                $"Windows would not name this thread's desktop (error {Marshal.GetLastWin32Error()}).");
        var name = new StringBuilder(256);
        Native.GetUserObjectInformationW(desktop, Native.UoiName, name, name.Capacity * 2, out _);
        return new AgentDesktop(name.ToString(), desktop, ownsDesktop: false);
    }

    /// <summary>Desktop names are a Windows object namespace, so keep them short and plain.</summary>
    public static string NameFor(string workspaceId)
    {
        Span<char> safe = stackalloc char[24];
        int length = 0;
        foreach (char letter in workspaceId)
        {
            if (length == safe.Length) break;
            if (char.IsAsciiLetterOrDigit(letter)) safe[length++] = letter;
        }
        return "Deskweave-" + (length == 0 ? "workspace" : new string(safe[..length]));
    }

    /// <summary>
    /// Starts a process directed to this desktop before its first instruction. This is the supported
    /// application route, not a security claim against deliberate same-user USER32 calls.
    /// </summary>
    /// <param name="handles">
    /// Standard input and output for the child, inherited. Browser DevTools uses LaunchBrowser
    /// instead: its explicitly named pipe handles must not also receive stdout or stderr.
    /// </param>
    public int Launch(string exe, string? arguments = null, (nint In, nint Out)? handles = null, long lease = 0)
    {
        lock (_lifecycle)
        {
            if (_disposed || Revoked(lease)) return 0;
            return LaunchCore(exe, arguments, handles, lease, _limits);
        }
    }

    /// <summary>
    /// Chrome owns a nested Windows sandbox whose broker must use desktop USER objects while it
    /// creates the restricted renderer/GPU children. Keep it in an equally bounded kill-on-close
    /// job, but without the general application's USER-object restrictions.
    /// </summary>
    internal int LaunchBrowser(string exe, string? arguments = null,
        (nint In, nint Out)? handles = null, long lease = 0)
    {
        lock (_lifecycle)
        {
            if (_disposed || Revoked(lease)) return 0;
            _browserLimits ??= new WorkspaceLimits(Power, restrictUserObjects: false);
            return LaunchCore(exe, arguments, handles, lease, _browserLimits, pipeHandlesOnly: handles is not null);
        }
    }

    int LaunchCore(string exe, string? arguments, (nint In, nint Out)? handles, long lease,
        WorkspaceLimits? limits, bool pipeHandlesOnly = false)
    {
        // A WPF program chooses how it draws while it starts, and only a software renderer leaves
        // pixels a desktop Windows does not compose can hand back. On before the process runs, off
        // again once it is up: see WorkspaceRenderMode.
        IDisposable? softened = WorkspaceRenderMode.Soften();
        try { return LaunchCore(exe, arguments, handles, lease, limits, ref softened, pipeHandlesOnly); }
        finally { softened?.Dispose(); }
    }

    int LaunchCore(string exe, string? arguments, (nint In, nint Out)? handles, long lease,
        WorkspaceLimits? limits, ref IDisposable? softened, bool pipeHandlesOnly)
    {
        // Inspect the exact image we will start. A visible console can otherwise be delegated to
        // Windows Terminal through COM on Default, despite lpDesktop naming this workspace.
        if (!TryConsoleImage(exe, out string image, out bool console)) return 0;
        var info = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
        // The only supported way to place a process on another desktop. The managed process API has no
        // field for it, which is why this goes straight to CreateProcessW.
        info.lpDesktop = @"WinSta0\" + Name;
        var command = new StringBuilder($"\"{exe}\"{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}");

        // CREATE_NEW_CONSOLE: without it a console program attaches to whatever console the host
        // already owns, so its output lands on the owner's screen instead of the agent's desktop.
        // CREATE_UNICODE_ENVIRONMENT is required whenever an environment block is passed at all.
        uint flags = Native.CreateSuspended | Native.CreateNewConsole | Native.CreateUnicodeEnvironment;
        string? consoleTitle = null;
        if (console && handles is null)
        {
            // The inbox console explicitly refuses default-terminal handoff for minimized startup.
            // This takes effect before console allocation; hiding an already delegated window is late.
            consoleTitle = "Deskweave console " + Guid.NewGuid().ToString("N");
            info.lpTitle = consoleTitle;
            info.dwFlags |= ConsoleStartfUseShowWindow;
            info.wShowWindow = ConsoleShowMinNoActive;
        }
        if (handles is not null)
        {
            // A new console would replace the std handles we are trying to hand over.
            // CREATE_NO_WINDOW also prevents a redirected console from delegating to Terminal.
            flags = Native.CreateSuspended | Native.CreateUnicodeEnvironment | ConsoleCreateNoWindow;
            if (!pipeHandlesOnly)
            {
                info.dwFlags |= Native.StartfUseStdHandles;
                info.hStdInput = handles.Value.In;
                info.hStdOutput = handles.Value.Out;
                info.hStdError = handles.Value.Out;
            }
            // Chromium adopts its two inherited CDP handles from --remote-debugging-io-pipes.
            // Sending ordinary stdout/stderr there would corrupt its NUL-delimited JSON frames.
        }
        // CreateProcess uses the same primary token as this process. A separate desktop changes
        // where windows appear, not which task files the user can access. Never elevate or lower
        // the token merely because a program is running in a workspace.
        Native.ProcessInfo created;
        bool started = handles is { } inherited
            ? CreateWithHandles(image, command, flags, _sandbox?.Environment ?? 0, _sandbox?.Folder,
                ref info, inherited, out created)
            : Native.CreateProcessW(image, command, 0, 0, false, flags,
                _sandbox?.Environment ?? 0, _sandbox?.Folder, ref info, out created);
        if (!started) return 0;

        bool resumed = false;
        bool processHandleRetained = false;
        try
        {
            // The process has not executed yet. Assign through the authoritative handle returned by
            // CreateProcess, then fail closed if Windows refuses; reopening by PID after resume was
            // both racy and silently ignored assignment failure.
            if (limits is not null && !limits.TryTake(created.hProcess)) return 0;
            if (Revoked(lease)) return 0;
            if (Native.ResumeThread(created.hThread) == uint.MaxValue) return 0;
            resumed = true;
            if (consoleTitle is not null)
                RestoreConsoleWhenReady(consoleTitle, System.IO.Path.GetFileName(image), lease);

            if (limits is null)
            {
                lock (_borrowedProcesses) _borrowedProcesses.Add(created.hProcess);
                processHandleRetained = true;
            }

            ClipboardBroker.Shared.Touch(Name);
            StartMuting();
            // The program owns the switch from here: it goes back once this one has drawn, not
            // when this method returns, because nothing has put up a window yet.
            WorkspaceRenderMode.ReleaseWhenStarted(softened, created.dwProcessId, HasWindow);
            softened = null;
            return created.dwProcessId;
        }
        finally
        {
            if (!resumed)
            {
                Native.TerminateProcess(created.hProcess, 1);
                Native.WaitForSingleObject(created.hProcess, 2000);
            }
            Native.CloseHandle(created.hThread);
            if (!processHandleRetained) Native.CloseHandle(created.hProcess);
        }
    }

    // Each launch receives only its requested handles. A per-desktop launch lock cannot prevent
    // another workspace's inheritable pipe ends from existing in this process at the same time.
    // Never fall back to unrestricted inheritance if Windows refuses the explicit list.
    static bool CreateWithHandles(string image, StringBuilder command, uint flags,
        nint environment, string? directory, ref Native.StartupInfo info,
        (nint In, nint Out) handles, out Native.ProcessInfo created)
    {
        created = default;
        if (!Inheritable(handles.In) || !Inheritable(handles.Out)) return false;
        int count = handles.In == handles.Out ? 1 : 2;
        nuint bytes = 0;
        if (Native.InitializeProcThreadAttributeList(0, 1, 0, ref bytes)
            || Marshal.GetLastWin32Error() != 122 || bytes == 0 || bytes > int.MaxValue) return false;
        nint list = 0, values = 0;
        bool initialized = false;
        try
        {
            list = Marshal.AllocHGlobal((int)bytes);
            if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref bytes)) return false;
            initialized = true;
            values = Marshal.AllocHGlobal(count * nint.Size);
            Marshal.WriteIntPtr(values, 0, handles.In);
            if (count == 2) Marshal.WriteIntPtr(values, nint.Size, handles.Out);
            if (!Native.UpdateProcThreadAttribute(list, 0, Native.ProcThreadAttributeHandleList,
                values, (nuint)(count * nint.Size), 0, 0)) return false;
            var extended = new Native.StartupInfoEx { StartupInfo = info, lpAttributeList = list };
            extended.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            return Native.CreateProcessW(image, command, 0, 0, true,
                flags | Native.ExtendedStartupInfoPresent, environment, directory, ref extended, out created);
        }
        finally
        {
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            if (values != 0) Marshal.FreeHGlobal(values);
            if (list != 0) Marshal.FreeHGlobal(list);
        }

        static bool Inheritable(nint handle) => handle != 0 && handle != -1
            && Native.GetHandleInformation(handle, out uint value) && (value & 1) != 0;
    }

    /// <summary>
    /// Visible top-level windows, front of the z-order first, every one of them on the workspace
    /// screen - see <see cref="WindowsOnScreen"/> for why that is a promise rather than a hope.
    /// </summary>
    /// <summary>An empty list means the pump did not answer in time, which reads the same to a
    /// caller as an empty desktop and is the only honest answer either way.</summary>
    public IReadOnlyList<AgentWindow> Windows() => Run(WindowsOnScreen) ?? [];

    /// <summary>Whether one process has put a window on this desktop yet.</summary>
    internal bool HasWindow(int pid) => Windows().Any(window =>
    {
        Native.GetWindowThreadProcessId(window.Handle, out int owner);
        return owner == pid;
    });

    /// <summary>
    /// A workspace desktop is the size of the owner's own screen, so that is the size a whole
    /// capture is. Kept here rather than at each caller: the panel maps the owner's clicks back
    /// through the same numbers, and a dashboard card that disagreed with it by a pixel would
    /// compose the windows onto a canvas of the wrong shape.
    /// </summary>
    public static int ScreenWidth => Math.Max(640, Native.GetSystemMetrics(Native.SmCxScreen));

    public static int ScreenHeight => Math.Max(480, Native.GetSystemMetrics(Native.SmCyScreen));

    /// <summary>The whole desktop, at the size this workspace's screen actually is.</summary>
    public BitmapSource? CaptureScreen() => CaptureScreen(ScreenWidth, ScreenHeight);

    /// <summary>The whole desktop as one image, or null when there was nothing to photograph.</summary>
    public BitmapSource? CaptureScreen(int width, int height) => Capture(width, height).Image;

    /// <summary>
    /// One bounded wait for a capture. Native PrintWindow can outlast the caller's bound; keep
    /// that capture pending until the pump finishes it so later viewers cannot build a backlog.
    /// What it could not photograph is reported rather than quietly missing.
    /// </summary>
    public DesktopFrame Capture(int width, int height)
    {
        if (Volatile.Read(ref _disposed)) return new(null, 0, 0, false);
        // Preserve direct use from the desktop thread; waiting on its own queue would deadlock.
        if (Thread.CurrentThread == _thread) return CaptureOnPump(width, height);
        return _capture.Take(width, height, CaptureBound, () => QueueCapture(width, height));
    }

    Task<DesktopFrame> QueueCapture(int width, int height)
    {
        var done = new TaskCompletionSource<DesktopFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try
                {
                    done.TrySetResult(Volatile.Read(ref _disposed)
                        ? new DesktopFrame(null, 0, 0, false) : CaptureOnPump(width, height));
                }
                catch (Exception error) { done.TrySetException(error); }
            });
        }
        // CompleteAdding can race submission; a disposed collection has the same unavailable
        // result. An accepted capture completes on the pump, even while Dispose drains the queue.
        catch (InvalidOperationException) { done.TrySetResult(new(null, 0, 0, false)); }
        return done.Task;
    }

    readonly WorkspaceCaptureGate _capture = new();

    /// <summary>
    /// How long a caller will wait for the desktop pump before taking the last picture instead.
    /// Generous enough for an ordinary composite of a busy screen, short enough that the panel's
    /// own thread never visibly stalls on one.
    /// </summary>
    internal static readonly TimeSpan CaptureBound = TimeSpan.FromSeconds(2);

    DesktopFrame CaptureOnPump(int width, int height)
    {
        IReadOnlyList<AgentWindow> windows = WindowsOnScreen();
        // A new workspace can be observed before its first app has painted. Its empty background
        // is still a valid screenshot; returning null here made first tool calls intermittently fail.
        int skipped = windows.Count(window => !window.Responding);

        // A process gets 10,000 GDI handles. Anything thrown between taking a handle and giving it
        // back - running out of memory converting a 4K frame is the realistic one - used to leak a
        // DC and a bitmap per attempt, so every handle below is given back in a finally, and one
        // Windows refused is never drawn on.
        nint screenDc = Native.GetDC(0);
        if (screenDc == 0) return new DesktopFrame(null, windows.Count, skipped, false);
        nint canvasDc = 0, canvas = 0, previousCanvas = 0;
        try
        {
            canvasDc = Native.CreateCompatibleDC(screenDc);
            if (canvasDc == 0) return new DesktopFrame(null, windows.Count, skipped, false);
            canvas = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (canvas == 0) return new DesktopFrame(null, windows.Count, skipped, false);
            previousCanvas = Native.SelectObject(canvasDc, canvas);
            WorkspaceWall.Paint(canvasDc, screenDc, width, height);

            // Back to front, so the window the agent is actually using ends up on top. A window wholly
            // inside one printed in front of it is not printed at all: that one's copy covers every
            // pixel of it anyway, and PrintWindow is most of a frame's cost - with a maximized app
            // in front, every other window's.
            // Each window is drawn only where it really shows: see Drawn.
            AgentWindow[] drawn = [.. windows.Select(Drawn)];
            int[] hiddenBy = HiddenBehind(drawn);
            var unprinted = new HashSet<int>();
            var kept = new HashSet<nint>();
            for (int i = windows.Count - 1; i >= 0; i--)
                if (hiddenBy[i] < 0 && !Print(windows[i], drawn[i])) unprinted.Add(i);
            // A window meant to hide others that then failed to print left a hole where they belong.
            // That rare frame is drawn again the plain way, every window.
            if (hiddenBy.Any(unprinted.Contains))
            {
                WorkspaceWall.Paint(canvasDc, screenDc, width, height);
                for (int i = windows.Count - 1; i >= 0; i--) Print(windows[i], drawn[i]);
            }
            // Only what is on screen is worth keeping: a window that closed or went behind another
            // gives its picture back, so this holds at most one bitmap per visible window.
            foreach (nint gone in _prints.Keys.Where(handle => !kept.Contains(handle)).ToArray())
                ForgetPrint(gone);

            bool Print(AgentWindow window, AgentWindow shows)
            {
                // PrintWindow sends WM_PRINT and waits for the window's own thread to draw. A window
                // that is not pumping never answers, and there is no timeout on that call, so it is
                // drawn from its last picture instead of taking the capture down with it.
                if (!window.Responding) { Ghost(window, shows); return false; }
                nint windowDc = Native.CreateCompatibleDC(screenDc);
                if (windowDc == 0) return false;
                // The window's bitmap from the last frame is printed into again when it is still the
                // same size, rather than made and thrown away on every frame.
                bool reused = _prints.TryGetValue(window.Handle, out WindowPrint last)
                    && last.Width == window.Width && last.Height == window.Height;
                nint bitmap = reused ? last.Bitmap : 0, previous = 0;
                bool keep = false;
                try
                {
                    if (!reused) bitmap = Native.CreateCompatibleBitmap(screenDc, window.Width, window.Height);
                    if (bitmap == 0) return false;
                    previous = Native.SelectObject(windowDc, bitmap);
                    // PW_RENDERFULLCONTENT: a secondary desktop is not DWM-composited, and without this
                    // flag hardware-accelerated windows print as black.
                    if (!Native.PrintWindow(window.Handle, windowDc, Native.PwRenderFullContent)) return false;
                    Native.BitBlt(canvasDc, shows.X, shows.Y, shows.Width, shows.Height,
                        windowDc, shows.X - window.X, shows.Y - window.Y, Native.SrcCopy);
                    keep = true;
                    return true;
                }
                finally
                {
                    if (previous != 0) Native.SelectObject(windowDc, previous);
                    Native.DeleteDC(windowDc);
                    // The pair this window owns is given back on its own, so one bad window does not
                    // leak a bitmap on every frame after it.
                    if (keep)
                    {
                        if (!reused) { ForgetPrint(window.Handle); _prints[window.Handle] = new(bitmap, window.Width, window.Height); }
                        kept.Add(window.Handle);
                    }
                    else if (reused) ForgetPrint(window.Handle);
                    else if (bitmap != 0) Native.DeleteObject(bitmap);
                }
            }

            // A window that is not answering is drawn where it is, as it last looked, washed pale and
            // labelled. Leaving it out showed whatever was behind it - another app, the browser - as
            // though that were the window asked about, and an agent read it that way (2026-09-22).
            void Ghost(AgentWindow window, AgentWindow shows)
            {
                var box = new Native.Rect
                {
                    Left = shows.X, Top = shows.Y, Right = shows.X + shows.Width, Bottom = shows.Y + shows.Height,
                };
                if (_prints.TryGetValue(window.Handle, out WindowPrint last)
                    && last.Width == window.Width && last.Height == window.Height)
                {
                    nint source = Native.CreateCompatibleDC(screenDc);
                    nint previous = Native.SelectObject(source, last.Bitmap);
                    Native.BitBlt(canvasDc, shows.X, shows.Y, shows.Width, shows.Height,
                        source, shows.X - window.X, shows.Y - window.Y, Native.SrcCopy);
                    Native.SelectObject(source, previous);
                    Native.DeleteDC(source);
                    kept.Add(window.Handle);
                    Wash(canvasDc, screenDc, box);
                }
                else
                {
                    Native.SetBkColor(canvasDc, 0x00E6E6E6);
                    Native.ExtTextOutW(canvasDc, 0, 0, Native.EtoOpaque, ref box, null, 0, 0);
                }
                Label(canvasDc, box, "Not responding");
            }

            // Out of the device context before it is read, exactly as before. The finally below then
            // has nothing left to restore.
            Native.SelectObject(canvasDc, previousCanvas);
            previousCanvas = 0;
            BitmapSource? image = null;
            try
            {
                BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(canvas, 0, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                raw.Freeze();
                // A device bitmap carries no alpha, so every pixel arrives fully transparent and WPF
                // blends the frame into whatever is behind it. Bgr32 drops the channel and makes it opaque.
                image = new FormatConvertedBitmap(raw, PixelFormats.Bgr32, null, 0);
                image.Freeze();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // A window died mid-capture. The next frame is 500ms away.
            }

            return new DesktopFrame(image, windows.Count, skipped, false);
        }
        finally
        {
            if (previousCanvas != 0) Native.SelectObject(canvasDc, previousCanvas);
            if (canvas != 0) Native.DeleteObject(canvas);
            if (canvasDc != 0) Native.DeleteDC(canvasDc);
            Native.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// The part of a window that is actually drawn. A sizable Windows 10 window carries an invisible
    /// resize border, 7 px on the left, right and bottom at 100%, inside its rectangle; the owner's
    /// desktop shows what is behind it there, but PrintWindow paints it black, and every window on
    /// an agent's screen wore a black frame in the corner and the hub (2026-09-22). DWM reports the
    /// visible frame even for a window on a workspace desktop; anything it reports outside the
    /// rectangle is not believed, and a window it knows nothing about is drawn whole as before.
    /// </summary>
    internal static AgentWindow Drawn(AgentWindow window)
    {
        if (Native.DwmGetWindowAttribute(window.Handle, Native.DwmwaExtendedFrameBounds,
                out Native.Rect frame, Marshal.SizeOf<Native.Rect>()) != 0
            || frame.Left < window.X || frame.Top < window.Y
            || frame.Right > window.X + window.Width || frame.Bottom > window.Y + window.Height
            || frame.Right - frame.Left < window.Width / 2 || frame.Bottom - frame.Top < window.Height / 2)
            return window;
        return window with { X = frame.Left, Y = frame.Top, Width = frame.Right - frame.Left, Height = frame.Bottom - frame.Top };
    }

    /// <summary>One window's last good picture, a bitmap this desktop owns until it forgets it.</summary>
    readonly record struct WindowPrint(nint Bitmap, int Width, int Height);

    // Only ever touched on the pump thread, and after it has stopped, by Dispose.
    readonly Dictionary<nint, WindowPrint> _prints = [];

    void ForgetPrint(nint window)
    {
        if (_prints.Remove(window, out WindowPrint print)) Native.DeleteObject(print.Bitmap);
    }

    /// <summary>Pales a piece of the canvas the way Windows pales a window that has stopped answering.</summary>
    static void Wash(nint canvasDc, nint screenDc, Native.Rect box)
    {
        nint paint = Native.CreateCompatibleDC(screenDc);
        nint pixel = Native.CreateCompatibleBitmap(screenDc, 1, 1);
        nint previous = Native.SelectObject(paint, pixel);
        try
        {
            var one = new Native.Rect { Right = 1, Bottom = 1 };
            Native.SetBkColor(paint, 0x00FFFFFF);
            Native.ExtTextOutW(paint, 0, 0, Native.EtoOpaque, ref one, null, 0, 0);
            Native.AlphaBlend(canvasDc, box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top,
                paint, 0, 0, 1, 1, Native.ConstantAlpha(150));
        }
        finally
        {
            Native.SelectObject(paint, previous);
            Native.DeleteObject(pixel);
            Native.DeleteDC(paint);
        }
    }

    /// <summary>A dark tag in the middle of a box, sized to the screen so it reads at any resolution.</summary>
    static void Label(nint canvasDc, Native.Rect box, string text)
    {
        int size = Math.Max(14, (int)Math.Round(ScreenHeight / 50.0));
        nint font = Native.CreateFontW(-size, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint previous = font == 0 ? 0 : Native.SelectObject(canvasDc, font);
        try
        {
            var measured = new Native.Rect();
            Native.DrawTextW(canvasDc, text, text.Length, ref measured, Native.DtSingleLine | Native.DtCalcRect);
            int width = measured.Right - measured.Left + size * 2, height = measured.Bottom - measured.Top + size;
            int left = (box.Left + box.Right - width) / 2, top = (box.Top + box.Bottom - height) / 2;
            var tag = new Native.Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
            Native.SetBkColor(canvasDc, 0x00302C2A);
            Native.ExtTextOutW(canvasDc, 0, 0, Native.EtoOpaque, ref tag, null, 0, 0);
            Native.SetBkMode(canvasDc, Native.Transparent);
            Native.SetTextColor(canvasDc, 0x00FFFFFF);
            Native.DrawTextW(canvasDc, text, text.Length, ref tag, Native.DtCenter | Native.DtVCenter | Native.DtSingleLine);
        }
        finally
        {
            if (previous != 0) Native.SelectObject(canvasDc, previous);
            if (font != 0) Native.DeleteObject(font);
        }
    }

    /// <summary>
    /// For each window, front first, the index of a window in front of it whose rectangle holds all
    /// of it, or -1 when some of it can be seen. Only a window that will be printed can hide one.
    /// </summary>
    internal static int[] HiddenBehind(IReadOnlyList<AgentWindow> windows)
    {
        var hiddenBy = new int[windows.Count];
        var covers = new List<int>();
        for (int i = 0; i < windows.Count; i++)
        {
            AgentWindow window = windows[i];
            hiddenBy[i] = -1;
            foreach (int front in covers)
            {
                AgentWindow cover = windows[front];
                if (window.X < cover.X || window.Y < cover.Y || window.X + window.Width > cover.X + cover.Width
                    || window.Y + window.Height > cover.Y + cover.Height) continue;
                hiddenBy[i] = front;
                break;
            }
            // A hidden window lies inside its cover, so anything it would hide, that cover hides too.
            if (hiddenBy[i] < 0 && window.Responding) covers.Add(i);
        }
        return hiddenBy;
    }

    /// <summary>
    /// One window, printed on its own. The panel shows the whole desktop; an agent asking what a
    /// single window looks like - a captcha, a chart, a page it is driving through the browser
    /// protocol - needs exactly one, at any time, whatever else it is doing.
    /// </summary>
    public BitmapSource? CaptureWindow(nint window) => Run(() =>
    {
        if (!Native.IsWindow(window) || !Native.GetWindowRect(window, out Native.Rect rect)) return null;
        // Same rule as the whole screen: never send a synchronous message to a thread that has
        // stopped answering. One window asked for by name is exactly where that is easiest to forget.
        if (!Answering(window)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        // The same handle discipline as the whole-screen capture: nothing taken here survives an
        // exception on the way out, and a handle Windows refused is not printed into.
        nint screenDc = Native.GetDC(0);
        if (screenDc == 0) return null;
        nint windowDc = 0, bitmap = 0, previous = 0;
        try
        {
            windowDc = Native.CreateCompatibleDC(screenDc);
            if (windowDc == 0) return null;
            bitmap = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == 0) return null;
            previous = Native.SelectObject(windowDc, bitmap);
            BitmapSource? image = null;
            if (Native.PrintWindow(window, windowDc, Native.PwRenderFullContent))
            {
                // Out of the device context before it is read, exactly as the whole-screen capture
                // does. The finally below then has nothing left to restore.
                Native.SelectObject(windowDc, previous);
                previous = 0;
                try
                {
                    // Only the part that shows, as on the whole screen: see Drawn.
                    AgentWindow shows = Drawn(new AgentWindow(window, "", "", rect.Left, rect.Top, width, height));
                    BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0,
                        new Int32Rect(shows.X - rect.Left, shows.Y - rect.Top, shows.Width, shows.Height),
                        BitmapSizeOptions.FromEmptyOptions());
                    raw.Freeze();
                    image = new FormatConvertedBitmap(raw, PixelFormats.Bgr32, null, 0);
                    image.Freeze();
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    // The window died between the rect and the print.
                }
            }
            return image;
        }
        finally
        {
            if (previous != 0) Native.SelectObject(windowDc, previous);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            if (windowDc != 0) Native.DeleteDC(windowDc);
            Native.ReleaseDC(0, screenDc);
        }
    });

    /// <summary>
    /// The owner taking control revokes the agent's lease. Input the agent had already queued
    /// carries the old number and is dropped here on the pump, rather than landing under his hands.
    /// </summary>
    public long Revoke() => Interlocked.Increment(ref _lease);

    /// <summary>The number an agent's input must still carry to be delivered. Zero means the owner.</summary>
    public long Lease => Interlocked.Read(ref _lease);

    /// <summary>The window the last click was delivered to, for the gate that drives a real click
    /// through the workspace page's picture and then asks where it landed.</summary>
    internal nint LastClickedForTests => _lastClicked;

    bool Revoked(long lease) => lease != 0 && lease != Interlocked.Read(ref _lease);

    /// <summary>
    /// Sends a mouse press to a point in the agent's screen. Windows routes by window, not by
    /// cursor, so the owner's real pointer never moves and never leaves their own desktop.
    /// </summary>
    public bool Click(int x, int y, bool rightButton = false, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return false;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (!OwnsWindow(target)) return false;
        nint screen = (y & 0xFFFF) << 16 | (x & 0xFFFF);

        // A title bar, a close button, a scrollbar and a resize edge are non-client area and drop
        // WM_LBUTTONDOWN on the floor. Measured 2026-08-23: the element tree offers those buttons,
        // and before this hit test the matching click by coordinate did nothing at all.
        nint answered = Native.SendMessageTimeoutW(target, Native.WmNcHitTest, 0, screen,
            Native.SmtoAbortIfHung, 500, out nint area);
        if (Revoked(lease) || answered == 0) return false;
        if (answered != 0 && (int)area > Native.HtClient)
        {
            nint command = (int)area switch
            {
                Native.HtMinButton => Native.ScMinimize,
                Native.HtMaxButton => Native.IsZoomed(target) ? Native.ScRestore : Native.ScMaximize,
                Native.HtClose => Native.ScClose,
                _ => 0
            };
            if (command != 0)
            {
                Native.PostMessageW(target, Native.WmSysCommand, command, screen);
                _lastClicked = target;
                return true;
            }
            // A title bar is the same trap as those buttons: WM_NCLBUTTONDOWN with HTCAPTION puts
            // the window into DefWindowProc's move loop, which waits on a cursor that is on the
            // owner's desktop and will never move, so the click does nothing at all and the window
            // is left mid-drag. What a click on a title bar means to someone watching a picture of
            // a screen is "bring this window forward", so that is what it does.
            if ((int)area == Native.HtCaption)
            {
                _lastClicked = target;
                return Arrange(target, WindowArrangement.Front, lease: lease);
            }
            // A resize edge, the grow box, the system menu and the menu bar each start a loop of
            // their own on that same cursor. None of them is worth wedging a window for, so the
            // click is refused here and the caller is told it did not land.
            if ((int)area is Native.HtSysMenu or Native.HtGrowBox or Native.HtMenu
                or (>= Native.HtLeft and <= Native.HtBorder)) return false;
            // Everything else non-client - a scrollbar, the help button - gets the ordinary pair.
            Native.PostMessageW(target, Native.WmNcMouseMove, area, screen);
            Native.PostMessageW(target, rightButton ? Native.WmNcRButtonDown : Native.WmNcLButtonDown,
                area, screen);
            Native.PostMessageW(target, rightButton ? Native.WmNcRButtonUp : Native.WmNcLButtonUp,
                area, screen);
            _lastClicked = target;
            return true;
        }

        var client = new Native.Point { X = x, Y = y };
        Native.ScreenToClient(target, ref client);
        nint position = (client.Y & 0xFFFF) << 16 | (client.X & 0xFFFF);

        // A workspace desktop has no foreground window - see Activate for what that does and does not
        // fix.
        Activate(target);
        if (Native.SendMessageTimeoutW(target, Native.WmMouseMove, 0, position,
            Native.SmtoAbortIfHung, 250, out _) == 0) return false;
        if (Revoked(lease)) return false;
        bool pressed = Native.SendMessageTimeoutW(target, rightButton ? Native.WmRButtonDown : Native.WmLButtonDown,
            rightButton ? 2 : 1, position, Native.SmtoAbortIfHung, 250, out _) != 0;
        // Finish this press even if takeover happened during delivery. Never start another press.
        bool released = Native.SendMessageTimeoutW(target, rightButton ? Native.WmRButtonUp : Native.WmLButtonUp,
            0, position, Native.SmtoAbortIfHung, 250, out _) != 0;
        _lastPoint = new Native.Point { X = x, Y = y };
        _lastClicked = target;
        // WPF hit-tests a mouse message against the real hardware pointer rather than the coordinates
        // carried in the message, and that pointer lives on the window station, not this desktop, so
        // it is wherever the owner left it and the message route can never raise Click here - measured
        // 2026-09-20, activation included. See InvokeAtPoint for what does.
        if (IsWpfWindow(Native.GetAncestor(target, 2)) && InvokeAtPoint(x, y)) return true;
        return pressed && released;
    });

    /// <summary>
    /// Makes a window's top-level owner the active window on this desktop, from the pump thread that
    /// already lives on it. A workspace desktop has no foreground window of its own, which is a real
    /// difference from the owner's desktop that some controls key their visual and interactive state
    /// on - a WinForms combo box's drop button and a hover-styled control both read as "inactive"
    /// without this.
    ///
    /// Measured 2026-09-20 with engine-probe's --click-proof, though: activating first did not make a
    /// WPF button's Click fire, before or after this existed. WinForms and a plain Win32 button both
    /// answered a coordinate click either way - they only need mouse capture, not window activation, to
    /// raise Click. So this stays because it is cheap, correct, and the real difference from the
    /// owner's desktop it exists to paper over, but it is not what makes WPF work - see
    /// <see cref="InvokeAtPoint"/> for what was measured to actually do that.
    ///
    /// AttachThreadInput shares the calling thread's input state with the target's for the duration of
    /// the call, which is what lets SetActiveWindow/SetFocus take effect for a window this thread does
    /// not own without SetForegroundWindow's restrictions. Both threads already belong to this
    /// desktop's own window station, so none of this is visible to the owner's real desktop.
    /// </summary>
    void Activate(nint target)
    {
        nint root = Native.GetAncestor(target, 2); // GA_ROOT
        if (root == 0) root = target;
        uint targetThread = (uint)Native.GetWindowThreadProcessId(root, out _);
        uint ourThread = (uint)Native.GetCurrentThreadId();
        if (targetThread == 0 || targetThread == ourThread) return;
        bool attached = Native.AttachThreadInput(ourThread, targetThread, true);
        try
        {
            Native.SetActiveWindow(root);
            Native.SetFocus(root);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(ourThread, targetThread, false);
        }
    }

    /// <summary>
    /// WPF names its top-level window's own class after itself - "HwndWrapper[...]" - which is a
    /// reliable, no-cost way to know before sending a single message that the window ahead is one
    /// where the message route cannot raise Click. Chrome, WinForms and plain Win32 windows carry
    /// their own class names and never match this.
    /// </summary>
    static bool IsWpfWindow(nint window) => Text(window, Native.GetClassNameW).StartsWith("HwndWrapper", StringComparison.Ordinal);

    /// <summary>
    /// Resolves the UI Automation element at a screen point and invokes it - one call, never a tree
    /// walk, and only reached for a WPF window, where <see cref="Click"/> has already measured that
    /// the message route cannot raise Click. This goes around WPF's own mouse pipeline instead of
    /// fighting it: InvokePattern (and the same handful of verbs WorkspaceTree already presses)
    /// call straight into the control's handler, so it does not matter that the real cursor never
    /// moved here. False means the point resolved to nothing actionable - text, a blank pane, a
    /// disabled control - which is not a fault; the caller falls back to reporting what the message
    /// route itself managed.
    /// </summary>
    static bool InvokeAtPoint(int x, int y)
    {
        try
        {
            AutomationElement? element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element is null) return false;
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
            { ((InvokePattern)invoke).Invoke(); return true; }
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
            { ((TogglePattern)toggle).Toggle(); return true; }
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? select))
            { ((SelectionItemPattern)select).Select(); return true; }
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
            {
                var it = (ExpandCollapsePattern)expand;
                if (it.Current.ExpandCollapseState == ExpandCollapseState.Expanded) it.Collapse(); else it.Expand();
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    /// <summary>Scrolls at a point in the agent's screen.</summary>
    public bool Scroll(int x, int y, int delta, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return false;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (!OwnsWindow(target) || Math.Abs((long)delta) > 10000) return false;
        // Bounded wheel messages, without cosmetic pauses between notches.
        nint at = (y & 0xFFFF) << 16 | (x & 0xFFFF);
        for (int left = Math.Abs(delta); left > 0; left -= 120)
        {
            if (Revoked(lease)) return false;
            if (Native.SendMessageTimeoutW(target, Native.WmMouseWheel,
                (Math.Sign(delta) * Math.Min(120, left)) << 16, at, Native.SmtoAbortIfHung, 250, out _) == 0) return false;
        }
        return true;
    });

    /// <summary>
    /// Returns whether the input actually went out. False means the pump discarded it - the lease it
    /// carried is stale because the owner took the workspace - and the caller must not then write
    /// down that it happened. Milestone 4's exit gate found the evidence log claiming a discarded
    /// write had been typed, which is worse than the input landing would have been.
    ///
    /// Types text. WM_CHAR per character, because SendInput returns 0 for a desktop that is not the
    /// input desktop - measured, not assumed. This is also how the agent itself will type.
    /// </summary>
    public bool TypeText(string text, nint window = 0, long lease = 0) => Type(text, window, lease).Landed;

    /// <summary>
    /// Types text and says where it went and whether the control now holds it.
    ///
    /// Delivery used to be the whole answer, and that was the defect: with no window named this
    /// picks whatever window is topmost, so text meant for a page went into a browser's address bar
    /// and the caller was still told "typed". Two calls in a row piled up somewhere nobody was
    /// looking. Delivering a character is not evidence that an application took it, so where the
    /// control can be read back this reads it back, and where it cannot this says so rather than
    /// implying success.
    /// </summary>
    public TypedText Type(string text, nint window = 0, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return TypedText.Discarded;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Focused(window);
        if (target == 0) return new TypedText(false, "", "no window on this desktop had the keyboard");

        string wanted = MessageText(text);
        string where = Describe(target);
        if (wanted.Length == 0) return new TypedText(true, where, "nothing to type");
        bool readable = Readable(target, out string before);

        // Text is WM_CHAR only. Posting WM_KEYDOWN as well lets the app's TranslateMessage create
        // a second WM_CHAR, doubling every ordinary character (measured in the execution probe).
        // Browser keyboard events belong to DevTools; single non-text keys use SendKey below.
        foreach (char letter in wanted)
        {
            if (Revoked(lease)) return TypedText.Discarded;
            // A bounded synchronous delivery means the following batch read observes this write,
            // and a full message queue or hung window cannot be reported as successful typing.
            if (Native.SendMessageTimeoutW(target, Native.WmChar, letter, 1,
                Native.SmtoAbortIfHung, 250, out _) == 0)
                return new TypedText(false, where, "that window stopped accepting characters part way through");
        }

        // A window whose class is not a text control answers WM_GETTEXT with its title, so reading
        // it back would report every successful type as a failure and then type it again. Unverified
        // is the honest answer for those, and the caller is told which window took the text.
        if (!readable) return new TypedText(true, where, "by message", Verified: false);

        // Unchanged is checked before the contents are searched, and that order is the point: a box
        // already holding "cat" contains the "a" that was just swallowed, so searching first would
        // call a failed type a success whenever the text was short.
        string after = ControlText(target);
        if (after != before)
        {
            if (Holds(after, wanted)) return new TypedText(true, where, "by message");
            // Changed, but not to what was asked for. Something took the characters and did its own
            // thing with them - an auto-complete, a shortcut, a validator. Retrying here would type
            // it a second time on top of whatever landed, so this reports instead of escalating.
            return new TypedText(false, where, "the control changed but does not hold that text");
        }

        // Untouched, so a second attempt cannot duplicate anything. Edit and RichEdit controls take
        // their text directly when WM_CHAR is being swallowed, which is the common case behind a
        // read-only-looking box that is not actually read-only.
        if (Revoked(lease)) return TypedText.Discarded;
        if (Native.SendMessageTimeoutString(target, Native.EmReplaceSel, 1, wanted,
                Native.SmtoAbortIfHung, 500, out _) != 0
            && ControlText(target) is { } replaced && replaced != before && Holds(replaced, wanted))
            return new TypedText(true, where, "by replacing the selection");

        return new TypedText(false, where, "it accepted nothing; the text is not in it");
    }) ?? TypedText.Wedged;

    /// <summary>How the owner would name the control that took the text.</summary>
    static string Describe(nint window)
    {
        // Never the control's own WM_GETTEXT: for an edit box that is its contents, and the caller's
        // own text would come back to it disguised as the name of the place it went.
        string kind = Text(window, Native.GetClassNameW);
        nint root = Native.GetAncestor(window, 2);
        // InternalGetWindowText, never GetWindowTextW: naming the target must not send a message to
        // the application, or an application that has stopped pumping parks the desktop pump on the
        // way to reporting where text went. WordPad came up "(not responding)" while this was tested.
        string title = root == 0 ? string.Empty : Text(root, Native.InternalGetWindowText);
        if (title.Length > 60) title = title[..60] + "...";
        if (title.Length == 0) return kind.Length > 0 ? kind : window.ToString();
        return kind.Length > 0 && root != window ? $"{kind} in \"{title}\"" : $"\"{title}\"";
    }

    /// <summary>
    /// The classes whose WM_GETTEXT is their contents rather than a window title. Deliberately a
    /// list and not a guess: treating an unknown class as readable is what would turn a working
    /// type into a duplicate one.
    /// </summary>
    static bool Readable(nint window, out string text)
    {
        text = string.Empty;
        string kind = Text(window, Native.GetClassNameW);
        bool known = kind.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Scintilla", StringComparison.OrdinalIgnoreCase);
        if (!known) return false;
        text = ControlText(window);
        return true;
    }

    /// <summary>A control's contents, bounded, or empty when its thread will not answer.</summary>
    static string ControlText(nint window)
    {
        if (Native.SendMessageTimeoutW(window, Native.WmGetTextLength, 0, 0,
            Native.SmtoAbortIfHung, 250, out nint length) == 0) return string.Empty;
        int count = (int)length;
        if (count <= 0) return string.Empty;
        var buffer = new StringBuilder(count + 1);
        if (Native.SendMessageTimeoutText(window, Native.WmGetText, buffer.Capacity, buffer,
            Native.SmtoAbortIfHung, 250, out _) == 0) return string.Empty;
        return buffer.ToString();
    }

    /// <summary>
    /// Whether a control now holds what was typed. Line endings are compared flattened: typing
    /// sends CR and an edit control stores CRLF, so a literal comparison calls every multi-line
    /// type a failure.
    /// </summary>
    static bool Holds(string contents, string wanted) =>
        wanted.Length == 0 || Flat(contents).Contains(Flat(wanted), StringComparison.Ordinal);

    static string Flat(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Sends one virtual key, for the keys that produce no character (Enter, Tab, arrows).</summary>
    public bool SendKey(int virtualKey, nint window = 0, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return false;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Focused(window);
        if (target == 0) return false;
        string kind = Text(target, Native.GetClassNameW);
        if (kind.Equals("Edit", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            // TranslateMessage runs later for posted key events. In an action group that could
            // insert Enter after the following text. Edit controls accept their character directly.
            int character = virtualKey is 8 or 9 or 13 or 32 ? virtualKey
                : virtualKey is >= 65 and <= 90 ? virtualKey + 32
                : virtualKey is >= 48 and <= 57 ? virtualKey : 0;
            if (character != 0) return Native.SendMessageTimeoutW(target, Native.WmChar, character, 1,
                Native.SmtoAbortIfHung, 250, out _) != 0;
            bool down = Native.SendMessageTimeoutW(target, Native.WmKeyDown, virtualKey, 1,
                Native.SmtoAbortIfHung, 250, out _) != 0;
            bool up = Native.SendMessageTimeoutW(target, Native.WmKeyUp, virtualKey, unchecked((nint)0xC0000001),
                Native.SmtoAbortIfHung, 250, out _) != 0;
            return down && up;
        }
        if (!Native.PostMessageW(target, Native.WmKeyDown, virtualKey, 1)) return false;
        return Native.PostMessageW(target, Native.WmKeyUp, virtualKey, unchecked((nint)0xC0000001));
    });

    /// <summary>
    /// Copies from the workspace. The copy lands on the real clipboard for an instant and the
    /// broker files it under this workspace, so the owner never finds an agent's text in his own.
    /// </summary>
    public void Copy(nint window = 0) => Run(() => Deliver(window, Native.WmCopy));

    /// <summary>
    /// Pastes into the workspace, with this workspace's own clipboard loaded for exactly as long as
    /// the paste takes. Synchronous on purpose: a posted paste would land after the swap back.
    /// </summary>
    public void Paste(nint window = 0) =>
        ClipboardBroker.Shared.WithClipboardOf(Name, () => Run(() => Deliver(window, Native.WmPaste)));

    bool Deliver(nint window, uint message)
    {
        ClipboardBroker.Shared.Touch(Name);
        nint target = Focused(window);
        if (target == 0) return false;
        Native.SendMessageTimeoutW(target, message, 0, 0, Native.SmtoAbortIfHung, 2000, out _);
        return true;
    }

    nint Focused(nint window)
    {
        nint candidate = window;
        if (candidate == 0)
            candidate = OwnsWindow(_lastClicked) ? _lastClicked : WindowsOnPump().FirstOrDefault()?.Handle ?? 0;
        if (!OwnsWindow(candidate)) return 0;
        uint thread = (uint)Native.GetWindowThreadProcessId(candidate, out _);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        if (Native.GetGUIThreadInfo(thread, ref info) && OwnsWindow(info.Focus)
            && (window == 0 || Native.GetAncestor(candidate, 2) == Native.GetAncestor(info.Focus, 2)))
            return info.Focus;
        return candidate;
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _muter?.Dispose();
        _muter = null;
        if (_ownsDesktop) ClipboardBroker.Shared.Unregister(Name);

        nint[] borrowedProcesses;
        lock (_borrowedProcesses)
        {
            borrowedProcesses = [.. _borrowedProcesses];
            _borrowedProcesses.Clear();
        }

        // Stop the desktop-affine pump before dismantling its desktop. The lifecycle gate above
        // prevents a concurrent launch from racing the job teardown.
        _work.CompleteAdding();
        // Every pump operation is bounded, but some intentionally exceed two seconds (typing can
        // take roughly twelve). Resource teardown must not race a pump that is still using them.
        //
        // Bounded, because "every pump operation is bounded" is a claim about our own code and not
        // about Windows: one call into a wedged application used to hold this Join forever and the
        // workspace could not be closed, nor HiveMind shut down, until that application was killed.
        // The job object below kills everything in the workspace anyway, and the pump is a
        // background thread, so going on without it is the safe direction.
        ShutdownWaitedOut = !_thread.Join(ShutdownJoin);

        if (_browserLimits is not null || _limits is not null)
        {
            // The job is authoritative for owned workspaces and includes every inherited child.
            _browserLimits?.Dispose();
            _browserLimits = null;
            _limits?.Dispose();
            WaitForFolderRelease(_sandbox!.Folder);
        }
        else
        {
            // A borrowed Current() desktop has no job. Terminate only processes this instance
            // actually launched, through the original handles rather than reusable PIDs.
            foreach (nint process in borrowedProcesses) Native.TerminateProcess(process, 0);
        }
        // Termination is asynchronous. Give all exact launch handles one shared, bounded wait,
        // then release them. Returning earlier can leave a child's current directory locked.
        long waitUntil = Environment.TickCount64 + 2000;
        foreach (nint process in borrowedProcesses)
        {
            int remaining = (int)Math.Max(0, waitUntil - Environment.TickCount64);
            if (remaining > 0) Native.WaitForSingleObject(process, (uint)remaining);
            Native.CloseHandle(process);
        }

        // Windows destroys the desktop itself once the last window and process on it is gone.
        //
        // Only when the pump has actually stopped. A pump thread that is still inside a wedged
        // application still has this desktop bound and is still enumerating the queue, and closing
        // either underneath it trades a workspace that will not close for a process that crashes.
        // Both are one handle and one collection; the workspace's processes are already dead.
        if (!ShutdownWaitedOut)
        {
            if (_ownsDesktop) Native.CloseDesktop(_desktop);
            _work.Dispose();
            foreach (nint window in _prints.Keys.ToArray()) ForgetPrint(window);
        }
        _sandbox?.Dispose();
    }

    static void WaitForFolderRelease(string folder)
    {
        long waitUntil = Environment.TickCount64 + 2000;
        while (true)
        {
            // DELETE access conflicts with a process that still has the workspace as its current
            // directory. Once this succeeds, no workspace process remains to reacquire the folder.
            nint directory = Native.CreateFileW(folder, Native.DeleteAccess,
                Native.FileShareRead | Native.FileShareWrite | Native.FileShareDelete, 0,
                Native.OpenExisting, Native.FileFlagBackupSemantics, 0);
            if (directory != (nint)(-1))
            {
                Native.CloseHandle(directory);
                return;
            }

            int error = Marshal.GetLastWin32Error();
            if (error is not Native.ErrorSharingViolation and not Native.ErrorLockViolation
                || Environment.TickCount64 >= waitUntil)
                return;
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// An audio session only exists once a process actually plays something, so muting is a sweep
    /// rather than a one-off. Three seconds is the worst case for a blip reaching the owner.
    /// </summary>
    void StartMuting()
    {
        if (_limits is null || _muter is not null) return;
        _muter = new Timer(_ =>
        {
            try { AudioSessionsMuted += WorkspaceAudio.Mute(_limits.Owns); }
            catch (Exception) { /* a workspace with no audio is the state we wanted */ }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    IReadOnlyList<AgentWindow> WindowsOnPump()
    {
        var found = new List<AgentWindow>();
        Native.EnumDesktopWindows(_desktop, (handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || !Native.GetWindowRect(handle, out Native.Rect rect))
                return true;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 64 || height < 64) return true;
            // GetWindowText sends WM_GETTEXT to another process's window and waits for its thread to
            // answer. Measured 2026-09-07: an application that had stopped pumping parked the whole
            // desktop thread there, and with it every capture, every action, and the panel's own UI
            // thread behind it. Nothing here may wait on another process without a bound.
            bool responding = Answering(handle);
            string title = responding ? Text(handle, Native.GetWindowTextW) : Titled(handle);
            string kind = Text(handle, Native.GetClassNameW);   // read from the window, not its thread
            found.Add(new AgentWindow(handle, title, kind, rect.Left, rect.Top, width, height, responding));
            return true;
        }, 0);
        // Windows recycles handle values. A verdict left behind by a window that has since been
        // destroyed would be inherited by whatever new window lands on that number, and leave it out
        // of a frame it was answering perfectly well for. Swept here on the pump thread that owns the
        // map, which is the same thread that just enumerated - no lock, and none wanted.
        if (_notAnswering.Count > 0)
        {
            HashSet<nint> live = found.Select(window => window.Handle).ToHashSet();
            foreach (nint handle in _notAnswering.Keys.ToArray())
                if (!live.Contains(handle)) _notAnswering.Remove(handle);
        }
        return found;
    }

    /// <summary>
    /// Whether a window's thread is still pumping, answered in a fixed 150 ms whatever it does. Both
    /// checks are needed: IsHungAppWindow only reports a window Windows has already given up on, and
    /// a thread that is merely busy for a minute is just as unprintable while it is.
    ///
    /// Only the "not answering" verdict is remembered, and only briefly. A window that answers costs
    /// nothing to ask, so it is asked every time and can never be printed on the strength of a stale
    /// yes; a window that does not answer would otherwise cost the full 150 ms on every frame, which
    /// at two frames a second is most of the interval.
    /// </summary>
    bool Answering(nint window)
    {
        if (_notAnswering.TryGetValue(window, out long until) && Environment.TickCount64 < until) return false;
        bool answered = !Native.IsHungAppWindow(window)
            && Native.SendMessageTimeoutW(window, Native.WmNull, 0, 0,
                Native.SmtoAbortIfHung | Native.SmtoBlock, HungMilliseconds, out _) != 0;
        if (answered) _notAnswering.Remove(window);
        else _notAnswering[window] = Environment.TickCount64 + HungRememberMilliseconds;
        return answered;
    }

    /// <summary>
    /// Whether every window on the screen answers right now, or just the one given - asked afresh
    /// rather than from the brief memory of an earlier "no", because the caller is waiting for that
    /// "no" to turn into a "yes". False too when the pump itself is too busy to ask.
    /// </summary>
    internal bool Answers(nint window = 0) => Run(() =>
    {
        _notAnswering.Clear();
        return window == 0 ? WindowsOnScreen().All(open => open.Responding) : Answering(window);
    }, CaptureBound);

    // Only ever touched on the pump thread, which is the one thread that enumerates windows.
    readonly Dictionary<nint, long> _notAnswering = [];

    const long HungRememberMilliseconds = 750;

    /// <summary>A title for a window that is not answering: what Windows already knows, never a wait.</summary>
    static string Titled(nint window)
    {
        var buffer = new StringBuilder(256);
        int read = Native.InternalGetWindowText(window, buffer, buffer.Capacity);
        string title = read > 0 ? buffer.ToString() : string.Empty;
        return title.Length > 0 ? title + " (not responding)" : "(not responding)";
    }

    const uint HungMilliseconds = 150;

    static string Text(nint window, Func<nint, StringBuilder, int, int> read)
    {
        var buffer = new StringBuilder(256);
        read(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    void Pump()
    {
        foreach (Action job in _work.GetConsumingEnumerable())
        {
            try { job(); }
            catch (Exception) { /* the caller's TaskCompletionSource carries it */ }
        }
    }

    /// <summary>True when Dispose gave up waiting for the pump because an application had wedged it.</summary>
    public bool ShutdownWaitedOut { get; private set; }

    /// <summary>
    /// How long closing a workspace waits for its desktop pump. Long enough for the slowest ordinary
    /// pump operation, short enough that a wedged application cannot hold the workspace open.
    /// </summary>
    internal static readonly TimeSpan ShutdownJoin = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long any other pump operation will wait. Ordinary ones are milliseconds and the slowest
    /// deliberate one - typing a long string - is about twelve seconds, so this is not a limit any
    /// real action reaches. It exists so that nothing in this class can wait on another process
    /// forever: a caller that runs out of patience is told the action did not happen, which is what
    /// every caller already does with a false or a null.
    /// </summary>
    internal static readonly TimeSpan ActionBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Occupies the desktop pump for a while, so the stale-picture path can be reached on purpose.
    /// Only the unresponsive-workspace probe calls this: waiting for a real application to wedge the
    /// pump at the right moment is luck, and a path that is only ever tested by luck is not tested.
    /// </summary>
    internal void OccupyForTests(TimeSpan how)
    {
        try { _work.Add(() => Thread.Sleep(how)); }
        catch (InvalidOperationException) { /* already closing, which is not something to occupy */ }
    }

    T? Run<T>(Func<T> job) => Run(job, ActionBound);

    /// <summary>
    /// Puts one piece of work on the desktop-affine pump. With a bound, a caller that runs out of
    /// patience gets null and carries on: the work stays queued and its answer is dropped, which is
    /// the right trade for a picture and the wrong one for an action, so only capture passes a bound.
    /// </summary>
    T? Run<T>(Func<T> job, TimeSpan? bound)
    {
        ObjectDisposedException.ThrowIf(_disposed && !_work.IsAddingCompleted, this);
        if (Thread.CurrentThread == _thread) return job();

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try { done.SetResult(job()); }
                catch (Exception ex) { done.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(AgentDesktop));
        }
        if (bound is null) return done.Task.GetAwaiter().GetResult();
        return done.Task.Wait(bound.Value) ? done.Task.GetAwaiter().GetResult() : default;
    }
}
