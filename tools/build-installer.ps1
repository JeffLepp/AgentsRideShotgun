param([string]$Version = '', [switch]$SkipPublish, [switch]$Sign)
# Builds Deskweave-Setup: a per-user install (no admin) with a Start menu shortcut and an Apps &
# features uninstall that takes Deskweave out of the agents' configs. Publishes fresh first, so
# Deskweave must be closed. -SkipPublish packages a separately published, version-matched payload.
# -Sign signs every binary and Setup with the Azure identity; public releases are always signed.
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
# Settings shows the assembly version, so it and the file version must match too.
$numeric = $Version.Split('-')[0] + '.0'
$fileVersion = (Get-Item -LiteralPath (Join-Path $payload 'Deskweave.dll')).VersionInfo.FileVersion
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'Deskweave.dll')).Version.ToString()
if ($fileVersion -cne $numeric -or $assemblyVersion -cne $numeric) { throw "Published app file version $fileVersion and assembly version $assemblyVersion should both be $numeric." }
$staging = Join-Path $root ('artifacts\installer-staging\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))

# The install id is not "Deskweave" on purpose: Velopack installs to %LOCALAPPDATA%\<id> and its
# uninstall deletes that whole folder, while the owner's workspaces live in %LOCALAPPDATA%\Deskweave.
# A technical floor, matching the oldest Windows build it was tested on.
$packageRuntime = 'win10.0.19041-x64'
$signing = @()
if ($Sign) {
    # Azure Artifact Signing through the service principal already in the environment; the
    # metadata file holds no secret and is deleted after packing.
    foreach ($name in @('AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET', 'AZURE_TENANT_ID', 'DESKWEAVE_SIGN_ACCOUNT', 'DESKWEAVE_SIGN_ENDPOINT', 'DESKWEAVE_SIGN_PROFILE')) {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Signing needs $name in the environment." }
    }
    $signMetadata = Join-Path ([IO.Path]::GetTempPath()) ('deskweave-sign-' + [Guid]::NewGuid().ToString('N') + '.json')
    $signJson = [ordered]@{
        Endpoint = $env:DESKWEAVE_SIGN_ENDPOINT; CodeSigningAccountName = $env:DESKWEAVE_SIGN_ACCOUNT; CertificateProfileName = $env:DESKWEAVE_SIGN_PROFILE
        ExcludeCredentials = @('ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzureCliCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    } | ConvertTo-Json
    # No byte-order mark: the signing library fails on one with only "SignerSign() failed".
    [IO.File]::WriteAllText($signMetadata, $signJson)
    $signing = @('--azureTrustedSignFile', $signMetadata)
}
try {
    & vpk pack --packId DeskweaveApp --packVersion $Version --packDir $payload --runtime $packageRuntime `
        --packTitle Deskweave --packAuthors 'Jefferson Kline' --mainExe Deskweave.exe `
        --icon (Join-Path $root 'app\Assets\Deskweave.ico') --shortcuts StartMenuRoot `
        --outputDir $staging --noPortable --skip-updates @signing
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
}
finally { if ($signMetadata) { Remove-Item -LiteralPath $signMetadata -ErrorAction SilentlyContinue } }
# A signing run once produced a package holding 38 of the app's files and still exited 0.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Get-ChildItem -LiteralPath $staging -Filter '*-full.nupkg' | Select-Object -First 1
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
# Compared by name: vpk adds its own few files, which would hide as many missing ones in a count.
try { $packed = [Collections.Generic.HashSet[string]]::new([string[]]@($zip.Entries | Where-Object { $_.FullName -like 'lib/app/*' -and $_.Name } | ForEach-Object { $_.FullName.Substring(8) }), [StringComparer]::OrdinalIgnoreCase) }
finally { $zip.Dispose() }
$expected = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'createdump.exe' } |
    ForEach-Object { $_.FullName.Substring($payload.Length + 1).Replace('\', '/') })
$missing = @($expected | Where-Object { -not $packed.Contains($_) })
if ($missing.Count -gt 0) { throw "The package is missing $($missing.Count) of the app's $($expected.Count) files, first: $($missing[0])" }
$built = Get-ChildItem -LiteralPath $staging -Filter '*Setup.exe' | Select-Object -First 1
if (-not $built) { throw 'vpk reported success but produced no Setup.exe.' }
# The name a person downloads. The update feed names the package, not this file.
$setup = Move-Item -LiteralPath $built.FullName -Destination (Join-Path $staging 'Deskweave-Setup.exe') -PassThru
# vpk upload reads this list, so it has to name the file that is actually there.
$assets = Join-Path $staging 'assets.win.json'
[IO.File]::WriteAllText($assets, [IO.File]::ReadAllText($assets).Replace($built.Name, $setup.Name))
if ($Sign -and (Get-AuthenticodeSignature -LiteralPath $setup.FullName).Status -ne 'Valid') { throw 'Setup is not validly signed.' }
function PackedHash([string]$entry) {
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try { $stream = $zip.GetEntry($entry).Open(); try { (Get-FileHash -InputStream $stream).Hash } finally { $stream.Dispose() } }
    finally { $zip.Dispose() }
}
$manifest = [ordered]@{
    version = $Version; runtime = $packageRuntime; velopack = $sdkVersion; builtAt = [DateTimeOffset]::UtcNow.ToString('o')
    installerSha256 = (Get-FileHash -LiteralPath $setup.FullName).Hash
    signature = [string](Get-AuthenticodeSignature -LiteralPath $setup.FullName).Status
    # From the package, not out/: signing changes the bytes that get installed.
    appSha256 = PackedHash 'lib/app/Deskweave.dll'
    engineSha256 = PackedHash 'lib/app/Deskweave.AgentWorkspaces.dll'
    bridgeSha256 = PackedHash 'lib/app/Bridge/Deskweave.WorkspaceBridge.dll'
    frameworks = (Get-Content -Raw -LiteralPath (Join-Path $payload 'Deskweave.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions.includedFrameworks
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'package-evidence.json') -Encoding UTF8
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
# A failed pack leaves only staging; it never reserves an immutable release version.
[IO.Directory]::Move($staging, $output)
$setup = Get-Item -LiteralPath (Join-Path $output 'Deskweave-Setup.exe')
Write-Output ("{0} ({1:N0} MB) SHA-256 {2}" -f $setup.FullName, ($setup.Length / 1MB), (Get-FileHash -LiteralPath $setup.FullName).Hash)
