param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^JeffLepp/[A-Za-z0-9_.-]+$')][string]$ReleaseRepository,
    [string]$ReleaseTag = '',
    [string]$OutputRoot = ''
)

# Run after a signed, immutable Setup has been uploaded to a public GitHub Release.
# The downloaded bytes must match the locally tested package before a manifest is emitted.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageDir = Join-Path $root "artifacts/installer/$Version"
$installer = Join-Path $packageDir 'Deskweave-Setup.exe'
$evidencePath = Join-Path $packageDir 'package-evidence.json'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing installer: $installer" }
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw "Missing package evidence: $evidencePath" }
$evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
if ($evidence.version -cne $Version) { throw 'Package evidence version does not match the requested version.' }
if ($evidence.signature -cne 'Valid') { throw 'The package evidence does not record a valid signature.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -notmatch '^CN=Jefferson Kline(?:,|$)') {
    throw 'The installer must have a valid Jefferson Kline signature.'
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToUpperInvariant()
if ($hash -cne ([string]$evidence.installerSha256).ToUpperInvariant()) {
    throw 'The installer no longer matches its package evidence.'
}

if (-not $ReleaseTag) { $ReleaseTag = "v$Version" }
if ($ReleaseTag -notmatch '^[A-Za-z0-9._-]+$') { throw 'ReleaseTag must be a plain GitHub tag.' }
$packageUrl = "https://github.com/$ReleaseRepository"
$installerUrl = "$packageUrl/releases/download/$ReleaseTag/Deskweave-Setup.exe"
$download = Join-Path ([IO.Path]::GetTempPath()) ("deskweave-winget-$([Guid]::NewGuid().ToString('N')).exe")
try {
    & curl.exe --silent --show-error --fail --location --retry 3 --proto '=https' --proto-redir '=https' --output $download $installerUrl
    if ($LASTEXITCODE -ne 0) { throw "Public installer download failed (curl exit $LASTEXITCODE)." }
    $downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($downloadHash -cne $hash) {
        throw "The public download hash differs from the tested installer. Local: $hash Download: $downloadHash"
    }
}
finally { Remove-Item -LiteralPath $download -ErrorAction SilentlyContinue }

if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'artifacts/winget' }
$manifestDir = Join-Path $OutputRoot "manifests/j/JeffLepp/Deskweave/$Version"
if (Test-Path -LiteralPath $manifestDir) { throw "Manifest already exists: $manifestDir" }
[IO.Directory]::CreateDirectory($manifestDir) | Out-Null

$versionManifest = @'
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
PackageIdentifier: JeffLepp.Deskweave
PackageVersion: __VERSION__
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
'@
$installerManifest = @'
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
PackageIdentifier: JeffLepp.Deskweave
PackageVersion: __VERSION__
InstallerType: exe
Scope: user
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: __URL__
    InstallerSha256: __HASH__
ManifestType: installer
ManifestVersion: 1.12.0
'@
$localeManifest = @'
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
PackageIdentifier: JeffLepp.Deskweave
PackageVersion: __VERSION__
PackageLocale: en-US
Publisher: Jefferson Kline
PublisherUrl: https://github.com/JeffLepp
PackageName: Deskweave
PackageUrl: __PACKAGE_URL__
License: MIT
ShortDescription: Desktop workspaces for connected AI agents.
Moniker: deskweave
Tags:
  - ai
  - desktop
  - mcp
ManifestType: defaultLocale
ManifestVersion: 1.12.0
'@
$versionManifest = $versionManifest.Replace('__VERSION__', $Version)
$installerManifest = $installerManifest.Replace('__VERSION__', $Version).Replace('__URL__', $installerUrl).Replace('__HASH__', $hash)
$localeManifest = $localeManifest.Replace('__VERSION__', $Version).Replace('__PACKAGE_URL__', $packageUrl)
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $manifestDir 'JeffLepp.Deskweave.yaml'), $versionManifest + [Environment]::NewLine, $utf8)
[IO.File]::WriteAllText((Join-Path $manifestDir 'JeffLepp.Deskweave.installer.yaml'), $installerManifest + [Environment]::NewLine, $utf8)
[IO.File]::WriteAllText((Join-Path $manifestDir 'JeffLepp.Deskweave.locale.en-US.yaml'), $localeManifest + [Environment]::NewLine, $utf8)
$oneLineTemplate = '$u=''__URL__'';$h=''__HASH__'';$p=Join-Path $env:TEMP (''Deskweave-''+[guid]::NewGuid().ToString(''N'')+''.exe'');try{& curl.exe --silent --show-error --fail --location --retry 3 --proto ''=https'' --proto-redir ''=https'' --output $p $u;if($LASTEXITCODE){throw ''Download failed''};$s=Get-AuthenticodeSignature -LiteralPath $p;if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -cne $h -or $s.Status -ne ''Valid'' -or $s.SignerCertificate.Subject -notmatch ''^CN=Jefferson Kline(?:,|$)''){throw ''Verification failed''};Set-Content -LiteralPath $p -Stream Zone.Identifier -Encoding Ascii -Value ''[ZoneTransfer]'',''ZoneId=3'',(''HostUrl='' + $u);$r=Start-Process -FilePath $p -ArgumentList ''--silent'' -PassThru -Wait;if($r.ExitCode){throw (''Installer exited ''+$r.ExitCode)}}finally{Remove-Item -LiteralPath $p -ErrorAction SilentlyContinue}'
$oneLine = $oneLineTemplate.Replace('__URL__', $installerUrl).Replace('__HASH__', $hash)
Write-Output 'PowerShell install command (download verified; clean install still required):'
Write-Output $oneLine
Write-Output "Verified public installer: $installerUrl"
Write-Output "SHA-256: $hash"
Write-Output "WinGet manifest: $manifestDir"
Write-Output 'Next: validate and test this manifest, then submit only its three YAML files to microsoft/winget-pkgs.'
