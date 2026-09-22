using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Proves or disproves prompt 4a's theory (design/FIX-PROMPTS-2026-09-20.md): a workspace desktop has
/// no foreground window, so WPF and WinForms drop their "mouse is over this control" state and a
/// coordinate press puts the focus ring on a button while the release never raises Click
/// (found in app testing). Three real windows on a real workspace
/// desktop, each with one button that appends to a file when pressed - PowerShell + Add-Type builds
/// all three. The WPF one is started through AgentDesktop.Launch, not the probe's own process, so
/// WorkspaceRenderMode's software-drawing switch applies to it exactly as it would to a real agent's
/// target.
///
/// Every kind is driven both ways an agent or the owner can press something by coordinate -
/// AgentDesktop.Click and PointerPress/PointerRelease - and the result file is polled rather than
/// trusted from the delivery return value: "the message was accepted" and "the button fired" are
/// different claims, and this probe exists because the product used to only prove the first one.
///
/// Run: Deskweave.Probe.exe --click-proof "C:\absolute\output"
/// Wire it with one line in Program.cs, next to --capture-cost and --corner-rate:
///   if (args.Length == 2 && args[0] == "--click-proof" && Path.IsPathFullyQualified(args[1]))
///       return ClickProof.Run(Path.GetFullPath(args[1]));
/// </summary>
internal static class ClickProof
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Click proof exceeded its 180-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);

        string name = "click-" + Guid.NewGuid().ToString("N")[..8];
        string nonce = Guid.NewGuid().ToString("N")[..8];
        AgentDesktop? desktop = null;
        var results = new List<object>();
        object? failure = null;
        try
        {
            desktop = AgentDesktop.Create(name);
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

            foreach ((string kind, string script) in new[]
            {
                ("wpf", WpfScript), ("winforms", WinFormsScript), ("win32", Win32Script),
            })
            {
                string scriptPath = Path.Combine(output, kind + ".ps1");
                File.WriteAllText(scriptPath, script);
                foreach (string method in new[] { "click", "pointer" })
                {
                    string title = $"{kind}-{method}-{nonce}";
                    string outFile = Path.Combine(output, $"{kind}-{method}.txt");
                    string args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "
                        + $"\"{scriptPath}\" \"{outFile}\" \"{title}\"";
                    int pid = desktop.Launch(powershell, args);

                    AgentWindow? window = WaitForWindow(desktop, title, TimeSpan.FromSeconds(25));
                    if (window is null)
                    {
                        results.Add(new { kind, method, title, started = pid > 0, found = false, sent = false, fired = false });
                        continue;
                    }

                    int cx = window.X + window.Width / 2, cy = window.Y + window.Height / 2;
                    bool sent = method == "click"
                        ? desktop.Click(cx, cy)
                        : desktop.PointerPress(cx, cy, false, false) & desktop.PointerRelease(cx, cy);
                    bool fired = WaitFor(() => File.Exists(outFile) && File.ReadAllText(outFile).Contains("clicked"),
                        TimeSpan.FromSeconds(2));

                    results.Add(new
                    {
                        kind, method, title, started = pid > 0, found = true,
                        window = $"{window.Width}x{window.Height} at ({window.X},{window.Y})",
                        clickPoint = $"({cx},{cy})",
                        sent, fired,
                    });
                }
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally { desktop?.Dispose(); }

        File.WriteAllText(Path.Combine(output, "click-proof.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow,
            results,
            failure,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    static AgentWindow? WaitForWindow(AgentDesktop desktop, string title, TimeSpan bound)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < bound)
        {
            AgentWindow? found = desktop.Windows().FirstOrDefault(w => w.Title == title);
            if (found is not null) return found;
            Thread.Sleep(200);
        }
        return null;
    }

    static bool WaitFor(Func<bool> predicate, TimeSpan bound)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < bound) { if (predicate()) return true; Thread.Sleep(50); }
        return predicate();
    }

    // Every fixture's button fills its whole client area, so the window's own rectangle centre - read
    // back from AgentDesktop.Windows, in physical pixels - always lands on the button regardless of
    // window chrome or DPI, without the probe needing to ask the app where its own control is.

    const string WpfScript = """
param([string]$OutFile, [string]$Title)
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase
$window = New-Object System.Windows.Window
$window.Title = $Title
$window.Width = 360
$window.Height = 240
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$window.Left = 40
$window.Top = 40
$button = New-Object System.Windows.Controls.Button
$button.Content = "Press"
$button.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Stretch
$button.VerticalAlignment = [System.Windows.VerticalAlignment]::Stretch
$button.Add_Click({ [System.IO.File]::AppendAllText($OutFile, "clicked`n") })
$window.Content = $button
$window.Show()
[System.Windows.Threading.Dispatcher]::Run()
""";

    const string WinFormsScript = """
param([string]$OutFile, [string]$Title)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.Width = 360
$form.Height = 240
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(40, 40)
$button = New-Object System.Windows.Forms.Button
$button.Dock = [System.Windows.Forms.DockStyle]::Fill
$button.Text = "Press"
$button.Add_Click({ [System.IO.File]::AppendAllText($OutFile, "clicked`n") })
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)
""";

    // A plain Win32 top-level window with a real common-control BUTTON child, built from scratch with
    // no WPF or WinForms underneath it - the control case for the activation theory, since a native
    // button only needs mouse capture to answer WM_LBUTTONUP, not window activation.
    const string Win32Script = """
param([string]$OutFile, [string]$Title)
$src = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class DeskweaveWin32Probe
{
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);

    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_CHILD = 0x40000000;
    const uint WM_COMMAND = 0x0111;
    const uint WM_DESTROY = 0x0002;
    const int SW_SHOW = 5;

    static string _outFile = "";
    static WndProcDelegate _proc;

    static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COMMAND) File.AppendAllText(_outFile, "clicked" + Environment.NewLine);
        else if (msg == WM_DESTROY) PostQuitMessage(0);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public static void Run(string outFile, string title)
    {
        _outFile = outFile;
        _proc = WindowProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = _proc,
            hInstance = Marshal.GetHINSTANCE(typeof(DeskweaveWin32Probe).Module),
            hbrBackground = new IntPtr(6), // COLOR_WINDOW + 1
            lpszClassName = "DeskweaveWin32Probe",
        };
        if (RegisterClassW(ref wc) == 0)
        {
            File.AppendAllText(_outFile + ".error", "RegisterClassW failed: " + Marshal.GetLastWin32Error());
            return;
        }
        IntPtr hwnd = CreateWindowExW(0, "DeskweaveWin32Probe", title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            40, 40, 360, 240, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            File.AppendAllText(_outFile + ".error", "CreateWindowExW failed: " + Marshal.GetLastWin32Error());
            return;
        }
        CreateWindowExW(0, "BUTTON", "Press", WS_CHILD | WS_VISIBLE,
            0, 0, 360, 200, hwnd, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }
}
'@
Add-Type -TypeDefinition $src -Language CSharp
[DeskweaveWin32Probe]::Run($OutFile, $Title)
""";
}
