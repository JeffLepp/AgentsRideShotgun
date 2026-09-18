param([string]$Version = '')
# Builds Deskweave-Setup: a per-user install (no admin) with a Start menu shortcut and an Apps &
# features uninstall that takes Deskweave out of the agents' configs. Publishes fresh first, so
# Deskweave must be closed. Unsigned until the owner buys a code-signing certificate: Windows
# SmartScreen warns on first open until then.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Version) { $Version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup[0].Version }
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw 'The Velopack CLI is missing. Run: dotnet tool install -g vpk' }
$output = Join-Path $root "artifacts\installer\$Version"
if (Test-Path -LiteralPath $output) { throw "An installer for $Version already exists at $output. Released versions are immutable; bump the version." }

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'publish.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no installer was built.' }

# The install id is not "Deskweave" on purpose: Velopack installs to %LOCALAPPDATA%\<id> and its
# uninstall deletes that whole folder, while the owner's workspaces live in %LOCALAPPDATA%\Deskweave.
& vpk pack --packId DeskweaveApp --packVersion $Version --packDir (Join-Path $root 'out') `
    --packTitle Deskweave --packAuthors 'Jefferson Kline' --mainExe Deskweave.exe `
    --icon (Join-Path $root 'app\Assets\Deskweave.ico') --shortcuts StartMenuRoot `
    --outputDir $output --noPortable --skip-updates
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
$built = Get-ChildItem -LiteralPath $output -Filter '*Setup.exe' | Select-Object -First 1
if (-not $built) { throw 'vpk reported success but produced no Setup.exe.' }
# The name a person downloads. The update feed names the package, not this file.
$setup = Move-Item -LiteralPath $built.FullName -Destination (Join-Path $output 'Deskweave-Setup.exe') -PassThru
Write-Output ("{0} ({1:N0} MB) SHA-256 {2}" -f $setup.FullName, ($setup.Length / 1MB), (Get-FileHash -LiteralPath $setup.FullName).Hash)
