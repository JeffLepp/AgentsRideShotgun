param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$deskweaveRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $deskweaveRoot 'app/Deskweave.csproj'
$output = Join-Path $deskweaveRoot 'out'
function Assert-NotRunning {
    $running = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    })
    if ($running.Count -gt 0) { throw 'Quit Deskweave from its notification-area icon before publishing. Running work has not been stopped.' }
}
function Assert-OwnedPath([string]$path) {
    $resolved = [System.IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($deskweaveRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path is outside this Deskweave folder: $resolved"
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
    Move-Item -LiteralPath $output -Destination $previous
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
if ($Launch) { Start-Process -FilePath (Join-Path $output 'Deskweave.exe') -WorkingDirectory $output -WindowStyle Hidden }
