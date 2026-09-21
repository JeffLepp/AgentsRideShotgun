param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$BackgroundOnly, [switch]$Compact)
# Exercises the published shell without global input. -Compact resizes the owner's strip;
# its ordinary saved window geometry retains that explicitly requested preference.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must stay inside this checkout.'
}
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShellCheckWindow {
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr Shell(int process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) => { uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid != process || !IsWindowVisible(hwnd)) return true;
            var title = new System.Text.StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString() != "Deskweave") return true;
            found = hwnd; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static int VisibleWindows(int process) {
        int count = 0;
        EnumWindows((hwnd, _) => { uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == process && IsWindowVisible(hwnd)) count++; return true; }, IntPtr.Zero);
        return count;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
}
'@
$exe = Join-Path $root 'out\Deskweave.exe'
$app = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($app.Count -ne 1) { throw 'Expected one running published Deskweave instance.' }
$app = $app[0]
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ pid = $app.Id; version = (Get-Item $exe).VersionInfo.ProductVersion; backgroundOnly = [bool]$BackgroundOnly; compact = [bool]$Compact; checks = $checks }
$failure = $null
function Check([bool]$ok, [string]$claim) { if (-not $ok) { throw $claim }; $checks.Add($claim); "PASS $claim" }
function Control([string]$name) {
    $query = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    $found = $script:surface.FindFirst([Windows.Automation.TreeScope]::Descendants, $query)
    if ($null -eq $found) { throw "Missing published control: $name" }
    return $found
}
function Invoke([string]$name) { (Control $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 200 }
try {
    $engine = $app.Modules | Where-Object ModuleName -eq 'HiveMind.AgentWorkspaces.dll' | Select-Object -First 1
    Check ($engine.FileName -eq (Join-Path $root 'out\HiveMind.AgentWorkspaces.dll')) 'The live app loaded this checkout''s published engine'
    $second = Start-Process -FilePath $exe -ArgumentList '--background' -WindowStyle Hidden -PassThru
    Check ($second.WaitForExit(10000)) 'A second background launch exits without a duplicate app'
    if ($BackgroundOnly) {
        Check ([ShellCheckWindow]::VisibleWindows($app.Id) -eq 0) 'The published app stays entirely in the background without showing a hub, corner or dialog'
    } else {
    $second = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    Check ($second.WaitForExit(10000)) 'An explicit second launch restores the existing app'
    # The second process exits after signaling; the first process shows on its dispatcher.
    # MainWindowHandle can still be zero or name another surface during that transition.
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $window = [ShellCheckWindow]::Shell($app.Id)
        if ($window -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    Check ($window -ne [IntPtr]::Zero -and [ShellCheckWindow]::IsWindowVisible($window)) 'The published strip is visible on an explicit launch'
    $script:surface = [Windows.Automation.AutomationElement]::FromHandle($window)
    if ($Compact) {
        $scale = [ShellCheckWindow]::GetDpiForWindow($window) / 96.0
        $transform = $surface.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern)
        $transform.Resize(340 * $scale, 560 * $scale)
        Start-Sleep -Milliseconds 250
        $compactBounds = [ShellCheckWindow+Rect]::new()
        [void][ShellCheckWindow]::GetWindowRect($window, [ref]$compactBounds)
        Check ([Math]::Abs(($compactBounds.Right - $compactBounds.Left) - 340 * $scale) -le 2 -and
            [Math]::Abs(($compactBounds.Bottom - $compactBounds.Top) - 560 * $scale) -le 2) 'The requested vertical strip uses 340 by 560 DIP'
    }
    foreach ($name in @('Find a workspace', 'Settings', 'Close Deskweave')) { $null = Control $name }
    $bounds = [ShellCheckWindow+Rect]::new()
    [void][ShellCheckWindow]::GetWindowRect($window, [ref]$bounds)
    $beforeWidth = $bounds.Right - $bounds.Left
    $report.stripBounds = [ordered]@{ width = $beforeWidth; height = $bounds.Bottom - $bounds.Top; dpi = [ShellCheckWindow]::GetDpiForWindow($window) }
    $bitmap = [Drawing.Bitmap]::new($beforeWidth, $bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $captured = [ShellCheckWindow]::PrintWindow($window, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { Check $captured 'Windows captured the published compact strip'; $bitmap.Save((Join-Path $output 'live-strip.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
    $filterQuery = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Filter workspaces by name')
    $existingFilter = $surface.FindFirst([Windows.Automation.TreeScope]::Descendants, $filterQuery)
    if ($null -eq $existingFilter -or $existingFilter.Current.IsOffscreen) { Invoke 'Find a workspace' }
    $filter = (Control 'Filter workspaces by name').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $filter.SetValue('Deskweave validation no match 739913')
    Start-Sleep -Milliseconds 200
    Check ((Control 'No matching workspaces').Current.IsOffscreen -eq $false) 'Workspace search presents an accessible empty result'
    $filter.SetValue('')
    Invoke 'Find a workspace'
    Invoke 'Settings'
    $null = Control 'General'
    Invoke 'Workspaces'
    [void][ShellCheckWindow]::GetWindowRect($window, [ref]$bounds)
    Check ([Math]::Abs(($bounds.Right - $bounds.Left) - $beforeWidth) -le 2) 'Returning from Settings preserves the strip width'
    Invoke 'Close Deskweave'
    Check (-not [ShellCheckWindow]::IsWindowVisible($window) -and -not $app.HasExited) 'Closing the strip leaves Deskweave running in the background'
    }
    foreach ($file in @('Deskweave.exe', 'Deskweave.dll', 'HiveMind.AgentWorkspaces.dll')) {
        $report[$file + 'Sha256'] = (Get-FileHash -LiteralPath (Join-Path $root ('out\' + $file))).Hash
    }
} catch { $failure = $_.ToString() }
finally {
    if (-not $BackgroundOnly) {
        $remaining = [ShellCheckWindow]::Shell($app.Id)
        if ($remaining -ne [IntPtr]::Zero) {
            [void][ShellCheckWindow]::PostMessage($remaining, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        }
    }
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'live-shell-report.json') -Encoding UTF8
}
if ($failure) { throw $failure }
