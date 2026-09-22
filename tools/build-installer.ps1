param([string]$Version = '', [switch]$SkipPublish)
# Builds Deskweave-Setup: a per-user install (no admin) with a Start menu shortcut and an Apps &
# features uninstall that takes Deskweave out of the agents' configs. Publishes fresh first, so
# Deskweave must be closed. -SkipPublish packages a separately published, version-matched payload.
# Public distribution requires a signing identity and separate SmartScreen/Smart App Control checks.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceVersion = [string]([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup[0].Version
if (-not $Version) { $Version = $sourceVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Use a three-part version, optionally followed by a prerelease label.' }
if ($Version -cne $sourceVersion) { throw "Package version $Version differs from source version $sourceVersion. Update Directory.Build.props first." }
$sdkVersion = ([xml](Get-Content -Raw (Join-Path $root 'app\Deskweave.csproj'))).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Velopack' } | Select-Object -ExpandProperty Version
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw "The Velopack CLI is missing. Run: dotnet tool install -g vpk --version $sdkVersion" }
$cliHelp = & vpk -h | Out-String
if ($LASTEXITCODE -ne 0 -or $cliHelp -notmatch ('Velopack CLI ' + [regex]::Escape($sdkVersion) + ',')) {
    throw "The Velopack CLI must match the app SDK ($sdkVersion). Run: dotnet tool update -g vpk --version $sdkVersion"
}
$output = Join-Path $root "artifacts\installer\$Version"
if (Test-Path -LiteralPath $output) { throw "An installer for $Version already exists at $output. Released versions are immutable; bump the version." }

if (-not $SkipPublish) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no installer was built.' }
}
$payload = Join-Path $root 'out'
foreach ($file in @('Deskweave.exe', 'Deskweave.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Bridge\Deskweave.WorkspaceBridge.exe', 'Bridge\coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Incomplete published app: $file" }
}
$payloadVersion = (Get-Item -LiteralPath (Join-Path $payload 'Deskweave.dll')).VersionInfo.ProductVersion.Split('+')[0]
if ($payloadVersion -cne $Version) { throw "Published app version $payloadVersion differs from package version $Version. Publish this version first." }
$staging = Join-Path $root ('artifacts\installer-staging\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))

# The install id is not "Deskweave" on purpose: Velopack installs to %LOCALAPPDATA%\<id> and its
# uninstall deletes that whole folder, while the owner's workspaces live in %LOCALAPPDATA%\Deskweave.
# A technical floor for this private candidate, matching its oldest observed test guest.
# Edition/lifecycle support is narrower and is stated in LAUNCH.md; a build number cannot encode it.
$packageRuntime = 'win10.0.19041-x64'
& vpk pack --packId DeskweaveApp --packVersion $Version --packDir $payload --runtime $packageRuntime `
    --packTitle Deskweave --packAuthors 'Jefferson Kline' --mainExe Deskweave.exe `
    --icon (Join-Path $root 'app\Assets\Deskweave.ico') --shortcuts StartMenuRoot `
    --outputDir $staging --noPortable --skip-updates
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
$built = Get-ChildItem -LiteralPath $staging -Filter '*Setup.exe' | Select-Object -First 1
if (-not $built) { throw 'vpk reported success but produced no Setup.exe.' }
# The name a person downloads. The update feed names the package, not this file.
$setup = Move-Item -LiteralPath $built.FullName -Destination (Join-Path $staging 'Deskweave-Setup.exe') -PassThru
$manifest = [ordered]@{
    version = $Version; runtime = $packageRuntime; velopack = $sdkVersion; builtAt = [DateTimeOffset]::UtcNow.ToString('o')
    installerSha256 = (Get-FileHash -LiteralPath $setup.FullName).Hash
    signature = [string](Get-AuthenticodeSignature -LiteralPath $setup.FullName).Status
    appSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'Deskweave.dll')).Hash
    engineSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'Deskweave.AgentWorkspaces.dll')).Hash
    bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'Bridge\Deskweave.WorkspaceBridge.dll')).Hash
    frameworks = (Get-Content -Raw -LiteralPath (Join-Path $payload 'Deskweave.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions.includedFrameworks
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'package-evidence.json') -Encoding UTF8
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
# A failed pack leaves only staging; it never reserves an immutable release version.
[IO.Directory]::Move($staging, $output)
$setup = Get-Item -LiteralPath (Join-Path $output 'Deskweave-Setup.exe')
Write-Output ("{0} ({1:N0} MB) SHA-256 {2}" -f $setup.FullName, ($setup.Length / 1MB), (Get-FileHash -LiteralPath $setup.FullName).Hash)
