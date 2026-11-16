<#
.SYNOPSIS
Inventory developer artifacts. Dry run unless -Execute and a matching -ApprovedPlan are supplied.
.DESCRIPTION
Never manages installed ARS, user workspaces, out/, or external rollback bundles.
Sizes are logical file bytes, not allocated disk space. Run only with builds stopped before execution.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$RepositoryRoot,
    [string]$PolicyPath,
    [ValidateRange(-1, 1000)][int]$KeepLatest = -1,
    [ValidateRange(-1, 36500)][int]$MinimumAgeDays = -1,
    [string]$ReportPath,
    [switch]$Execute,
    [string]$ApprovedPlan
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
if (-not $PolicyPath) { $PolicyPath = Join-Path $PSScriptRoot 'artifacts-retention.policy.json' }
$repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
$root = Join-Path $repo 'artifacts'
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
if ($policy.schemaVersion -ne 1) { throw 'Unsupported policy schema.' }
if ($KeepLatest -lt 0) { $KeepLatest = [int]$policy.keepLatest }
if ($MinimumAgeDays -lt 0) { $MinimumAgeDays = [int]$policy.minimumAgeDays }
if ($KeepLatest -lt 0 -or $MinimumAgeDays -lt 0) { throw 'Negative retention policy.' }
$now = [DateTime]::UtcNow

function Within([string]$Path, [string]$Parent) {
    $a = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $b = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    return $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Assert-PlainPath([string]$Path) {
    $cursor = Get-Item -LiteralPath $Path -Force
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($cursor.FullName)" }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
}
Assert-PlainPath $root
if ($ReportPath) {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    if ((Within $ReportPath $root) -or $ReportPath -eq $root) { throw 'Save reports outside artifacts so reports do not change the measured tree.' }
    if (Test-Path -LiteralPath $ReportPath) { throw "Report already exists (no overwrite): $ReportPath" }
    Assert-PlainPath (Split-Path -Parent $ReportPath)
}
if ($Execute -and -not $ApprovedPlan) { throw '-Execute requires an independently reviewed -ApprovedPlan JSON dry run.' }

function Measure-Tree([string]$Path) {
    Assert-PlainPath $Path
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Path)
    $records = [Collections.Generic.List[string]]::new()
    [long]$bytes = 0; [long]$count = 0
    $last = [DateTime]::MinValue
    $markers = [Collections.Generic.List[string]]::new()
    while ($queue.Count) {
        $entry = Get-Item -LiteralPath $queue.Dequeue() -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($entry.FullName)" }
        if ($entry.LastWriteTimeUtc -gt $last) { $last = $entry.LastWriteTimeUtc }
        if ($entry.Name -eq '.retention-keep') { $markers.Add($entry.FullName) }
        $relative = $entry.FullName.Substring($Path.Length)
        if ($entry.PSIsContainer) {
            $records.Add("D|$relative|$($entry.LastWriteTimeUtc.Ticks)")
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $queue.Enqueue($child.FullName) }
        } else {
            $bytes += $entry.Length; $count++
            $records.Add("F|$relative|$($entry.Length)|$($entry.LastWriteTimeUtc.Ticks)")
        }
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((($records | Sort-Object) -join "`n")))).Replace('-', '') }
    finally { $sha.Dispose() }
    [pscustomobject]@{ bytes = $bytes; files = $count; lastWriteUtc = $last.ToString('o'); fingerprint = $fingerprint; markers = @($markers.ToArray()) }
}
function Get-Kind([string]$Relative) {
    $first = $Relative.Split('/')[0]
    if ($Relative.StartsWith('bounded/')) { return 'bounded/' + $Relative.Split('/')[1] }
    if ($first -in @('previous','publish','installer','installer-staging','mcp-bundle')) { return $first }
    if ($first -match '^(bs|f|pf|ss|th)-|^design-profile|^edge-reference') { return 'test-state' }
    if ($first -match 'publish|^mvp$|^lifecycle$|^readiness|^clean-install|^release-|^backup$|^nuget-packages$') { return 'legacy-build' }
    if ($first -match '^browser') { return 'browser-evidence' }
    return 'legacy-evidence'
}
# Split known build containers into runs; all other legacy top-level entries are atomic units.
$units = [Collections.Generic.List[object]]::new()
foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
    if ($entry.Name -eq 'bounded' -and $entry.PSIsContainer) {
        Assert-PlainPath $entry.FullName
        foreach ($kind in Get-ChildItem -LiteralPath $entry.FullName -Force) {
            Assert-PlainPath $kind.FullName
            if ($kind.PSIsContainer) { foreach ($run in Get-ChildItem -LiteralPath $kind.FullName -Force) { $units.Add($run) } }
            else { $units.Add($kind) }
        }
    } elseif ($entry.PSIsContainer -and $entry.Name -in @('previous','publish','installer','installer-staging','mcp-bundle')) {
        Assert-PlainPath $entry.FullName
        foreach ($run in Get-ChildItem -LiteralPath $entry.FullName -Force) { $units.Add($run) }
    } else { $units.Add($entry) }
}
$processes = @(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID })
# Reference evidence is informational: historical test links do not make superseded output a rollback.
$references = [Collections.Generic.List[object]]::new()
foreach ($referenceRoot in @($repo) + @($policy.referenceRoots)) {
    if (-not (Test-Path -LiteralPath $referenceRoot -PathType Container)) { continue }
    $files = @(& rg --files --hidden -g '!.git' -g '!artifacts' -g '!out' -g '!bin' -g '!obj' -g '!node_modules' -g '!.worktrees' -g '!dependencies' -g '!scratch' -g '!portable' -g '!mcp-bundle' -g '*.md' -g '*.ps1' -g '*.json' -g '*.csproj' -g '*.props' -g '*.yml' -g '*.cs' $referenceRoot)
    if ($LASTEXITCODE -gt 1) { throw "Reference search failed: $referenceRoot" }
    foreach ($file in $files) {
        if ((Get-Item -LiteralPath $file).Length -gt 2MB) { continue }
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines($file)) {
            $lineNumber++
            $normalized = $line.Replace('\','/').ToLowerInvariant()
            if ($normalized.Contains('artifacts/') -or $normalized.Contains('artifacts\\')) {
                $references.Add([pscustomobject]@{ source = "${file}:$lineNumber"; text = $normalized.Replace('//','/') })
            }
        }
    }
}
$rows = @(foreach ($unit in $units) {
    if (-not (Within $unit.FullName $root)) { throw 'Inventory escaped artifacts.' }
    $relative = $unit.FullName.Substring($root.Length + 1).Replace('\','/')
    $measurement = Measure-Tree $unit.FullName
    $holds = [Collections.Generic.List[string]]::new()
    foreach ($pin in $policy.pins) {
        $pinPath = [IO.Path]::GetFullPath((Join-Path $root $pin.path))
        if (-not (Within $pinPath $root)) { throw "Pin escaped artifacts: $($pin.path)" }
        if ($pinPath -eq $unit.FullName -or (Within $pinPath $unit.FullName) -or (Within $unit.FullName $pinPath)) { $holds.Add('PIN: ' + $pin.reason) }
    }
    if ($measurement.markers.Count) { $holds.Add('Explicit .retention-keep marker') }
    $age = ($now - [DateTime]::Parse($measurement.lastWriteUtc).ToUniversalTime()).TotalDays
    if ($age -lt $MinimumAgeDays) { $holds.Add("Modified within $MinimumAgeDays days; possibly concurrent work") }
    foreach ($process in $processes) {
        $command = ([string]$process.CommandLine).Replace('\','/')
        $exe = [string]$process.ExecutablePath
        if (($exe -and (($exe -eq $unit.FullName) -or (Within $exe $unit.FullName))) -or $command.IndexOf($unit.FullName.Replace('\','/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $holds.Add("Live process PID $($process.ProcessId)")
        }
    }
    $needle = 'artifacts/' + $relative.ToLowerInvariant()
    $referencedBy = @($references | Where-Object { $_.text -match ([regex]::Escape($needle) + '(?=$|[/`\s"''<>),;])') } | Select-Object -ExpandProperty source -Unique)
    [pscustomobject]@{ path = $unit.FullName; relativePath = $relative; kind = (Get-Kind $relative); bytes = $measurement.bytes; files = $measurement.files; lastWriteUtc = $measurement.lastWriteUtc; ageDays = [Math]::Round($age, 4); fingerprint = $measurement.fingerprint; referencedBy = $referencedBy; holds = @($holds.ToArray()); action = ''; reason = '' }
})
foreach ($group in $rows | Group-Object kind) {
    # Protected/recent runs count toward N; pins never become deletable even if outside the newest N.
    $latest = @($group.Group | Sort-Object @{Expression = { [DateTime]::Parse($_.lastWriteUtc) }; Descending = $true}, relativePath | Select-Object -First $KeepLatest)
    foreach ($row in $group.Group) {
        if (@($latest | Select-Object -ExpandProperty path) -contains $row.path) { $row.holds += "Latest $KeepLatest of kind $($row.kind)" }
        $row.action = if ($row.holds.Count) { 'keep' } else { 'would-delete' }
        $row.reason = if ($row.holds.Count) { $row.holds -join '; ' } else { "Superseded developer $($row.kind); outside newest $KeepLatest and at least $MinimumAgeDays days old. Historical references are listed for review." }
    }
}
$external = @(foreach ($bundle in $policy.externalBundles) {
    Assert-PlainPath $bundle
    $manifestPath = Join-Path $bundle 'deployment.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    [pscustomobject]@{ path = $bundle; action = 'out-of-scope-preserve'; manifestSha256 = (Get-FileHash -LiteralPath $manifestPath).Hash; previousVersion = $manifest.previousVersion; candidateVersion = $manifest.candidateVersion; note = 'Use stage-update.ps1 -Mode Verify to verify all payload hashes; this inventory does not substitute for that check.' }
})
$candidates = @($rows | Where-Object action -eq 'would-delete' | Sort-Object relativePath)
[long]$totalBytes = 0; [long]$candidateBytes = 0
foreach ($row in $rows) { $totalBytes += $row.bytes }
foreach ($row in $candidates) { $candidateBytes += $row.bytes }
$result = [pscustomobject]@{
    schemaVersion = 1; generatedAtUtc = $now.ToString('o'); mode = $(if ($Execute) { 'execute' } else { 'dry-run' });
    repositoryRoot = $repo; artifactsRoot = $root; keepLatest = $KeepLatest; minimumAgeDays = $MinimumAgeDays;
    policySha256 = (Get-FileHash -LiteralPath $PolicyPath).Hash;
    totalBytes = $totalBytes; wouldDeleteBytes = $candidateBytes;
    deletionUnitCount = $candidates.Count; deletedBytes = [long]0; externalBundles = $external;
    accounting = 'Logical file lengths; includes hidden files; no reparse traversal. Does not measure allocated/reclaimable disk clusters, hard-link sharing, or open-file deletion delays.';
    referenceScope = 'Repo source/docs and configured referenceRoots; exact artifacts/unit path matches only, <=2 MiB text files. Dynamic aliases and generic container links may be absent. References are informational.';
    entries = @($rows | Sort-Object relativePath)
}
if ($Execute) {
    $approved = Get-Content -LiteralPath $ApprovedPlan -Raw | ConvertFrom-Json
    if ($approved.schemaVersion -ne 1 -or $approved.mode -ne 'dry-run' -or $approved.artifactsRoot -ne $root -or $approved.keepLatest -ne $KeepLatest -or $approved.minimumAgeDays -ne $MinimumAgeDays -or $approved.policySha256 -ne $result.policySha256) { throw 'Approved plan settings differ.' }
    $expected = @($approved.entries | Where-Object action -eq 'would-delete' | Sort-Object relativePath)
    if ($expected.Count -ne $candidates.Count) { throw 'Candidate set changed; review a new dry run.' }
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        if ($expected[$i].path -ne $candidates[$i].path -or $expected[$i].fingerprint -ne $candidates[$i].fingerprint) { throw 'Candidate contents changed; review a new dry run.' }
    }
    foreach ($row in $candidates) {
        # Recheck containment, all ancestors/descendants, changes and live references immediately before removal.
        if (-not (Within $row.path $root)) { throw 'Deletion escaped artifacts.' }
        $fresh = Measure-Tree $row.path
        if ($fresh.fingerprint -ne $row.fingerprint) { throw "Changed since inventory: $($row.path)" }
        foreach ($process in Get-CimInstance Win32_Process) {
            if ($process.ProcessId -eq $PID) { continue }
            $exe = [string]$process.ExecutablePath
            $command = ([string]$process.CommandLine).Replace('\','/')
            if (($exe -and (($exe -eq $row.path) -or (Within $exe $row.path))) -or $command.IndexOf($row.path.Replace('\','/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "New live reference: $($row.path)" }
        }
        if ($PSCmdlet.ShouldProcess($row.path, "Delete developer artifacts ($($row.bytes) logical bytes)")) {
            Remove-Item -LiteralPath $row.path -Recurse -Force -ErrorAction Stop
            $row.action = 'deleted'; $result.deletedBytes += $row.bytes
        }
    }
}
$json = $result | ConvertTo-Json -Depth 10
if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, $json, [Text.UTF8Encoding]::new($false)) }
$json
