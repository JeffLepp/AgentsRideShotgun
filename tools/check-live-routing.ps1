param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ProjectDirectory, [switch]$BrowserOnly)
# Run with PowerShell 7. This launches and closes only its own published validation instance.
# No model calls. Provider configuration is isolated; fixture workspace records are retained.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must be inside this Deskweave checkout.'
}
if (Get-Process Deskweave -ErrorAction SilentlyContinue) { throw 'Quit Deskweave before running this isolated live check.' }
$product = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Deskweave'
$settings = Join-Path $product 'settings.json'
if ((Test-Path $settings) -and (Get-Content -Raw $settings | ConvertFrom-Json).FirstRunDone) {
    throw 'This check requires unanswered first launch so startup cannot change the owner''s Start with Windows setting.'
}
function Hash([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'missing' } }
$settingsBefore = Hash $settings
$shell = Join-Path $product 'shell.json'
$shellBefore = Hash $shell
$store = Join-Path $product 'agent-workspaces'
function WorkspaceIds { @(Get-ChildItem -LiteralPath $store -Filter workspace.json -Recurse -ErrorAction SilentlyContinue | ForEach-Object { (Get-Content -Raw $_.FullName | ConvertFrom-Json).Id }) }
$before = WorkspaceIds
[IO.Directory]::CreateDirectory($output) | Out-Null
$projects = if ($ProjectDirectory) { [IO.Path]::GetFullPath($ProjectDirectory) } else { Join-Path $output 'projects' }
if (-not $projects.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture projects must be inside this Deskweave checkout.'
}
$project = Join-Path $projects 'Shop'
$trader = Join-Path $projects 'Trader'
foreach ($folder in @((Join-Path $project '.git'), (Join-Path $project 'src'), (Join-Path $trader '.git'))) {
    [IO.Directory]::CreateDirectory($folder) | Out-Null
}
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'out\Deskweave.exe'))
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add('--background')
$start.Environment['CLAUDE_CONFIG_DIR'] = Join-Path $output 'app-agents\claude'
$start.Environment['CODEX_HOME'] = Join-Path $output 'app-agents\codex'
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ observedAt = [DateTimeOffset]::UtcNow; modelCalls = 0; checks = $checks }
$failure = $null
$app = $null
function Check([bool]$ok, [string]$claim) {
    if (-not $ok) { throw $claim }
    $checks.Add($claim)
    Write-Output "PASS $claim"
}
function Run-Session([string]$label, [string]$folder, [bool]$existing, [bool]$browser = $false) {
    $args = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-live-mcp.ps1'),
        '-OutputDirectory', (Join-Path $output $label), '-ProjectFolder', $folder, '-ProviderCli')
    if ($existing) { $args += '-RequireExisting' }
    if ($browser) { $args += '-BrowserFixture' }
    & pwsh @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Live MCP check failed: $label" }
    Get-Content -Raw (Join-Path $output "$label\live-mcp-report.json") | ConvertFrom-Json
}
try {
    $app = [Diagnostics.Process]::Start($start)
    $report.pid = $app.Id
    Start-Sleep -Seconds 2
    Check (-not $app.HasExited) 'The published app starts in the background'
    $report.exeSha256 = Hash (Join-Path $root 'out\Deskweave.exe')
    $report.engineSha256 = Hash (Join-Path $root 'out\HiveMind.AgentWorkspaces.dll')
    $report.bridgeSha256 = Hash (Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.dll')
    if ($BrowserOnly) {
        $browser = Run-Session 'browser' $project $false $true
        Check ($browser.sessions.Count -eq 2 -and @($browser.sessions | Where-Object { -not $_.browserVerified }).Count -eq 0) 'Both configured providers can open, click and observe a real local browser page'
        $report.workspaces = [ordered]@{ browser = $browser.workspace.id }
    }
    else {
    $nested = Run-Session 'project-nested' (Join-Path $project 'src') $false
    $same = Run-Session 'project-root' $project $true
    Check ($nested.workspace.id -eq $same.workspace.id) 'Nested and root sessions from both providers reuse the same project workspace'
    $other = Run-Session 'other-project' $trader $false
    Check ($other.workspace.id -ne $same.workspace.id) 'A different project gets a separate workspace'
    $scratch = Run-Session 'scratch-home' ([Environment]::GetFolderPath('UserProfile')) $false
    $desktop = Run-Session 'scratch-desktop' ([Environment]::GetFolderPath('DesktopDirectory')) $true
    Check ($scratch.workspace.id -eq $desktop.workspace.id -and $scratch.workspace.id -ne $same.workspace.id) 'Home and Desktop sessions share Scratch without entering a project'
    $report.workspaces = [ordered]@{ project = $same.workspace.id; other = $other.workspace.id; scratch = $scratch.workspace.id }
    }
    Check ((Hash $settings) -eq $settingsBefore -and (Hash $shell) -eq $shellBefore) 'Owner settings and shell preferences are unchanged'
}
catch { $failure = $_.ToString() }
finally {
    # This instance inherited fixture provider roots and must not be left for the owner to consent in.
    if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit(10000) | Out-Null }
    $report.validationInstanceExited = -not $app -or $app.HasExited
    $report.createdWorkspaceIds = @(WorkspaceIds | Where-Object { $_ -notin $before })
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'live-routing-report.json') -Encoding utf8
}
if ($failure) { throw $failure }
