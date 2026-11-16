param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipBuild,
    [string]$Browser = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith((Join-Path $root 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release gate output must be a fresh directory under this checkout artifacts folder.'
}
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; old reports cannot count as a new pass.' }
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($sdk) { $sdk.Source } else { Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'The .NET 10 Windows desktop SDK is required.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
if ($Browser) {
    if (-not (Test-Path -LiteralPath $Browser -PathType Leaf)) { throw 'The fixture browser executable does not exist.' }
    $env:DESKWEAVE_WORKSPACE_BROWSER = [IO.Path]::GetFullPath($Browser)
}
if (-not $SkipBuild) {
    foreach ($project in @('engine-probe/Deskweave.EngineProbe.csproj', 'ui-probe/Deskweave.UiProbe.csproj')) {
        & $dotnet build (Join-Path $root $project) -c Release -warnaserror --nologo -p:UseSharedCompilation=false -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}
$probe = Join-Path $root 'engine-probe/bin/Release/net10.0-windows/Deskweave.Probe.exe'
$ui = Join-Path $root 'ui-probe/bin/Release/net10.0-windows/win-x64/Deskweave.UiProbe.exe'
$gates = @(
    @('engine', $probe, 'full', 'report.json'),
    @('bridge-lifetime', $probe, '--bridge-lifetime', 'report.json'),
    @('minecraft-preview', $probe, '--minecraft-preview-checks', 'result.json'),
    @('lifecycle', $ui, '--lifecycle', 'ui-report.json'),
    @('storage', $ui, '--storage', 'ui-report.json'),
    @('ui', $ui, 'full', 'ui-report.json'),
    @('browser-ui', $ui, '--browser-ui', 'ui-report.json')
)
[IO.Directory]::CreateDirectory($output) | Out-Null
$provenance = [ordered]@{
    commit = (& git -C $root rev-parse HEAD)
    workingTree = @(& git -C $root status --porcelain)
    buildPerformed = (-not $SkipBuild)
    binaries = @(Get-FileHash -LiteralPath @($probe, $ui,
        (Join-Path (Split-Path -Parent $ui) 'ARS.dll'),
        (Join-Path (Split-Path -Parent $probe) 'Deskweave.AgentWorkspaces.dll'),
        (Join-Path (Split-Path -Parent $probe) 'Bridge/ARS.WorkspaceBridge.dll')) | Select-Object Path,Hash)
}
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding UTF8
$rows = [Collections.Generic.List[object]]::new()
foreach ($gate in $gates) {
    $name, $executable, $mode, $reportName = $gate
    $folder = Join-Path $output $name
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $probe -ArgumentList @('--isolate-suite', ('"' + $executable + '"'), $mode, ('"' + $folder + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output ($name + '.stdout.log')) -RedirectStandardError (Join-Path $output ($name + '.stderr.log'))
    $null = $process.Handle # Retain identity before a short process exits on Windows PowerShell.
    try {
        if (-not $process.WaitForExit(480000)) {
            Stop-Process -InputObject $process -Force
            throw "Isolated gate exceeded its watchdog: $name"
        }
        $isolation = Get-Content -LiteralPath (Join-Path $folder 'isolation.json') -Raw | ConvertFrom-Json
        $report = Get-Content -LiteralPath (Join-Path $folder $reportName) -Raw | ConvertFrom-Json
        $ok = $isolation.exited -and $isolation.exitCode -eq 0 -and $isolation.startupDesktop -ceq $isolation.desktop
        if ($reportName -eq 'ui-report.json') { $ok = $ok -and $report.success }
        elseif ($reportName -eq 'report.json') { $ok = $ok -and $report.status -eq 'passed' -and -not $report.failure }
        else { $ok = $ok -and $report.passed -gt 0 -and $report.failed -eq 0 }
        $count = if ($reportName -eq 'ui-report.json') { @($report.passed).Count } elseif ($reportName -eq 'report.json') { @($report.claims).Count } else { $report.passed }
        $row = [pscustomobject]@{ gate=$name; passed=[bool]$ok; checks=$count; exitCode=$isolation.exitCode; startupDesktop=$isolation.startupDesktop; seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2); report=(Join-Path $folder $reportName) }
        $rows.Add($row)
        $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'gates.json') -Encoding UTF8
        $row | ConvertTo-Json -Compress
    }
    finally { $process.Dispose() }
}
if ($rows | Where-Object { -not $_.passed }) { throw "A release gate failed; inspect $output/gates.json" }
Write-Output "All $($rows.Count) source release gates passed. Signed package and disposable-machine checks remain separate."
