[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Tests require a new evidence directory; fixtures are retained, never deleted.' }
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$repo = Join-Path $evidence 'fixture'
$root = Join-Path $repo 'artifacts'
[IO.Directory]::CreateDirectory($root) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $checks.Add($Name)
}
function Fixture([string]$Relative, [int]$Days, [string]$Text) {
    $path = Join-Path $root $Relative
    [IO.Directory]::CreateDirectory($path) | Out-Null
    [IO.File]::WriteAllText((Join-Path $path 'data.bin'), $Text)
    $time = [DateTime]::UtcNow.AddDays(-$Days)
    (Get-Item -LiteralPath (Join-Path $path 'data.bin')).LastWriteTimeUtc = $time
    (Get-Item -LiteralPath $path).LastWriteTimeUtc = $time
}
Fixture 'previous/old' 30 '12345'
Fixture 'previous/pinned' 25 '123456'
Fixture 'previous/marked' 40 '1234567'
[IO.File]::WriteAllText((Join-Path $root 'previous/marked/.retention-keep'), 'hold')
(Get-Item -LiteralPath (Join-Path $root 'previous/marked/.retention-keep') -Force).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-40)
(Get-Item -LiteralPath (Join-Path $root 'previous/marked')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-40)
Fixture 'previous/recent-descendant' 20 '12345678'
(Get-Item -LiteralPath (Join-Path $root 'previous/recent-descendant/data.bin')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-1)
Fixture 'publish/old' 25 '123456789'
Fixture 'publish/newest' 10 '1234567890'
Fixture 'live-test' 40 '12345678901'
$hidden = Join-Path $root 'publish/old/hidden.bin'
[IO.File]::WriteAllText($hidden, 'hidden')
[IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Hidden)
(Get-Item -LiteralPath $hidden -Force).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-25)
(Get-Item -LiteralPath (Join-Path $root 'publish/old')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-25)
[IO.File]::WriteAllText((Join-Path $repo 'references.md'), 'Historical evidence: artifacts/previous/old/data.bin')
$policyPath = Join-Path $evidence 'policy.json'
@{ schemaVersion = 1; keepLatest = 1; minimumAgeDays = 7; pins = @(@{ path = 'previous/pinned'; reason = 'fixture rollback' }); externalBundles = @(); referenceRoots = @() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $policyPath -Encoding UTF8
function Snapshot {
    @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName).Hash)" }) -join "`n"
}
$before = Snapshot
$helper = Join-Path $evidence 'hold.ps1'
@'
param([string]$HoldPath,[string]$Ready,[string]$Done)
[IO.File]::WriteAllText($Ready, $HoldPath)
while (-not (Test-Path -LiteralPath $Done)) { Start-Sleep -Milliseconds 100 }
'@ | Set-Content -LiteralPath $helper -Encoding UTF8
$ready = Join-Path $evidence 'ready.txt'
$done = Join-Path $evidence 'done.txt'
$helperArgs = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -HoldPath "{1}" -Ready "{2}" -Done "{3}"' -f $helper,(Join-Path $root 'live-test'),$ready,$done
$child = Start-Process powershell.exe -ArgumentList $helperArgs -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Headless helper did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $planPath = Join-Path $evidence 'plan.json'
    $output = & (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath -ReportPath $planPath
    $plan = $output | ConvertFrom-Json
    Check ($plan.mode -eq 'dry-run' -and $plan.deletedBytes -eq 0) 'Default dry run performs zero deletion'
    Check ($plan.totalBytes -eq 66) 'Exact accounting includes hidden bytes (independent expected total 66)'
    Check ($plan.wouldDeleteBytes -eq 20 -and $plan.deletionUnitCount -eq 2) 'Only two superseded units selected; exact expected bytes 20'
    Check (@($plan.entries | Where-Object { $_.relativePath -eq 'publish/newest' -and $_.action -eq 'keep' }).Count -eq 1) 'Latest N retained separately per build kind'
    Check (@($plan.entries | Where-Object { $_.relativePath -eq 'previous/pinned' -and $_.action -eq 'keep' }).Count -eq 1) 'Explicit rollback pin retained outside latest N'
    Check (@($plan.entries | Where-Object { $_.relativePath -eq 'previous/marked' -and $_.action -eq 'keep' }).Count -eq 1) 'Nested retention marker retained'
    Check (@($plan.entries | Where-Object { $_.relativePath -eq 'previous/recent-descendant' -and $_.action -eq 'keep' }).Count -eq 1) 'Recent child protects old parent'
    Check (@($plan.entries | Where-Object { $_.relativePath -eq 'live-test' -and $_.reason -like '*Live process*' }).Count -eq 1) 'Live command-line reference protects candidate'
    $old = @($plan.entries | Where-Object relativePath -eq 'previous/old')[0]
    Check ($old.action -eq 'would-delete' -and $old.referencedBy.Count -eq 1) 'Historical exact reference recorded without implicit pin'
    Check ((Snapshot) -ceq $before) 'Independent byte/hash/time readback proves dry run unchanged'
    $zero = (& (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath -KeepLatest 0 | ConvertFrom-Json)
    Check (@($zero.entries | Where-Object { $_.relativePath -eq 'publish/newest' -and $_.action -eq 'would-delete' }).Count -eq 1) 'KeepLatest zero supported without removing protected units'
    $none = (& (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath -KeepLatest 1000 | ConvertFrom-Json)
    Check ($none.deletionUnitCount -eq 0 -and $none.wouldDeleteBytes -eq 0 -and $none.totalBytes -eq 66) 'Empty candidate set reports zero bytes successfully'
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath -ReportPath (Join-Path $root 'bad.json') | Out-Null } catch { $refused = $_.Exception.Message -like '*outside artifacts*' }
    Check $refused 'Self-mutating inventory report destination rejected'
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath -ReportPath $planPath | Out-Null } catch { $refused = $_.Exception.Message -like '*already exists*' }
    Check $refused 'Existing report overwrite rejected'
    # Junction requires no elevated symlink privilege and is retained as evidence.
    $outside = Join-Path $evidence 'outside'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'outside data')
    New-Item -ItemType Junction -Path (Join-Path $root 'escape') -Target $outside | Out-Null
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'artifacts-retention.ps1') -RepositoryRoot $repo -PolicyPath $policyPath | Out-Null } catch { $refused = $_.Exception.Message -like '*Reparse point refused*' }
    Check $refused 'Junction traversal rejected; outside sentinel unchanged'
    Check ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -ceq 'outside data') 'Outside sentinel preserved'
    $preview = & (Join-Path $PSScriptRoot 'build-bounded.ps1') -Kind Publish | ConvertFrom-Json
    Check ($preview.mode -eq 'preview' -and $preview.target -like '*artifacts\bounded\publish\*' -and $preview.arguments -contains '--artifacts-path') 'Future publish preview routes output/intermediates under bounded path'
    $capRoot = Join-Path $evidence 'cap-fixture'
    [IO.Directory]::CreateDirectory((Join-Path $capRoot 'artifacts/bounded/build/one')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $capRoot 'artifacts/bounded/build/two')) | Out-Null
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'build-bounded.ps1') -RepositoryRoot $capRoot | Out-Null } catch { $refused = $_.Exception.Message -like '*Run cap reached*' }
    Check $refused 'Bounded route refuses a third run without deleting or building'
    [pscustomobject]@{ passed = $checks.Count; failed = 0; checks = @($checks.ToArray()); cleanupExecuted = $false; fixturePath = $repo; note = 'No -Execute invocation, no deletion. Destructive branch and real dotnet build are not exercised.' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'test-results.json') -Encoding UTF8
    $checks | ForEach-Object { "PASS: $_" }
} finally {
    [IO.File]::WriteAllText($done, 'test complete')
    if (-not $child.WaitForExit(10000)) { throw "Helper $($child.Id) failed to exit after completion signal." }
    $child.Dispose()
}
