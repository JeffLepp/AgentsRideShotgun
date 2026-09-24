param([Parameter(Mandatory)][string]$Installer, [Parameter(Mandatory)][string]$OutputDirectory, [switch]$Sign)
# Records a local Defender scan and Authenticode status for the exact release payload: the files
# inside the package next to the installer, not out\, since signing changes the installed bytes.
# -Sign (a signed build) fails when any packaged binary is not validly signed.
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
$package = @(Get-ChildItem -LiteralPath (Split-Path -Parent $installerPath) -Filter '*-full.nupkg')
if ($package.Count -ne 1) { throw "Expected one full package beside the installer, found $($package.Count)." }
$status = Get-MpComputerStatus
if (-not $status.AntivirusEnabled) { throw 'Microsoft Defender is not enabled; scan with the installed security provider.' }
$scanner = Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform') -Directory |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $scanner) { throw 'Microsoft Defender command-line scanner was not found.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
# The app as Setup installs it: lib/app/ of the package.
$payload = Join-Path $output 'payload'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package[0].FullName)
try {
    foreach ($entry in @($zip.Entries | Where-Object { $_.FullName -like 'lib/app/*' -and $_.Name })) {
        $target = [IO.Path]::GetFullPath((Join-Path $payload $entry.FullName.Substring(8)))
        if (-not $target.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Package entry leaves the payload: $($entry.FullName)" }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
    }
}
finally { $zip.Dispose() }
if (-not (Test-Path -LiteralPath (Join-Path $payload 'ARS.exe') -PathType Leaf)) { throw 'The package holds no ARS.exe.' }
$report = [ordered]@{
    observedAt = [DateTimeOffset]::UtcNow.ToString('o')
    signatureUpdatedAt = $status.AntivirusSignatureLastUpdated.ToString('o')
    antivirusVersion = $status.AntivirusSignatureVersion
    realTimeProtection = $status.RealTimeProtectionEnabled
    scans = @(); files = @()
}
$failure = $null
try {
    foreach ($target in @($payload, $installerPath)) {
        $index = $report.scans.Count + 1
        $scanLog = Join-Path $output "scan-$index.txt"
        # Keep the scan read-only; this does not change real-time protection or its settings.
        & $scanner -Scan -ScanType 3 -File $target -DisableRemediation 2>&1 | Tee-Object -FilePath $scanLog
        $code = $LASTEXITCODE
        $report.scans += [ordered]@{ path = $target; exitCode = $code; log = $scanLog }
        if ($code -ne 0) { throw "Defender scan did not pass ($code): $target. See $scanLog" }
    }
    # Setup and every packaged binary.
    $binaries = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object FullName)
    foreach ($file in @($installerPath) + $binaries) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $report.files += [ordered]@{ path = $file; sha256 = (Get-FileHash -LiteralPath $file).Hash; signature = [string]$signature.Status }
    }
    $unsigned = @($report.files | Where-Object { $_.signature -ne 'Valid' })
    $report.allFilesSigned = $unsigned.Count -eq 0
    if ($Sign -and $unsigned.Count -gt 0) {
        throw "$($unsigned.Count) file(s) of a signed build are not validly signed, first: $($unsigned[0].path)"
    }
}
catch { $failure = $_.ToString() }
finally {
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'security-report.json') -Encoding UTF8
}
if ($failure) { throw $failure }
$signed = if ($report.allFilesSigned) { 'every file is signed' } else { 'NOT every file is signed' }
Write-Output "Local malware scans passed; $signed. Evidence: $output\security-report.json"
