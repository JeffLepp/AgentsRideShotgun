param([switch]$Launch, [switch]$Background, [switch]$DisconnectBridges)
$ErrorActionPreference = 'Stop'
$deskweaveRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $deskweaveRoot 'app/Deskweave.csproj'
$output = Join-Path $deskweaveRoot 'out'
function Assert-NotRunning {
    $running = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    })
    if ($running.Count -gt 0) { throw @'
Quit Deskweave from its notification-area icon before publishing.
Quitting stops every running workspace: their desktops, browsers and open apps close, and open
tabs and unsaved work in them are lost. Workspace folders, browser profiles and evidence logs are
on disk and survive. Nothing is stopped for you, so quit when you are ready.
'@ }
}
function Assert-OwnedPath([string]$path) {
    $resolved = [System.IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($deskweaveRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path is outside this Deskweave folder: $resolved"
    }
}
function Get-PublishedBridges {
    $bridgePath = Join-Path $output 'Bridge\Deskweave.WorkspaceBridge.exe'
    @(Get-Process Deskweave.WorkspaceBridge -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and
        [string]::Equals($_.Path, $bridgePath, [StringComparison]::OrdinalIgnoreCase)
    })
}
function Disconnect-PublishedBridges {
    # Explicit opt-in: a loaded self-contained runtime can prevent an atomic directory move.
    # Never swap individual runtime files under a live bridge or stop a different installation.
    Assert-NotRunning
    foreach ($bridge in @(Get-PublishedBridges)) {
        try {
            $null = $bridge.Handle # Keep the process identity open across inspection and stop.
            if ($bridge.HasExited) { continue }
            $expected = Join-Path $output 'Bridge\Deskweave.WorkspaceBridge.exe'
            if (-not [string]::Equals($bridge.MainModule.FileName, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A bridge changed identity before it could be disconnected.'
            }
            $bridge.Kill()
            if (-not $bridge.WaitForExit(10000)) { throw "Bridge $($bridge.Id) did not exit." }
            Write-Output "Disconnected Deskweave bridge $($bridge.Id). Its agent client may need to reconnect after publish."
        }
        finally { $bridge.Dispose() }
    }
}
Assert-NotRunning
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$staging = Join-Path $deskweaveRoot "artifacts/publish/$stamp"
$previous = Join-Path $deskweaveRoot "artifacts/previous/$stamp"
foreach ($path in @($staging, $output, $previous)) { Assert-OwnedPath $path }
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging -p:UseSharedCompilation=false -m:1 -nr:false --nologo
if ($LASTEXITCODE -ne 0) { throw "Deskweave publish failed ($LASTEXITCODE)." }
$required = @('Deskweave.exe', 'Deskweave.dll', 'Deskweave.runtimeconfig.json', 'HiveMind.AgentWorkspaces.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Bridge/Deskweave.WorkspaceBridge.exe', 'Bridge/Deskweave.WorkspaceBridge.runtimeconfig.json', 'Bridge/coreclr.dll')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $file))) { throw "Incomplete private build: missing $file. Existing build is unchanged." }
}
Assert-NotRunning
if (Test-Path -LiteralPath $output) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $previous)) | Out-Null
    if ($DisconnectBridges) { Disconnect-PublishedBridges }
    try { Move-Item -LiteralPath $output -Destination $previous }
    catch {
        $bridges = @(Get-PublishedBridges)
        if ($bridges.Count -gt 0) {
            throw "The current build is unchanged. Connected Deskweave bridges ($($bridges.Id -join ', ')) keep its runtime locked. Close those MCP connections, or explicitly rerun with -DisconnectBridges and reconnect the clients afterward. Staged build: $staging"
        }
        throw
    }
}
try { Move-Item -LiteralPath $staging -Destination $output }
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $output)) {
        Move-Item -LiteralPath $previous -Destination $output
    }
    throw
}
$identity = (Get-Item -LiteralPath (Join-Path $output 'Deskweave.exe')).VersionInfo
Write-Output "Deskweave $($identity.ProductVersion) is ready in $output"
if ($Launch -or $Background) {
    $start = @{ FilePath = (Join-Path $output 'Deskweave.exe'); WorkingDirectory = $output; WindowStyle = 'Hidden' }
    if ($Background) { $start.ArgumentList = @('--background') }
    Start-Process @start
}
