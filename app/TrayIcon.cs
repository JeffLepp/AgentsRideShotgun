using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Deskweave;

/// <summary>
/// One shell identity per install, not per process/window. A crashed or forcibly stopped process
/// cannot leave a new ghost on every restart. Construct only while holding the app-instance mutex;
/// remove the icon before releasing that mutex. Never touches Explorer's cache or other apps' icons.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    internal const int CallbackMessage = 0x8001;
    internal static readonly int TaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
    internal delegate bool Notify(uint operation, ref IconData data);
    readonly Notify _notify;
    readonly Guid _identity;
    readonly Icon _icon;
    readonly HwndSource _source;
    readonly DispatcherTimer _retry;
    bool _disposed;

    internal ContextMenuStrip Menu { get; }
    internal event Action? OpenRequested;
    internal event Action? BalloonClicked;
    internal IntPtr Handle => _source.Handle;

    // Windows binds an unsigned icon GUID to its executable path. Include the path so an installed
    // copy and an out/ build both work; never include the PID, build version, timestamp or window.
    internal static Guid IdentityFor(string executable) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "Deskweave.Tray.v1|" + Path.GetFullPath(executable).ToUpperInvariant())).AsSpan(0, 16));

    internal TrayIcon(Icon icon, ContextMenuStrip menu, Guid identity, Notify? notify = null)
    {
        _icon = (Icon)icon.Clone();
        Menu = menu;
        _identity = identity;
        _notify = notify ?? Shell_NotifyIcon;
        // Hidden top-level window: unlike a message-only window it receives TaskbarCreated when
        // Explorer restarts. It is never shown, activated, or included in the taskbar.
        _source = new HwndSource(new HwndSourceParameters("Deskweave tray")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0
        });
        _source.AddHook(WindowMessage);
        _retry = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => Register(), _source.Dispatcher);
        _retry.Stop();
        Menu.Closed += MenuClosed;
        // Only this install's stale entry, after the single-instance lock was acquired.
        Send(2, Data());
        Register();
    }

    IconData Data(uint flags = 0) => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = Handle, Id = 1,
        Flags = flags | 0x20, Identity = _identity, Tip = "", Info = "", Title = ""
    };

    bool Send(uint operation, IconData data) => _notify(operation, ref data);

    internal void Register()
    {
        if (_disposed) return;
        var data = Data(1 | 2 | 4 | 0x80); // MESSAGE, ICON, TIP, SHOWTIP, and GUID
        data.Callback = CallbackMessage;
        data.Icon = _icon.Handle;
        data.Tip = "Deskweave";
        // A duplicate ADD is rejected, then MODIFY refreshes that same entry. Always identify both
        // operations by GUID so recovery cannot accumulate entries with new callback-window handles.
        bool present = Send(0, data) || Send(1, data);
        if (present)
        {
            var version = Data();
            version.Version = 4;
            Send(4, version);
            _retry.Stop();
        }
        else _retry.Start(); // Explorer may not yet be ready at sign-in. No polling when healthy.
    }

    internal void ShowBalloonTip(string title, string text)
    {
        if (_disposed) return;
        var data = Data(0x10);
        data.Title = Truncate(title, 63);
        data.Info = Truncate(text, 255);
        data.InfoFlags = 1 | 0x80; // Information, respecting Windows quiet time
        if (!Send(1, data)) Register(); // Do not queue stale requests or create another identity.
    }

    static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == TaskbarCreated) Register();
        else if (message == CallbackMessage)
        {
            handled = true;
            switch ((int)((long)lParam & 0xffff))
            {
                case 0x203: // WM_LBUTTONDBLCLK: preserve the existing double-click action
                case 0x401: // NIN_KEYSELECT: Enter/Space from the notification area
                    OpenRequested?.Invoke();
                    break;
                case 0x405: // NIN_BALLOONUSERCLICK
                    BalloonClicked?.Invoke();
                    break;
                case 0x7b: // WM_CONTEXTMENU, including keyboard invocation
                    var point = new Point(unchecked((short)(long)wParam), unchecked((short)((long)wParam >> 16)));
                    if (point == new Point(-1, -1)) point = Cursor.Position;
                    SetForegroundWindow(Handle);
                    Menu.Show(point);
                    break;
            }
        }
        return IntPtr.Zero;
    }

    void MenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (!_disposed) Send(3, Data()); // NIM_SETFOCUS: return keyboard navigation to the tray
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Stop();
        // Delete before destroying the callback window, icon resource, or single-instance mutex.
        Send(2, Data());
        Menu.Closed -= MenuClosed;
        Menu.Dispose();
        _source.RemoveHook(WindowMessage);
        _source.Dispose();
        _icon.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct IconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Identity;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Shell_NotifyIcon(uint operation, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr window);
}
