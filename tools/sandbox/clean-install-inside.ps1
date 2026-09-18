# Runs inside Windows Sandbox (Windows PowerShell 5.1) as a brand-new user: nothing installed, no
# Claude Code, no Codex, no Chrome. Installs Deskweave from the mapped installer folder, checks
# first launch, the bridge starting the app for an agent, a workspace browser with only Edge,
# then uninstalls and checks what is left. Writes everything to the mapped results folder.
$ErrorActionPreference = 'Stop'
$results = 'C:\Users\WDAGUtilityAccount\Desktop\results'
$installer = 'C:\Users\WDAGUtilityAccount\Desktop\installer\Deskweave-Setup.exe'
New-Item -ItemType Directory -Force $results | Out-Null
Start-Transcript -Path (Join-Path $results 'transcript.txt') | Out-Null
$checks = New-Object System.Collections.Generic.List[string]
$report = [ordered]@{ observedAt = (Get-Date).ToString('o'); os = [Environment]::OSVersion.VersionString; checks = $checks }
function Check([bool]$ok, [string]$claim) { if (-not $ok) { throw $claim }; $checks.Add($claim); Write-Output "PASS $claim" }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Shot([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bitmap); $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $results "$name.png")); $g.Dispose(); $bitmap.Dispose()
}
function WaitFor([scriptblock]$condition, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return $false
}
$local = $env:LOCALAPPDATA
$app = Join-Path $local 'DeskweaveApp\current\Deskweave.exe'
$bridge = Join-Path $local 'DeskweaveApp\current\Bridge\Deskweave.WorkspaceBridge.exe'
$ticket = Join-Path $local 'Deskweave\agent-workspaces.access\router.json'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskweaveApp'
$failure = $null
try {
    # --- install ---------------------------------------------------------------------------
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $setup = Start-Process -FilePath $installer -ArgumentList '--silent', '--log', (Join-Path $results 'setup.log') -PassThru
    Check ($setup.WaitForExit(300000)) 'Setup finishes within five minutes'
    $report.setupExitCode = $setup.ExitCode
    $report.installSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    Check (Test-Path $app) 'Deskweave installs per user under LocalAppData, no admin'
    Check (Test-Path $bridge) 'The agent bridge is installed beside it'
    $shortcut = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter '*.lnk' | Where-Object { $_.Name -like 'Deskweave*' })
    Check ($shortcut.Count -ge 1) 'A Start menu shortcut is created'
    Check (-not (Test-Path "$env:USERPROFILE\Desktop\Deskweave.lnk")) 'No desktop icon is added'
    Check (Test-Path $uninstallKey) 'An Apps & features uninstall entry is registered'
    $report.displayName = (Get-ItemProperty $uninstallKey).DisplayName

    # --- first launch: Setup starts the app, which asks once before touching anything -------
    $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30
    if (-not $started) { Start-Process $app; $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30 }
    Check $started 'Deskweave starts after install'
    Start-Sleep -Seconds 6
    Shot 'first-launch'
    $windows = @(Get-Process Deskweave | Where-Object { $_.MainWindowTitle } | ForEach-Object { $_.MainWindowTitle })
    $report.firstLaunchWindows = $windows
    Check ($windows.Count -ge 1) 'First launch shows a window on a fresh PC'
    Get-Process Deskweave | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }

    # --- an agent's bridge starts Deskweave in the background -------------------------------
    $psi = New-Object System.Diagnostics.ProcessStartInfo $bridge, ('--workspace "' + $ticket + '"')
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $project = Join-Path $results 'project'; New-Item -ItemType Directory -Force (Join-Path $project '.git') | Out-Null
    $psi.WorkingDirectory = $project
    $clock.Restart()
    $session = [Diagnostics.Process]::Start($psi)
    $id = 0
    function Ask([string]$method, $params, [int]$seconds = 90) {
        $script:id++
        $session.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 8))
        $session.StandardInput.Flush()
        $read = $session.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($seconds * 1000)) { throw "No answer to $method within $seconds s" }
        if ($null -eq $read.Result) { throw ('Bridge closed: ' + $session.StandardError.ReadToEnd()) }
        return ($read.Result | ConvertFrom-Json)
    }
    $hello = Ask 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'claude-code'; version = '1' } } 30
    $report.coldStartSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    Check ($hello.result.serverInfo.name -eq 'deskweave') "With Deskweave closed, an agent's bridge starts it and connects ($($report.coldStartSeconds) s)"
    $tools = Ask 'tools/list' @{}
    Check ($tools.result.tools.Count -ge 20) 'The agent sees the workspace tools'

    # --- a workspace, and its browser on a PC that only has Edge ------------------------------
    $page = Join-Path $project 'index.html'
    Set-Content -LiteralPath $page -Encoding UTF8 -Value '<!doctype html><title>Shop</title><h1>Tiny shop</h1><button id="add" onclick="this.textContent=''Added!''">Add to cart</button>'
    $browse = Ask 'tools/call' @{ name = 'browse'; arguments = @{ url = ([Uri]$page).AbsoluteUri } } 180
    $report.browse = $browse.result.content[0].text
    Check (-not $browse.result.isError) 'The workspace browser opens a local page with only Edge on the PC'
    $click = Ask 'tools/call' @{ name = 'page_click'; arguments = @{ selector = '#add' } }
    $read = Ask 'tools/call' @{ name = 'page'; arguments = @{} }
    Check ((-not $click.result.isError) -and ($read.result.content[0].text -like '*Added!*')) 'The agent clicks the page in its workspace and reads the result'
    $shot = Ask 'tools/call' @{ name = 'computer'; arguments = @{ screenshot = $true } }
    Check (@($shot.result.content | Where-Object { $_.type -eq 'image' }).Count -eq 1) 'The agent gets a screenshot of its own screen'
    Start-Sleep -Seconds 2
    Shot 'owner-screen-while-agent-works'
    $status = Ask 'tools/call' @{ name = 'status'; arguments = @{} }
    $report.workspace = ($status.result.content[0].text | ConvertFrom-Json).workspace
    Check ($report.workspace -eq 'project') 'The workspace is named after the project folder'
    $session.StandardInput.Close(); $session.WaitForExit(10000) | Out-Null
    Get-Process Deskweave -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    Get-Process msedge -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    # --- uninstall ------------------------------------------------------------------------------
    $update = Join-Path $local 'DeskweaveApp\Update.exe'
    Check (Test-Path $update) 'The uninstaller is present'
    $un = Start-Process -FilePath $update -ArgumentList '--uninstall', '--silent' -PassThru
    $un.WaitForExit(120000) | Out-Null
    if (Test-Path (Join-Path $local 'DeskweaveApp\current')) {
        $un = Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -PassThru
        $un.WaitForExit(120000) | Out-Null
    }
    Start-Sleep -Seconds 3
    Check (-not (Test-Path $app)) 'Uninstall removes the app'
    Check (-not (Test-Path $uninstallKey)) 'Uninstall removes the Apps & features entry'
    $left = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter '*.lnk' | Where-Object { $_.Name -like 'Deskweave*' })
    Check ($left.Count -eq 0) 'Uninstall removes the Start menu shortcut'
    Check (Test-Path (Join-Path $local 'Deskweave\agent-workspaces')) "Uninstall keeps the owner's workspaces and settings"
    Check ($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Deskweave) 'Nothing is left starting with Windows'
}
catch { $failure = $_.ToString() }
finally {
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'report.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
