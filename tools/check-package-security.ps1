param([Parameter(Mandatory)][string]$Installer, [Parameter(Mandatory)][string]$OutputDirectory)
# Records a local Defender scan and Authenticode status for the exact private release payload.
# A clean scan does not establish download reputation or permit unsigned public distribution.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$installerPath = [IO.Path]::GetFullPath($Installer)
foreach ($path in @($output, $installerPath)) {
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Security-check inputs and output must stay inside this Deskweave checkout.'
    }
}
if (Test-Path -LiteralPath $output) { throw 'Use a new output folder for each security scan.' }
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "No installer: $installerPath" }
$status = Get-MpComputerStatus
if (-not $status.AntivirusEnabled) { throw 'Microsoft Defender is not enabled; scan with the installed security provider.' }
$scanner = Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform') -Directory |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $scanner) { throw 'Microsoft Defender command-line scanner was not found.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$report = [ordered]@{
    observedAt = [DateTimeOffset]::UtcNow.ToString('o')
    signatureUpdatedAt = $status.AntivirusSignatureLastUpdated.ToString('o')
    antivirusVersion = $status.AntivirusSignatureVersion
    realTimeProtection = $status.RealTimeProtectionEnabled
    scans = @(); files = @()
}
$failure = $null
try {
    foreach ($target in @((Join-Path $root 'out'), $installerPath)) {
        $index = $report.scans.Count + 1
        $scanLog = Join-Path $output "scan-$index.txt"
        # Keep the scan read-only; this does not change real-time protection or its settings.
        & $scanner -Scan -ScanType 3 -File $target -DisableRemediation 2>&1 | Tee-Object -FilePath $scanLog
        $code = $LASTEXITCODE
        $report.scans += [ordered]@{ path = $target; exitCode = $code; log = $scanLog }
        if ($code -ne 0) { throw "Defender scan did not pass ($code): $target. See $scanLog" }
    }
    foreach ($file in @($installerPath, (Join-Path $root 'out\Deskweave.exe'),
        (Join-Path $root 'out\Deskweave.dll'), (Join-Path $root 'out\Deskweave.AgentWorkspaces.dll'),
        (Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.exe'), (Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.dll'))) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $report.files += [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $file).Hash; signature = [string]$signature.Status }
    }
    $report.allFilesSigned = @($report.files | Where-Object { $_.signature -ne 'Valid' }).Count -eq 0
}
catch { $failure = $_.ToString() }
finally {
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'security-report.json') -Encoding UTF8
}
if ($failure) { throw $failure }
Write-Output "Local malware scans passed. Signature evidence: $output\security-report.json"
