using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
    /// Standard input and output for the child, inherited. Only the browser transport uses this:
    /// Chrome's DevTools pipe is a pair of inherited handles rather than a port, so it collides with
    /// nothing on the owner's PC.
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
            return LaunchCore(exe, arguments, handles, lease, _browserLimits);
        }
    }

    int LaunchCore(string exe, string? arguments, (nint In, nint Out)? handles, long lease,
        WorkspaceLimits? limits)
    {
        var info = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
        // The only supported way to place a process on another desktop. The managed process API has no
        // field for it, which is why this goes straight to CreateProcessW.
        info.lpDesktop = @"WinSta0\" + Name;
        var command = new StringBuilder($"\"{exe}\"{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}");

        // CREATE_NEW_CONSOLE: without it a console program attaches to whatever console the host
        // already owns, so its output lands on the owner's screen instead of the agent's desktop.
        // CREATE_UNICODE_ENVIRONMENT is required whenever an environment block is passed at all.
        uint flags = Native.CreateSuspended | Native.CreateNewConsole | Native.CreateUnicodeEnvironment;
        bool inherit = handles is not null;
        if (handles is not null)
        {
            // A new console would replace the std handles we are trying to hand over.
            flags = Native.CreateSuspended | Native.CreateUnicodeEnvironment;
            info.dwFlags |= Native.StartfUseStdHandles;
            info.hStdInput = handles.Value.In;
            info.hStdOutput = handles.Value.Out;
            info.hStdError = handles.Value.Out;
        }
        // CreateProcess uses the same primary token as this process. A separate desktop changes
        // where windows appear, not which task files the user can access. Never elevate or lower
        // the token merely because a program is running in a workspace.
        bool started = Native.CreateProcessW(null, command, 0, 0, inherit, flags,
            _sandbox?.Environment ?? 0, _sandbox?.Folder, ref info, out Native.ProcessInfo created);
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

            if (limits is null)
            {
                lock (_borrowedProcesses) _borrowedProcesses.Add(created.hProcess);
                processHandleRetained = true;
            }

            ClipboardBroker.Shared.Touch(Name);
            StartMuting();
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

    /// <summary>
    /// Visible top-level windows, front of the z-order first, every one of them on the workspace
    /// screen - see <see cref="WindowsOnScreen"/> for why that is a promise rather than a hope.
    /// </summary>
    /// <summary>An empty list means the pump did not answer in time, which reads the same to a
    /// caller as an empty desktop and is the only honest answer either way.</summary>
    public IReadOnlyList<AgentWindow> Windows() => Run(WindowsOnScreen) ?? [];

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
    /// One bounded capture attempt. Never waits on the desktop pump for longer than
    /// <see cref="CaptureBound"/>, and never on an application for longer than a message timeout, so
    /// a workspace holding a wedged program still gives back the panel, the owner's takeover and
    /// shutdown. What it could not photograph is reported rather than quietly missing.
    /// </summary>
    public DesktopFrame Capture(int width, int height)
    {
        // One capture at a time. Queueing a second one behind a pump that is busy would build a
        // backlog at two frames a second and hand every caller an ever-staler picture.
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return new DesktopFrame(null, 0, 0, true);
        try
        {
            DesktopFrame? frame = Run(() => CaptureOnPump(width, height), CaptureBound);
            return frame ?? new DesktopFrame(null, 0, 0, true);
        }
        catch (ObjectDisposedException) { return new DesktopFrame(null, 0, 0, false); }
        finally { Interlocked.Exchange(ref _capturing, 0); }
    }

    int _capturing;

    /// <summary>
    /// How long a caller will wait for the desktop pump before taking the last picture instead.
    /// Generous enough for an ordinary composite of a busy screen, short enough that the panel's
    /// own thread never visibly stalls on one.
    /// </summary>
    internal static readonly TimeSpan CaptureBound = TimeSpan.FromSeconds(2);

    DesktopFrame CaptureOnPump(int width, int height)
    {
        IReadOnlyList<AgentWindow> windows = WindowsOnScreen();
        if (windows.Count == 0) return new DesktopFrame(null, 0, 0, false);
        int skipped = windows.Count(window => !window.Responding);

        nint screenDc = Native.GetDC(0);
        nint canvasDc = Native.CreateCompatibleDC(screenDc);
        nint canvas = Native.CreateCompatibleBitmap(screenDc, width, height);
        nint previousCanvas = Native.SelectObject(canvasDc, canvas);
        Native.SetBkColor(canvasDc, 0x00201A14);
        var whole = new Native.Rect { Right = width, Bottom = height };
        Native.ExtTextOutW(canvasDc, 0, 0, Native.EtoOpaque, ref whole, null, 0, 0);

        // Back to front, so the window the agent is actually using ends up on top.
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            AgentWindow window = windows[i];
            // PrintWindow sends WM_PRINT and waits for the window's own thread to draw. A window
            // that is not pumping never answers, and there is no timeout on that call, so it is
            // left out of the picture instead of taking the capture down with it.
            if (!window.Responding) continue;
            nint windowDc = Native.CreateCompatibleDC(screenDc);
            nint bitmap = Native.CreateCompatibleBitmap(screenDc, window.Width, window.Height);
            nint previous = Native.SelectObject(windowDc, bitmap);
            // PW_RENDERFULLCONTENT: a secondary desktop is not DWM-composited, and without this flag
            // hardware-accelerated windows print as black.
            if (Native.PrintWindow(window.Handle, windowDc, Native.PwRenderFullContent))
                Native.BitBlt(canvasDc, window.X, window.Y, window.Width, window.Height,
                    windowDc, 0, 0, Native.SrcCopy);
            Native.SelectObject(windowDc, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(windowDc);
        }

        Native.SelectObject(canvasDc, previousCanvas);
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

        Native.DeleteObject(canvas);
        Native.DeleteDC(canvasDc);
        Native.ReleaseDC(0, screenDc);
        return new DesktopFrame(image, windows.Count, skipped, false);
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

        nint screenDc = Native.GetDC(0);
        nint windowDc = Native.CreateCompatibleDC(screenDc);
        nint bitmap = Native.CreateCompatibleBitmap(screenDc, width, height);
        nint previous = Native.SelectObject(windowDc, bitmap);
        BitmapSource? image = null;
        if (Native.PrintWindow(window, windowDc, Native.PwRenderFullContent))
        {
            try
            {
                BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
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
        Native.SelectObject(windowDc, previous);
        Native.DeleteObject(bitmap);
        Native.DeleteDC(windowDc);
        Native.ReleaseDC(0, screenDc);
        return image;
    });

    /// <summary>
    /// The owner taking control revokes the agent's lease. Input the agent had already queued
    /// carries the old number and is dropped here on the pump, rather than landing under his hands.
    /// </summary>
    public long Revoke() => Interlocked.Increment(ref _lease);

    /// <summary>The number an agent's input must still carry to be delivered. Zero means the owner.</summary>
    public long Lease => Interlocked.Read(ref _lease);

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
            // Everything else non-client - a scrollbar, a resize edge - gets the ordinary pair. Some
            // of those also track the real cursor, so the element tree is the reliable route there.
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
        return pressed && released;
    });

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
    public bool TypeText(string text, nint window = 0, long lease = 0) => Run(() =>
    {
        if (Revoked(lease)) return false;
        ClipboardBroker.Shared.Touch(Name);
        nint target = Focused(window);
        if (target == 0) return false;
        // Text is WM_CHAR only. Posting WM_KEYDOWN as well lets the app's TranslateMessage create
        // a second WM_CHAR, doubling every ordinary character (measured in the execution probe).
        // Browser keyboard events belong to DevTools; single non-text keys use SendKey below.
        foreach (char letter in MessageText(text))
        {
            if (Revoked(lease)) return false;
            // A bounded synchronous delivery means the following batch read observes this write,
            // and a full message queue or hung window cannot be reported as successful typing.
            if (Native.SendMessageTimeoutW(target, Native.WmChar, letter, 1,
                Native.SmtoAbortIfHung, 250, out _) == 0) return false;
        }
        return true;
    });

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
