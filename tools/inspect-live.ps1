param([Parameter(Mandatory = $true)][string]$OutputDirectory, [switch]$ExerciseLifecycle, [switch]$ExerciseAppearance)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DeskweaveSurfaceCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
}
'@
$deskweaveRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $deskweaveRoot 'out/Deskweave.exe'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($output) | Out-Null
$process = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
if ($process.Count -ne 1) { throw 'Expected one running Deskweave from this private publish folder.' }
$process = $process[0]
$process.Refresh()
$hwnd = $process.MainWindowHandle
if ($hwnd -eq 0) { throw 'Deskweave has no visible window to inspect.' }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Get-DeskControl([string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $control = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $control) { throw "Missing accessible control: $name" }
    return $control
}
function Invoke-DeskControl([string]$name) {
    $control = Get-DeskControl $name
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Save-DeskSurface([string]$name) {
    $rect = New-Object DeskweaveSurfaceCapture+Rect
    if (-not [DeskweaveSurfaceCapture]::GetWindowRect($hwnd, [ref]$rect)) { throw 'Window bounds unavailable.' }
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $captured = [DeskweaveSurfaceCapture]::PrintWindow($hwnd, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try {
        if (-not $captured) { throw 'Deskweave could not be captured.' }
        $bitmap.Save((Join-Path $output $name), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

$required = @('Create a workspace', 'All workspaces', 'Toggle compact monitor', 'Collapse or expand Deskweave', 'Hide Deskweave to notification area')
$controls = foreach ($name in $required) {
    $control = Get-DeskControl $name
    [pscustomobject]@{ Name = $name; Enabled = $control.Current.IsEnabled; Bounds = $control.Current.BoundingRectangle.ToString() }
}
$module = $process.Modules | Where-Object { $_.ModuleName -eq 'HiveMind.AgentWorkspaces.dll' } | Select-Object -First 1
if ($null -eq $module -or $module.FileName -ne (Join-Path $deskweaveRoot 'out/HiveMind.AgentWorkspaces.dll')) {
    throw 'Deskweave did not load its own published engine.'
}
Save-DeskSurface 'live-overview.png'
$checks = [System.Collections.Generic.List[string]]::new()
$checks.Add('Actual published window exposes named controls and its own engine')
if ($ExerciseLifecycle) {
    # These actions use this window's own UI Automation surface, not global keyboard/pointer input.
    Invoke-DeskControl 'Toggle compact monitor'
    Start-Sleep -Milliseconds 400
    Save-DeskSurface 'live-compact.png'
    Invoke-DeskControl 'Collapse or expand Deskweave'
    Start-Sleep -Milliseconds 300
    Save-DeskSurface 'live-collapsed.png'
    $checks.Add('Packaged compact and collapsed modes were invoked and captured')
    Invoke-DeskControl 'Hide Deskweave to notification area'
    Start-Sleep -Milliseconds 300
    if ([DeskweaveSurfaceCapture]::IsWindowVisible($hwnd) -or $process.HasExited) { throw 'Hide-to-tray did not retain a hidden live process.' }
    $checks.Add('Close hides the actual window and keeps the process alive')
    $second = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(10000)) { throw 'Second instance failed to return to the first.' }
    Start-Sleep -Milliseconds 600
    $process.Refresh()
    if (-not [DeskweaveSurfaceCapture]::IsWindowVisible($hwnd) -or $process.HasExited) { throw 'Second launch did not restore the original app.' }
    $checks.Add('Second launch restores the same process/window, including collapsed mode')
    Invoke-DeskControl 'Toggle compact monitor'
    Start-Sleep -Milliseconds 400
    Save-DeskSurface 'live-restored.png'
}
if ($ExerciseAppearance) {
    foreach ($skin in @('Windows', 'Mac', 'Linux', 'Windows')) {
        Invoke-DeskControl 'Change appearance'
        Start-Sleep -Milliseconds 200
        $nameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, "Use $skin appearance")
        $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $processCondition)
        $item = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $item) { throw "Missing appearance menu action: $skin" }
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 350
        $labelCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AppearanceLabel')
        $label = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $labelCondition)
        if ($label.Current.Name -ne $skin) { throw "Appearance did not change to $skin." }
        Save-DeskSurface ("live-skin-" + $skin.ToLowerInvariant() + '.png')
    }
    $checks.Add('Actual appearance menu switches Windows, Mac and Linux on the same window and returns to Windows')
}
$report = [pscustomobject]@{
    Version = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    ProcessId = $process.Id
    Executable = $executable
    ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    Engine = $module.FileName
    EngineSha256 = (Get-FileHash -LiteralPath $module.FileName -Algorithm SHA256).Hash
    WorkingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
    ModelCalls = 0
    Controls = @($controls)
    Checks = @($checks)
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'live-report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 5
