using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// Tells the owner, on his own desktop, that a workspace finished, failed, needs him, or was
/// interrupted. A workspace runs with its panel closed and Deskweave minimised, so an outcome that
/// only exists inside the panel is an outcome nobody sees for hours.
///
/// This is a Shell_NotifyIcon balloon, which Windows 10 renders as an ordinary toast in the Action
/// Center. The icon is registered only for as long as the balloon is up and then removed, so the
/// module never leaves a second permanent icon beside Deskweave's own in the notification area.
/// </summary>
internal static class WorkspaceNotice
{
    /// <summary>Raised for every notice, whether or not Windows showed one. The probes read this.</summary>
    internal static event Action<string, string>? Raised;

    /// <summary>Set by the probes so a measurement run does not put balloons on the owner's screen.</summary>
    internal static bool SilentForTests { get; set; }

    /// <summary>Every notice raised in this session, newest last. Bounded; only the probes read it.</summary>
    internal static IReadOnlyList<(string Title, string Body)> Given { get { lock (Log) return [.. Log]; } }

    static readonly List<(string Title, string Body)> Log = [];

    /// <summary>
    /// One line about one workspace. Never called twice for the same outcome: the caller decides
    /// that from the record, because the point of writing the announced state down is that a restart
    /// does not re-announce something the owner has already been told.
    /// </summary>
    internal static void Show(string title, string body)
    {
        lock (Log)
        {
            Log.Add((title, body));
            if (Log.Count > 50) Log.RemoveAt(0);
        }
        Raised?.Invoke(title, body);
        if (SilentForTests) return;
        try { Balloon(title, body); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException
            or InvalidOperationException or ExternalException)
        {
            // A desktop that will not take a notification is not a reason to lose the outcome; the
            // panel and the card still carry it.
        }
    }

    static void Balloon(string title, string body)
    {
        // A message-only window would be simplest, but Shell_NotifyIcon wants a window that can
        // receive its callbacks, and a zero-sized invisible one is the least intrusive that works.
        var carrier = new Window
        {
            Width = 0, Height = 0, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            AllowsTransparency = true, Background = null, Opacity = 0,
        };
        carrier.Show();
        nint handle = new WindowInteropHelper(carrier).Handle;
        if (handle == 0) { carrier.Close(); return; }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = 1,
            uFlags = NifIcon | NifInfo | NifTip,
            hIcon = LoadIconW(0, (nint)ApplicationIcon),
            szTip = "Deskweave workspaces",
            szInfoTitle = Cut(title, 63),
            szInfo = Cut(body, 255),
            dwInfoFlags = InfoIconInfo,
        };
        if (!ShellNotifyIconW(NimAdd, ref data)) { carrier.Close(); return; }
        ShellNotifyIconW(NimModify, ref data);

        // The balloon lives in the Action Center once Windows has taken it; the icon does not need
        // to stay for it to. Remove it after the shell has certainly picked it up.
        var remove = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        remove.Tick += (_, _) =>
        {
            remove.Stop();
            var gone = new NotifyIconData { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = handle, uID = 1 };
            ShellNotifyIconW(NimDelete, ref gone);
            carrier.Close();
        };
        remove.Start();
    }

    static string Cut(string text, int max)
    {
        string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }

    const uint NimAdd = 0x00000000, NimModify = 0x00000001, NimDelete = 0x00000002;
    const uint NifIcon = 0x00000002, NifTip = 0x00000004, NifInfo = 0x00000010;
    const uint InfoIconInfo = 0x00000001;
    const int ApplicationIcon = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint LoadIconW(nint instance, nint name);
}
