<#
.SYNOPSIS
Opt-in developer build route with bounded run count. Preview by default; -Run builds without installing.
#>
[CmdletBinding()]
param(
    [ValidateSet('Build','Publish')][string]$Kind = 'Build',
    [ValidateRange(1, 100)][int]$MaximumRuns = 2,
    [ValidateRange(1, 1000)][int]$BudgetGiB = 4,
    [string]$RepositoryRoot,
    [switch]$Run
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$bounded = Join-Path $repo 'artifacts/bounded'
$kindRoot = Join-Path $bounded $Kind.ToLowerInvariant()
function Assert-Plain([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $entry = Get-Item -LiteralPath $Path -Force
    while ($null -ne $entry) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($entry.FullName)" }
        $entry = if ($entry -is [IO.DirectoryInfo]) { $entry.Parent } else { $entry.Directory }
    }
}
function Size([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    Assert-Plain $Path
    [long]$sum = 0
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($item.FullName)" }
        if ($item.PSIsContainer) { $sum += Size $item.FullName } else { $sum += $item.Length }
    }
    return $sum
}
Assert-Plain $repo
Assert-Plain (Join-Path $repo 'artifacts')
Assert-Plain $bounded
Assert-Plain $kindRoot
$lock = $null
try {
    if ($Run) {
        # Serialize admission across both build kinds without leaving a lock inside measured output.
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $id = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Replace('-','') }
        finally { $sha.Dispose() }
        $lockPath = Join-Path ([IO.Path]::GetTempPath()) "ars-bounded-$id.lock"
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    $runs = @(if (Test-Path -LiteralPath $kindRoot) { Get-ChildItem -LiteralPath $kindRoot -Force })
    $bytes = Size $bounded
    if ($runs.Count -ge $MaximumRuns) { throw "Run cap reached ($($runs.Count)/$MaximumRuns) for $Kind. Review artifacts-retention.ps1; no output is automatically removed." }
    if ($bytes -ge ([long]$BudgetGiB * 1GB)) { throw 'Bounded output budget reached. Review retention before another build.' }
    $target = Join-Path $kindRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $command = @($(if ($Kind -eq 'Publish') { 'publish' } else { 'build' }), (Join-Path $repo 'app/ARS.csproj'), '-c', 'Release', '--artifacts-path', (Join-Path $target 'dotnet'), '-p:UseSharedCompilation=false', '-m:1', '-nr:false', '--nologo', '-warnaserror')
    if ($Kind -eq 'Publish') { $command += @('-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $target 'payload')) }
    [pscustomobject]@{ mode = $(if ($Run) { 'build' } else { 'preview' }); kind = $Kind; existingRuns = $runs.Count; maximumRunsPerKind = $MaximumRuns; currentBytes = $bytes; budgetBytes = [long]$BudgetGiB * 1GB; target = $target; executable = 'dotnet'; arguments = $command } | ConvertTo-Json -Depth 4
    if ($Run) {
        if (Test-Path -LiteralPath $target) { throw 'Fresh output path already exists.' }
        [IO.Directory]::CreateDirectory($target) | Out-Null
        & dotnet @command
        if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE); failed output retained and counts toward cap: $target" }
        $after = Size $bounded
        if ($after -gt ([long]$BudgetGiB * 1GB)) { throw "Build exceeded admission budget ($after bytes). Output retained; further builds blocked. This is not a filesystem quota." }
    }
} finally { if ($null -ne $lock) { $lock.Dispose() } }
