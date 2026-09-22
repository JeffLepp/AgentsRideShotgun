param(
    [string]$InstallerPath = 'C:\Users\WDAGUtilityAccount\Desktop\installer\Deskweave-Setup.exe',
    [string]$OutputDirectory = 'C:\Users\WDAGUtilityAccount\Desktop\results',
    [switch]$VirtualMachine,
    [switch]$PreserveExistingCodexConfiguration
)
# Runs inside Windows Sandbox (Windows PowerShell 5.1) as a brand-new user: nothing installed, no
# Claude Code, no Codex, no Chrome. Installs Deskweave from the mapped installer folder, checks
# first launch, the bridge starting the app for an agent, a workspace browser with only Edge,
# then uninstalls and checks what is left. Writes everything to the mapped results folder.
# VM mode also supports a fresh Deskweave install in an existing Windows profile;
# opt-in Codex preservation compares config hashes without reading credentials.
$ErrorActionPreference = 'Stop'
if ($VirtualMachine) {
    $machine = Get-CimInstance Win32_ComputerSystem
    if ($machine.Model -notmatch 'VirtualBox|Virtual Machine|VMware') { throw 'The VM check can only install inside a virtual machine.' }
    foreach ($fixturePath in @('.claude.json', '.local\bin\claude.exe')) {
        if (Test-Path -LiteralPath (Join-Path $env:USERPROFILE $fixturePath)) { throw 'Existing Claude configuration/executable would conflict with the uninstall fixture.' }
    }
}
elseif ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run this script inside Windows Sandbox, never on the host.' }
if ($PreserveExistingCodexConfiguration -and -not $VirtualMachine) { throw 'Existing-provider preservation is only for the VM gate.' }
$codexConfiguration = Join-Path $env:USERPROFILE '.codex\config.toml'
$codexConfigurationBefore = if (Test-Path -LiteralPath $codexConfiguration) { (Get-FileHash -LiteralPath $codexConfiguration).Hash } else { $null }
if ($codexConfigurationBefore -and -not $PreserveExistingCodexConfiguration) { throw 'This gate requires a fresh Codex configuration or explicit VM preservation mode.' }
$results = [IO.Path]::GetFullPath($OutputDirectory)
$installer = [IO.Path]::GetFullPath($InstallerPath)
$previousInstaller = Join-Path (Split-Path -Parent $installer) 'Deskweave-Previous-Setup.exe'
$initialInstaller = if (Test-Path -LiteralPath $previousInstaller) { $previousInstaller } else { $installer }
New-Item -ItemType Directory -Force $results | Out-Null
Start-Transcript -Path (Join-Path $results 'transcript.txt') | Out-Null
$checks = New-Object System.Collections.Generic.List[string]
$report = [ordered]@{ observedAt = (Get-Date).ToString('o'); os = [Environment]::OSVersion.VersionString; checks = $checks; installerSha256 = (Get-FileHash -LiteralPath $installer).Hash }
$windowsVersion = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$report.windows = [ordered]@{
    productName = $windowsVersion.ProductName
    caption = (Get-CimInstance Win32_OperatingSystem).Caption
    displayVersion = $windowsVersion.DisplayVersion
    build = $windowsVersion.CurrentBuildNumber
    revision = $windowsVersion.UBR
    architecture = (Get-CimInstance Win32_OperatingSystem).OSArchitecture
}
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
$project = Join-Path $results 'project'
New-Item -ItemType Directory -Force (Join-Path $project '.git') | Out-Null
$session = $null
$id = 0
function StartBridge {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $bridge, ('--workspace "' + $ticket + '"')
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $project
    return [Diagnostics.Process]::Start($psi)
}
function Ask([string]$method, $params, [int]$seconds = 90) {
    $script:id++
    $session.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 8))
    $session.StandardInput.Flush()
    $read = $session.StandardOutput.ReadLineAsync()
    if (-not $read.Wait($seconds * 1000)) { throw "No answer to $method within $seconds s" }
    if ($null -eq $read.Result) { throw ('Bridge closed: ' + $session.StandardError.ReadToEnd()) }
    $answer = $read.Result | ConvertFrom-Json
    if ($answer.id -ne $script:id -or $answer.error) { throw ("Invalid MCP response to ${method}: " + $read.Result) }
    return $answer
}
$failure = $null
try {
    Check (-not (Test-Path $app)) 'This user has no existing Deskweave installation'
    Check (-not (Test-Path (Join-Path $local 'Deskweave'))) 'This user has no existing Deskweave data'
    # --- install ---------------------------------------------------------------------------
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $setup = Start-Process -FilePath $initialInstaller -ArgumentList ('--silent --log "' + (Join-Path $results 'setup.log') + '"') -PassThru -WindowStyle Hidden
    Check ($setup.WaitForExit(300000)) 'Setup finishes within five minutes'
    $report.setupExitCode = $setup.ExitCode
    Check ($setup.ExitCode -eq 0) 'Setup returns success'
    $report.installSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    Check (Test-Path $app) 'Deskweave installs per user under LocalAppData, no admin'
    Check (Test-Path $bridge) 'The agent bridge is installed beside it'
    $report.appVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    $report.appSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'Deskweave.dll')).Hash
    $report.engineSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'Deskweave.AgentWorkspaces.dll')).Hash
    $report.bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $bridge) 'Deskweave.WorkspaceBridge.dll')).Hash
    $shortcut = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter '*.lnk' | Where-Object { $_.Name -like 'Deskweave*' })
    Check ($shortcut.Count -ge 1) 'A Start menu shortcut is created'
    Check (-not (Test-Path "$env:USERPROFILE\Desktop\Deskweave.lnk")) 'No desktop icon is added'
    Check (Test-Path $uninstallKey) 'An Apps & features uninstall entry is registered'
    $report.displayName = (Get-ItemProperty $uninstallKey).DisplayName

    # --- first launch: Setup starts the app, which asks once before touching anything -------
    $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30
    if (-not $started) { Start-Process $app -WindowStyle Hidden; $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30 }
    Check $started 'Deskweave starts after install'
    $clock.Restart()
    $firstWindowReady = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle }).Count -gt 0 } 60
    $report.firstWindowWaitSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    Shot 'first-launch'
    $windows = @(Get-Process Deskweave | Where-Object { $_.MainWindowTitle } | ForEach-Object { $_.MainWindowTitle })
    $report.firstLaunchWindows = $windows
    Check ($firstWindowReady -and $windows.Count -ge 1) 'First launch shows a window on a fresh PC within 60 seconds'
    Check (-not (Test-Path (Join-Path $env:USERPROFILE '.claude.json'))) 'Unanswered first launch creates no Claude configuration'
    if ($codexConfigurationBefore) {
        Check ((Test-Path -LiteralPath $codexConfiguration) -and (Get-FileHash -LiteralPath $codexConfiguration).Hash -eq $codexConfigurationBefore) 'Unanswered first launch preserves the existing Codex configuration byte for byte'
    }
    else { Check (-not (Test-Path -LiteralPath $codexConfiguration)) 'Unanswered first launch creates no Codex configuration' }
    Get-Process Deskweave | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }

    if ($initialInstaller -ne $installer) {
        $report.previousVersion = $report.appVersion
        $report.previousInstallerSha256 = (Get-FileHash -LiteralPath $initialInstaller).Hash
        $upgradeMarker = Join-Path $local 'Deskweave\upgrade-retained.txt'
        Set-Content -LiteralPath $upgradeMarker -Value 'saved before upgrade' -Encoding UTF8
        $session = StartBridge
        $beforeUpgrade = Ask 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'install-upgrade-check'; version = '1' } } 30
        Check ($beforeUpgrade.result.serverInfo.name -eq 'deskweave') 'An agent bridge is connected before the upgrade'
        $report.upgradeBridge = [ordered]@{ processId = $session.Id; executable = $bridge; connectedBefore = $true }
        $upgrade = Start-Process -FilePath $installer -ArgumentList ('--silent --log "' + (Join-Path $results 'upgrade.log') + '"') -PassThru -WindowStyle Hidden
        Check ($upgrade.WaitForExit(300000) -and $upgrade.ExitCode -eq 0) 'The new Setup upgrades the previous installed release'
        Start-Sleep -Seconds 2
        $report.upgradeBridge.exitedDuringUpgrade = $session.HasExited
        if ($session.HasExited) {
            $report.upgradeBridge.exitCode = $session.ExitCode
            $report.upgradeBridge.reconnectRequired = $true
            $report.upgradeBridge.stderr = $session.StandardError.ReadToEnd()
        }
        else {
            $afterUpgrade = Ask 'tools/list' @{} 30
            Check ($afterUpgrade.result.tools.Count -ge 20) 'A surviving bridge can answer requests after the upgrade'
            $report.upgradeBridge.reconnectRequired = $false
            $session.StandardInput.Close()
            Check ($session.WaitForExit(10000) -and $session.ExitCode -eq 0) 'The surviving upgrade bridge closes cleanly'
        }
        $session.Dispose(); $session = $null
        Check ((Get-Content -Raw -LiteralPath $upgradeMarker).Trim() -eq 'saved before upgrade') 'An upgrade preserves existing Deskweave data'
        $report.appVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
        Check ($report.appVersion -ne $report.previousVersion) 'The installed version changes after upgrade'
        $report.appSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'Deskweave.dll')).Hash
        $report.engineSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'Deskweave.AgentWorkspaces.dll')).Hash
        $report.bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $bridge) 'Deskweave.WorkspaceBridge.dll')).Hash
        Start-Sleep -Seconds 3
        Get-Process Deskweave -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    }

    # --- an agent's bridge starts Deskweave in the background -------------------------------
    $clock.Restart()
    $session = StartBridge
    $hello = Ask 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'claude-code'; version = '1' } } 30
    $report.coldStartSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    Check ($hello.result.serverInfo.name -eq 'deskweave') "With Deskweave closed, an agent's bridge starts it and connects ($($report.coldStartSeconds) s)"
    if ($initialInstaller -ne $installer) { $report.upgradeBridge.newClientConnected = $true }
    $tools = Ask 'tools/list' @{}
    $names = @($tools.result.tools | ForEach-Object { $_.name })
    Check (@('browse', 'computer', 'look', 'run', 'open', 'window', 'release' | Where-Object { $names -notcontains $_ }).Count -eq 0) 'The agent sees the workspace tools'

    # --- a workspace, and its browser on a PC that only has Edge ------------------------------
    $page = Join-Path $project 'index.html'
    Set-Content -LiteralPath $page -Encoding UTF8 -Value '<!doctype html><title>Shop</title><h1>Tiny shop</h1><button id="add" onclick="this.textContent=''Added!''">Add to cart</button>'
    $browse = Ask 'tools/call' @{ name = 'browse'; arguments = @{ url = ([Uri]$page).AbsoluteUri } } 180
    $report.browse = $browse.result.content[0].text
    Check (-not $browse.result.isError) 'The workspace browser opens a local page with only Edge on the PC'
    $click = Ask 'tools/call' @{ name = 'page'; arguments = @{ action = 'click'; selector = '#add' } }
    $read = Ask 'tools/call' @{ name = 'page'; arguments = @{} }
    Check ((-not $click.result.isError) -and ($read.result.content[0].text -like '*Added!*')) 'The agent clicks the page in its workspace and reads the result'
    $shot = Ask 'tools/call' @{ name = 'computer'; arguments = @{ screenshot = $true } }
    Check (@($shot.result.content | Where-Object { $_.type -eq 'image' }).Count -eq 1) 'The agent gets a screenshot of its own screen'
    Start-Sleep -Seconds 2
    Shot 'owner-screen-while-agent-works'
    $status = Ask 'tools/call' @{ name = 'status'; arguments = @{} }
    $report.workspace = ($status.result.content[0].text | ConvertFrom-Json).workspace
    Check ($report.workspace -eq 'project') 'The workspace is named after the project folder'
    $session.StandardInput.Close(); Check ($session.WaitForExit(10000) -and $session.ExitCode -eq 0) 'The installed bridge exits cleanly when its agent disconnects'
    Get-Process Deskweave -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    # The desktop owns browser children; killing the host app must release them too.
    Check (WaitFor { @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like '*Deskweave*' }).Count -eq 0 } 15) 'Closing Deskweave releases its workspace browser processes'

    # --- uninstall ------------------------------------------------------------------------------
    $update = Join-Path $local 'DeskweaveApp\Update.exe'
    Check (Test-Path $update) 'The uninstaller is present'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name Deskweave -Value ('"' + $app + '" --background') -PropertyType String -Force | Out-Null
    $retained = Join-Path $local 'Deskweave\agent-workspaces\install-test-retained.txt'
    Set-Content -LiteralPath $retained -Value 'saved work survives uninstall' -Encoding UTF8
    # A provider-shaped local fixture verifies that the real uninstall hook calls the provider
    # and leaves unrelated configuration alone. No provider account or model is involved.
    $fixtureBin = Join-Path $env:USERPROFILE '.local\bin'
    New-Item -ItemType Directory -Force $fixtureBin | Out-Null
    $providerConfig = Join-Path $env:USERPROFILE '.claude.json'
    $providerFixture = @{ retainedPreference = 'keep'; mcpServers = @{
        deskweave = @{ type = 'stdio'; command = $bridge; args = @('--workspace', $ticket) }
        another_server = @{ command = 'unrelated.exe' }
    } } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($providerConfig, $providerFixture, (New-Object Text.UTF8Encoding $false))
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
public static class InstallerProviderFixture {
    public static int Main(string[] args) {
        if (string.Join(" ", args) != "mcp remove --scope user deskweave") return 2;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        var serializer = new JavaScriptSerializer();
        var config = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        ((Dictionary<string, object>)config["mcpServers"]).Remove("deskweave");
        File.WriteAllText(path, serializer.Serialize(config));
        return 0;
    }
}
'@ -ReferencedAssemblies 'System.Web.Extensions.dll' -OutputAssembly (Join-Path $fixtureBin 'claude.exe') -OutputType ConsoleApplication
    $un = Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -PassThru -WindowStyle Hidden
    Check ($un.WaitForExit(120000)) 'Uninstall finishes within two minutes'
    $report.uninstallExitCode = $un.ExitCode
    Check ($un.ExitCode -eq 0) 'Uninstall returns success'
    Start-Sleep -Seconds 3
    Check (-not (Test-Path $app)) 'Uninstall removes the app'
    Check (-not (Test-Path $uninstallKey)) 'Uninstall removes the Apps & features entry'
    $left = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter '*.lnk' | Where-Object { $_.Name -like 'Deskweave*' })
    Check ($left.Count -eq 0) 'Uninstall removes the Start menu shortcut'
    Check (Test-Path (Join-Path $local 'Deskweave\agent-workspaces')) "Uninstall keeps the owner's workspaces and settings"
    Check ((Get-Content -Raw -LiteralPath $retained).Trim() -eq 'saved work survives uninstall') 'Uninstall preserves saved workspace file contents'
    $providerAfter = Get-Content -Raw -LiteralPath $providerConfig | ConvertFrom-Json
    Check ($null -eq $providerAfter.mcpServers.deskweave) 'Uninstall removes its owned MCP entry through the provider command'
    Check ($providerAfter.retainedPreference -eq 'keep' -and $providerAfter.mcpServers.another_server.command -eq 'unrelated.exe') 'Uninstall preserves unrelated provider settings and MCP entries'
    Check ($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Deskweave) 'Nothing is left starting with Windows'
    if ($codexConfigurationBefore) {
        Check ((Test-Path -LiteralPath $codexConfiguration) -and (Get-FileHash -LiteralPath $codexConfiguration).Hash -eq $codexConfigurationBefore) 'The complete install, workspace and uninstall sequence preserves the existing Codex configuration byte for byte'
    }

    # --- reinstall, then Settings > Uninstall Deskweave --------------------------------------
    # The same steps the Settings row takes: quit, delete both data folders, run Update.exe.
    $setup = Start-Process -FilePath $installer -ArgumentList '--silent' -PassThru -WindowStyle Hidden
    Check ($setup.WaitForExit(300000) -and $setup.ExitCode -eq 0) 'Reinstalling over kept data succeeds'
    $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30
    if (-not $started) { Start-Process $app -WindowStyle Hidden; $started = WaitFor { @(Get-Process Deskweave -ErrorAction SilentlyContinue).Count -gt 0 } 30 }
    Check $started 'Deskweave starts after the reinstall'
    [IO.File]::WriteAllText($providerConfig, $providerFixture, (New-Object Text.UTF8Encoding $false))
    Get-Process Deskweave -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    foreach ($folder in @((Join-Path $local 'Deskweave'), (Join-Path $env:APPDATA 'Deskweave'))) {
        if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    }
    $un = Start-Process -FilePath $update -ArgumentList 'uninstall', '--silent' -PassThru -WindowStyle Hidden
    Check ($un.WaitForExit(120000) -and $un.ExitCode -eq 0) 'Full uninstall returns success'
    Start-Sleep -Seconds 5
    $leftovers = @(
        @($local, $env:APPDATA, $env:TEMP, "$env:APPDATA\Microsoft\Windows\Start Menu\Programs", "$env:USERPROFILE\Desktop") |
            Where-Object { Test-Path $_ } |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Deskweave*' } } |
            ForEach-Object { $_.FullName })
    $report.fullUninstallLeftovers = $leftovers
    Shot 'after-full-uninstall'
    Check ($leftovers.Count -eq 0) ('Full uninstall leaves no Deskweave files or folders' + $(if ($leftovers) { ': ' + ($leftovers -join ', ') }))
    Check (-not (Test-Path $uninstallKey)) 'Full uninstall removes the Apps & features entry'
    Check ($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Deskweave) 'Full uninstall leaves nothing starting with Windows'
    Check ($null -eq (Get-Content -Raw -LiteralPath $providerConfig | ConvertFrom-Json).mcpServers.deskweave) 'Full uninstall disconnects the agent'
    Check (@(Get-Process Deskweave, Deskweave.WorkspaceBridge -ErrorAction SilentlyContinue).Count -eq 0) 'No Deskweave process is left running'
}
catch { $failure = $_.ToString() }
finally {
    if ($null -ne $session) {
        try { if (-not $session.HasExited) { $session.Kill(); $session.WaitForExit(10000) | Out-Null } } catch { }
        $session.Dispose()
    }
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'report.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if ($failure) { exit 1 }
