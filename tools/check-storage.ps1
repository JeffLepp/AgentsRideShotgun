param([string]$OutputDirectory = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/wo05a/storage-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory for each test run.' }
# Process-scoped SDK discovery also fixes apphost runtime discovery in shells without dotnet on PATH.
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($sdk) { $sdk.Source } else { Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 Windows desktop SDK to run storage checks.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
if (-not $SkipBuild) {
    foreach ($project in @('ui-probe/Deskweave.UiProbe.csproj', 'engine-probe/Deskweave.EngineProbe.csproj')) {
        & $dotnetPath build (Join-Path $root $project) -c Release -warnaserror --nologo -p:UseSharedCompilation=false -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}
$probe = Join-Path $root 'engine-probe/bin/Release/net10.0-windows/Deskweave.Probe.exe'
$ui = Join-Path $root 'ui-probe/bin/Release/net10.0-windows/win-x64/Deskweave.UiProbe.exe'
$process = Start-Process -FilePath $probe -ArgumentList @('--isolate-suite', ('"' + $ui + '"'), '--storage', ('"' + $output + '"')) -WindowStyle Hidden -PassThru
$null = $process.Handle
try {
    if (-not $process.WaitForExit(480000)) { throw "Storage probe did not finish; inspect $output" }
    if ($process.ExitCode -ne 0) { throw "Storage probe failed ($($process.ExitCode)); inspect $output" }
    $report = Get-Content -LiteralPath (Join-Path $output 'ui-report.json') -Raw | ConvertFrom-Json
    $isolation = Get-Content -LiteralPath (Join-Path $output 'isolation.json') -Raw | ConvertFrom-Json
    if (-not $report.success -or -not $isolation.exited -or $isolation.exitCode -ne 0 -or $isolation.startupDesktop -cne $isolation.desktop) { throw 'Storage verification receipt failed.' }
    [pscustomobject]@{Passed = @($report.passed).Count; Version = $report.version; Output = $output}
}
finally { $process.Dispose() }
