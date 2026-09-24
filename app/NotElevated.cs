using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Deskweave;

/// <summary>
/// Programs an agent starts through ARS inherit its token, so a copy started with Run as
/// administrator would give the agent administrator rights. That copy restarts itself through
/// Explorer, which runs it as the signed-in user, and exits. With UAC off every program runs with
/// the full token and there is no lesser one to restart with, so that case is left alone.
/// </summary>
static class NotElevated
{
    /// <summary>True when this copy should not continue: it restarted as the user or said why not.</summary>
    internal static bool Handle(string[] args)
    {
        if (!StartedAsAdministrator()) return false;
        try
        {
            StartAsUser(Environment.ProcessPath!, string.Join(' ', args.Select(Quote)));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MessageBox.Show("ARS does not run as administrator, because programs your agents start would get the same rights. Start ARS normally.",
                "ARS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return true;
    }

    static string Quote(string arg) => arg.Length > 0 && !arg.Any(c => c is ' ' or '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    static bool StartedAsAdministrator()
    {
        const int TokenElevationType = 18, TokenElevationTypeFull = 2;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out nint token)) return false;
        try
        {
            return GetTokenInformation(token, TokenElevationType, out int type, sizeof(int), out _) && type == TokenElevationTypeFull;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Asks the desktop's Explorer window to start the program, so it runs with Explorer's token.</summary>
    internal static void StartAsUser(string file, string arguments)
    {
        const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0;
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object location = 0 /* CSIDL_DESKTOP */, root = null!;
        object desktop = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        Guid topBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"), shellBrowser = typeof(IShellBrowser).GUID;
        var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(ref topBrowser, ref shellBrowser);
        Guid dispatch = new("00020400-0000-0000-C000-000000000046");
        dynamic view = browser.QueryActiveShellView().GetItemObject(SVGIO_BACKGROUND, ref dispatch);
        view.Application.ShellExecute(file, arguments, Path.GetDirectoryName(file) ?? "", "open", 1);
    }

    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, int access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int kind, out int value, int size, out int returned);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int kind, out int window, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    // Only the slot order matters for the methods before the one used.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(int item, ref Guid riid);
    }
}
