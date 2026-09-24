param([Parameter(Mandatory)][string]$OutputDirectory)
# PowerShell 7. No model calls; starts/stops only its own published validation instances.
# Unanswered first launch prevents provider registration and sign-in startup changes.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must be inside this checkout.'
}
if (Get-Process ARS -ErrorAction SilentlyContinue) { throw 'Quit ARS before this check. Existing work was not stopped.' }
$product = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ARS'
$settings = Join-Path $product 'settings.json'
if ((Test-Path $settings) -and (Get-Content -Raw $settings | ConvertFrom-Json).FirstRunDone) {
    throw 'This live check requires unanswered first launch to protect owner configuration.'
}
$exe = Join-Path $root 'out/ARS.exe'
[IO.Directory]::CreateDirectory($output) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
public static class TrayProbe {
    [StructLayout(LayoutKind.Sequential)]
    struct Identifier { public uint size; public IntPtr window; public uint id; public Guid guid; }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Data {
        public uint size; public IntPtr window; public uint id, flags, callback; public IntPtr icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public uint state, mask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info;
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string title;
        public uint infoFlags; public Guid guid; public IntPtr balloon;
    }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint operation, ref Data data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    public static Guid Identity(string exe) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
        "Deskweave.Tray.v1|" + System.IO.Path.GetFullPath(exe).ToUpperInvariant())).AsSpan(0, 16));
    public static bool Exists(Guid guid) {
        var id = new Identifier { size = (uint)Marshal.SizeOf<Identifier>(), guid = guid };
        // S_FALSE also succeeds: the icon lives in the collapsed overflow.
        return Shell_NotifyIconGetRect(ref id, out _) >= 0;
    }
    public static void Remove(Guid guid) {
        var d = new Data { size = (uint)Marshal.SizeOf<Data>(), flags = 0x20, guid = guid,
            tip = "", info = "", title = "" };
        Shell_NotifyIcon(2, ref d);
    }
    public static int Windows(uint pid, bool trayOnly) {
        int count = 0;
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid) return true;
            var name = new StringBuilder(256); GetWindowText(h, name, name.Capacity);
            if (trayOnly ? name.ToString() == "Deskweave tray" : IsWindowVisible(h)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
    public static uint Foreground() { GetWindowThreadProcessId(GetForegroundWindow(), out uint p); return p; }
}
'@
$identity = [TrayProbe]::Identity($exe)
function Hash([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'missing' } }
$protected = @($settings, (Join-Path $product 'shell.json'), (Join-Path $env:USERPROFILE '.claude.json'), (Join-Path $env:USERPROFILE '.codex/config.toml'))
$before = @{}; foreach ($file in $protected) { $before[$file] = Hash $file }
$checks = [Collections.Generic.List[string]]::new()
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$report = [ordered]@{ observedAt = [DateTimeOffset]::UtcNow; modelCalls = 0; identity = $identity; checks = $checks; crashGhostsObserved = 0 }
function Check([bool]$ok, [string]$claim) { if (-not $ok) { throw $claim }; $checks.Add($claim); Write-Output "PASS $claim" }
function Start-Owned {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = Split-Path $exe
    $start.ArgumentList.Add('--background')
    $start.Environment['CLAUDE_CONFIG_DIR'] = Join-Path $output 'agents/claude'
    $start.Environment['CODEX_HOME'] = Join-Path $output 'agents/codex'
    $p = [Diagnostics.Process]::Start($start)
    $children.Add($p)
    return $p
}
function Wait-Tray($p) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $p.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if ([TrayProbe]::Windows($p.Id, $true) -eq 1 -and [TrayProbe]::Exists($identity)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Tray did not become ready for validation process $($p.Id)."
}
$failure = $null
try {
    $app = Start-Owned
    Wait-Tray $app
    Check ([TrayProbe]::Exists($identity)) 'Published ARS registers its persistent tray identity'
    foreach ($batch in 1..5) {
        $duplicates = @(foreach ($i in 1..8) { Start-Owned })
        foreach ($p in $duplicates) {
            if (-not $p.WaitForExit(20000) -or $p.ExitCode -ne 0) { throw 'A duplicate launch failed to exit cleanly.' }
        }
        Check (@(Get-Process ARS).Count -eq 1 -and -not $app.HasExited -and [TrayProbe]::Exists($identity) -and [TrayProbe]::Windows($app.Id, $true) -eq 1) "Launch burst ${batch}: one app, one tray owner, same shell identity"
    }
    foreach ($cycle in 1..5) {
        $app.Kill(); $app.WaitForExit(10000) | Out-Null
        if ([TrayProbe]::Exists($identity)) { $report.crashGhostsObserved++ }
        $app = Start-Owned
        Wait-Tray $app
        Check (@(Get-Process ARS).Count -eq 1 -and [TrayProbe]::Windows($app.Id, $true) -eq 1 -and [TrayProbe]::Exists($identity)) "Abrupt-stop recovery ${cycle}: one owner reclaims the same tray identity"
    }
    Check ([TrayProbe]::Windows($app.Id, $false) -eq 0 -and [TrayProbe]::Foreground() -ne $app.Id) 'Repeated background launches leave the hub hidden and do not leave ARS in the foreground'
    foreach ($file in $protected) { if ((Hash $file) -ne $before[$file]) { throw "Protected configuration changed: $file" } }
    Check $true 'Owner settings, shell preferences and checked provider configurations are unchanged'
}
catch { $failure = $_.ToString() }
finally {
    foreach ($p in $children) {
        if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
    }
    # The abrupt-exit test intentionally bypassed disposal. Clean up only this test's published
    # icon, only after every validation process exited, and never if another ARS appeared.
    if (-not (Get-Process ARS -ErrorAction SilentlyContinue)) { [TrayProbe]::Remove($identity) }
    $report.validationInstancesExited = @($children | Where-Object { -not $_.HasExited }).Count -eq 0
    $report.trayEntryRemoved = -not [TrayProbe]::Exists($identity)
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report.exeSha256 = Hash $exe
    $report.appSha256 = Hash (Join-Path $root 'out/ARS.dll')
    $report.engineSha256 = Hash (Join-Path $root 'out/Deskweave.AgentWorkspaces.dll')
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'live-tray-report.json') -Encoding utf8
}
if ($failure) { throw $failure }
if (-not $report.validationInstancesExited -or -not $report.trayEntryRemoved) { throw 'Validation cleanup did not finish.' }
