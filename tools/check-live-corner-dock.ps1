param([Parameter(Mandatory)][string]$OutputDirectory)
# PowerShell 7. Checks the actual published app through its bridge and native/UIA surfaces.
# The tab is invoked through UI Automation; the owner's cursor and keyboard never move.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence must stay inside this checkout.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$project = Join-Path $output 'Corner dock fixture'
[IO.Directory]::CreateDirectory((Join-Path $project '.git')) | Out-Null
$exe = Join-Path $root 'out/ARS.exe'
$app = @(Get-Process ARS -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
if ($app.Count -ne 1) { throw 'Expected one running out/ARS.exe.' }
$app = $app[0]
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CornerDockCheck {
    delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    public static IntPtr Find(int process, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != process || !IsWindowVisible(h)) return true;
            var s = new StringBuilder(256); GetWindowText(h, s, s.Capacity);
            if (s.ToString() != title) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ observedAt = [DateTimeOffset]::UtcNow; modelCalls = 0; checks = $checks
    version = (Get-Item $exe).VersionInfo.ProductVersion; pid = $app.Id
    exeSha256 = (Get-FileHash $exe).Hash
    engineSha256 = (Get-FileHash (Join-Path $root 'out/Deskweave.AgentWorkspaces.dll')).Hash }
function Check([bool]$ok, [string]$claim) { if (-not $ok) { throw $claim }; $checks.Add($claim); Write-Output "PASS $claim" }
function Wait-Window([string]$title) {
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $h = [CornerDockCheck]::Find($app.Id, $title)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $title. Check that the hub is hidden, Corner is Comes and goes, and no fullscreen app is visible."
}
function Bounds([IntPtr]$h) {
    $r = [CornerDockCheck+Rect]::new()
    if (-not [CornerDockCheck]::GetWindowRect($h, [ref]$r)) { throw 'Window bounds unavailable.' }
    return @($r.Left, $r.Top, $r.Right, $r.Bottom)
}
function Capture([IntPtr]$h, [string]$name) {
    $r = Bounds $h
    $bitmap = [Drawing.Bitmap]::new($r[2] - $r[0], $r[3] - $r[1])
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $ok = [CornerDockCheck]::PrintWindow($h, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try {
        if (-not $ok) { throw 'Window capture failed.' }
        $bitmap.Save((Join-Path $output $name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}
$bridge = $null
$failure = $null
try {
    $module = $app.Modules | Where-Object ModuleName -eq 'Deskweave.AgentWorkspaces.dll' | Select-Object -First 1
    Check ($module.FileName -eq (Join-Path $root 'out/Deskweave.AgentWorkspaces.dll')) 'The live app uses this checkout''s published engine'
    $ticket = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ARS/agent-workspaces.access/router.json'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'out/Bridge/ARS.WorkspaceBridge.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $project
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--workspace'); $start.ArgumentList.Add($ticket)
    $bridge = [Diagnostics.Process]::Start($start)
    $errors = $bridge.StandardError.ReadToEndAsync()
    $script:requestId = 0
    function Request([string]$method, $parameters) {
        $script:requestId++
        $bridge.StandardInput.WriteLine((@{jsonrpc='2.0';id=$script:requestId;method=$method;params=$parameters} | ConvertTo-Json -Compress -Depth 12))
        $read = $bridge.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(45000)) { throw "Bridge timed out: $method" }
        $reply = $read.Result | ConvertFrom-Json
        if ($reply.error -or $reply.result.isError) { throw ($reply | ConvertTo-Json -Depth 10) }
        return $reply.result
    }
    $null = Request 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='deskweave-corner-check';version='1'}}
    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $null = Request 'tools/call' @{name='computer';arguments=@{screenshot=$true}}
    $state = Request 'tools/call' @{name='status';arguments=@{}}
    $report.workspace = ($state.content[0].text | ConvertFrom-Json).workspace
    $corner = Wait-Window 'Workspace corner view'
    Start-Sleep -Milliseconds 350
    $before = Bounds $corner
    Capture $corner 'live-open.png'
    $null = Request 'tools/call' @{name='release';arguments=@{}}
    $tab = Wait-Window 'Workspace edge tab'
    Start-Sleep -Milliseconds 300
    Check (-not [CornerDockCheck]::IsWindowVisible($corner)) 'After quiet time the full corner hides and its edge tab remains'
    Capture $tab 'live-tab.png'
    $tabBounds = Bounds $tab
    $scale = [CornerDockCheck]::GetDpiForWindow($tab) / 96.0
    Check (($tabBounds[2]-$tabBounds[0]) -eq [math]::Round(28*$scale) -and ($tabBounds[3]-$tabBounds[1]) -eq [math]::Round(64*$scale)) 'The published edge tab has the intended 28 by 64 DIP hit target'
    $surface = [Windows.Automation.AutomationElement]::FromHandle($tab)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $button = $surface.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    Check ($button.Current.Name -like 'Show * workspace') 'The tab exposes a named accessible action'
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $restored = Wait-Window 'Workspace corner view'
    Start-Sleep -Milliseconds 350
    Check ($restored -eq $corner -and ((Bounds $restored) -join ',') -eq ($before -join ',')) 'Clicking the tab restores the same native window at the same size and position'
    Capture $restored 'live-restored.png'
    $after = Request 'tools/call' @{name='status';arguments=@{}}
    $afterState = $after.content[0].text | ConvertFrom-Json
    Check ($afterState.workspace.id -eq $report.workspace.id -and -not $afterState.hasControl) 'Revealing preserves workspace identity and does not acquire agent control'
    $null = Request 'tools/call' @{name='release';arguments=@{}}
    $tab = Wait-Window 'Workspace edge tab'
    Check (-not [CornerDockCheck]::IsWindowVisible($restored)) 'The restored window tucks away again after five quiet seconds'
    $report.bounds = @{ before=$before; tab=$tabBounds; restored=(Bounds $restored) }
}
catch { $failure = $_; $report.failure = $_.ToString() }
finally {
    if ($bridge) {
        $bridge.StandardInput.Close()
        if (-not $bridge.WaitForExit(10000)) { $bridge.Kill() }
        $report.bridgeErrors = $errors.Result
        $bridge.Dispose()
    }
    $report.success = $null -eq $failure
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'live-corner-report.json') -Encoding utf8
}
if ($failure) { throw $failure }
