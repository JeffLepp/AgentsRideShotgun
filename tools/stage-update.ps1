param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Verify')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [string]$CandidateDirectory,
    [string]$ReleaseDirectory,
    [string]$InstallRoot = 'C:/Users/jeffe/AppData/Local/ARSApp'
)

# Read-only release/rollback archival. Installation uses the Velopack installer or app updater. This does not build a public release,
# stop/start any process, clear the owner's quit marker, or change agent connections.
# Prepare/Verify never stop, start, install, or replace the live app.
$ErrorActionPreference = 'Stop'
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
$bundle = [IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\', '/')
$current = Join-Path $install 'current'

function Within([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}
if (Within $bundle $install) { throw 'Keep the deployment bundle outside the installed app tree.' }

function Files([string]$Root) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        $item = Get-Item -LiteralPath $directory -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $directory" }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($entry.FullName)" }
            if ($entry.PSIsContainer) { $queue.Enqueue($entry.FullName) } else { $entry }
        }
    }
}
function Snapshot([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    @(Files $Root | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($prefix.Length); bytes = $_.Length;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Assert-Snapshot([string]$Root, $Expected) {
    $actual = @(Snapshot $Root)
    if ($actual.Count -ne @($Expected).Count) { throw "File count differs: $Root" }
    for ($index = 0; $index -lt $actual.Count; $index++) {
        $left = $actual[$index]; $right = @($Expected)[$index]
        if ($left.path -cne $right.path -or $left.bytes -ne $right.bytes -or $left.sha256 -cne $right.sha256) {
            throw "Payload differs at $($left.path): $Root"
        }
    }
}
function Copy-Tree([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Destination already exists: $Destination" }
    $prefix = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Files $Source) {
        $target = Join-Path $Destination $file.FullName.Substring($prefix.Length)
        if (-not (Within $target $Destination)) { throw 'Copy target escaped destination.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
function Payload-Version([string]$Root) {
    foreach ($file in @('ARS.exe', 'ARS.dll', 'ARS.runtimeconfig.json', 'Deskweave.AgentWorkspaces.dll',
        'Deskweave.ProductContext.dll', 'coreclr.dll', 'PresentationFramework.dll',
        'Bridge/ARS.WorkspaceBridge.exe', 'Bridge/ARS.WorkspaceBridge.dll', 'Bridge/coreclr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $file) -PathType Leaf)) { throw "Incomplete payload: $file" }
    }
    return [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Root 'ARS.dll')).Version.ToString()
}
function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

if ($Mode -eq 'Prepare') {
    if (-not $CandidateDirectory -or -not $ReleaseDirectory) { throw 'Prepare needs CandidateDirectory and ReleaseDirectory.' }
    $candidate = [IO.Path]::GetFullPath($CandidateDirectory).TrimEnd('\', '/')
    $release = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
    foreach ($source in @($candidate, $release, $current)) {
        if ((Within $source $bundle) -or (Within $bundle $source) -or $source -eq $bundle) { throw 'Archive and sources must be disjoint.' }
    }
    if (Test-Path -LiteralPath $bundle) { throw 'Bundle is immutable; choose a new output directory.' }
    $oldVersion = Payload-Version $current
    $newVersion = Payload-Version $candidate
    if (-not (Test-Path -LiteralPath (Join-Path $release 'ARS-Setup.exe'))) { throw 'Velopack release installer is missing.' }
    if (-not (Get-ChildItem -LiteralPath $release -Filter '*-full.nupkg')) { throw 'Velopack full package is missing.' }
    $previousBefore = @(Snapshot $current)
    $candidateBefore = @(Snapshot $candidate)
    $releaseBefore = @(Snapshot $release)
    [IO.Directory]::CreateDirectory($bundle) | Out-Null
    Copy-Tree $current (Join-Path $bundle 'previous')
    Copy-Tree $candidate (Join-Path $bundle 'candidate')
    Copy-Tree $release (Join-Path $bundle 'release')
    Copy-Tree (Join-Path $install 'packages') (Join-Path $bundle 'previous-packages')
    Assert-Snapshot $current $previousBefore
    Assert-Snapshot $candidate $candidateBefore
    Assert-Snapshot $release $releaseBefore
    $manifest = [ordered]@{
        schemaVersion = 2; kind = 'velopack-release-with-rollback'; createdAtUtc = [DateTime]::UtcNow.ToString('o');
        installRoot = $install; previousVersion = $oldVersion; candidateVersion = $newVersion;
        previous = $previousBefore; candidate = $candidateBefore; release = $releaseBefore;
        previousPackages = @(Snapshot (Join-Path $bundle 'previous-packages'));
        policy = 'No live install or restart. Use release/ARS-Setup.exe only after saving and stopping all old workspaces/clients. Future updates use ARS --update.'
    }
    Write-Json (Join-Path $bundle 'deployment.json') $manifest
}

$manifest = Get-Content -LiteralPath (Join-Path $bundle 'deployment.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.kind -cne 'velopack-release-with-rollback' -or
    -not [string]::Equals($manifest.installRoot, $install, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest identity differs.' }
Assert-Snapshot (Join-Path $bundle 'previous') $manifest.previous
Assert-Snapshot (Join-Path $bundle 'candidate') $manifest.candidate
Assert-Snapshot (Join-Path $bundle 'release') $manifest.release
Assert-Snapshot (Join-Path $bundle 'previous-packages') $manifest.previousPackages
Write-Output "Verified complete candidate, Velopack release, previous payload, and previous package hashes: $bundle. No installed files or processes changed."
