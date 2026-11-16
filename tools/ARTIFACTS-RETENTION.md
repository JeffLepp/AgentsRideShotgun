# Developer output retention

This manages checkout `artifacts/`, not ARS product storage. Installed binaries,
browser profiles, user workspaces, `out/`, and external deployment bundles are
outside its deletion scope. Existing build scripts are unchanged for WO-05 Part A.

Run from this checkout in Windows PowerShell 5.1 or later (requires `rg`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/artifacts-retention.ps1 -ReportPath C:/absolute/new-plan.json
```

The default is read-only. JSON lists every atomic deletion unit with logical bytes,
file count, latest modification time across all descendants, age, build kind,
source/report references, holds, and a reason. Known containers (`previous`,
`publish`, `installer`, `installer-staging`, `mcp-bundle`, and `bounded/<kind>`)
are split into individual runs. Other legacy top-level entries are atomic units.
Sizes do not promise exact reclaimed disk space (allocation, hard links and open
handles differ). Missing/inaccessible paths and reparse points fail the scan.

The policy keeps the latest two per kind plus anything modified in seven days,
explicit pins, `.retention-keep` markers anywhere inside a unit, and executable or
absolute command-line references from live processes. Unknown legacy entries are
reviewable candidates with reasons; they are never silently removed. References
are exact unit-path matches in source/docs and configured orchestration reports,
not a dependency graph. Generic container paths, dynamically constructed paths,
working directories and aliases may not be discovered. Historical references do
not automatically pin superseded tests; review them before approving deletion.
Use `-KeepLatest N` and `-MinimumAgeDays N` for another reviewed policy.

WO-05B pins 0.3.4's release and all WO-02 provenance, historical package 0.3.2 and
the private final2 source payload. The exact historical final2 rollback and staged
candidate live in the **external** `ars-wo02-0.3.4-20261107` bundle. Preserve its
`previous/`, `previous-packages/`, `candidate/`, `release/` and `deployment.json`.
`tools/stage-update.ps1 -Mode Verify -BundleDirectory PATH` verifies all its hashes.
The retention report records its manifest identity; it cannot delete that bundle.
Update policy pins only after the replacement candidate and rollback are accepted.

WO-06 read the installed version as 0.3.7 and additionally pins all `wo05a/`
evidence and `installer/0.3.6/` plus `installer/0.3.7/`. The policy also records
the external `ars-wo05a-0.3.6-20261107` and `ars-wo05a-0.3.7-20261107`
bundles. Preserve both complete archives: the former contains the requested
0.3.6 candidate/release and final2 rollback; the latter contains the 0.3.7
candidate/release and exact installed 0.3.6 rollback payload/packages. Read the
installed version again before any later cleanup; these pins do not imply that
0.3.7 remains current indefinitely. Immutable archives are preserved without
adding marker files inside them.

After the user approves a concrete plan, and after stopping all builds/probes that
could access candidates, an operator can run:

```powershell
# DESTRUCTIVE; not authorized or run under WO-05 Part B.
powershell -NoProfile -ExecutionPolicy Bypass -File tools/artifacts-retention.ps1 -Execute -ApprovedPlan C:/absolute/new-plan.json -ReportPath C:/absolute/new-execution.json
```

Execution requires the same root, policy hash, retention settings, candidate set,
and per-unit path/size/time fingerprint. It rechecks containment, reparse points,
tree changes and live references before each native PowerShell `Remove-Item
-LiteralPath`. It asks through `ShouldProcess`; `-WhatIf` can preview that branch.
Execution can stop after partial progress if a later unit fails. There is no
transactional undo and no atomic exclusion against unrelated writers or a process
starting between the last check and deletion. Quiescence and review are required.
Content fingerprints cover paths/lengths/times, not file content hashes. This is
developer hygiene, not adversarial deletion protection. Reports never overwrite.

## Bounded future builds

Use the new opt-in entry point instead of creating another arbitrary output root:

```powershell
./tools/build-bounded.ps1 -Kind Build       # preview, no filesystem writes
./tools/build-bounded.ps1 -Kind Publish     # preview
./tools/build-bounded.ps1 -Kind Build -Run
./tools/build-bounded.ps1 -Kind Publish -Run
```

It routes .NET intermediate/output paths into `artifacts/bounded/<kind>/<run>/`.
Publish's payload is under that run's `payload/`. No installation, launch, copying
into `out/`, or automatic cleanup occurs. Existing `.gitignore` already excludes
all of `artifacts/`; no shared file needs editing. Admission is serialized, with
two retained runs per kind by default. At the cap, builds stop until an approved
retention run frees a slot (for example a reviewed `-KeepLatest 1` plan once the
older run is seven days old). Failed runs also consume a slot. A four GiB total
admission budget blocks later builds; an individual build can exceed it, so this
is a count bound and pre/post size check, not a hard disk quota. NuGet's external
package cache is unaffected. Overrides are explicit parameters.

This route is opt-in: existing scripts and direct `dotnet` commands can still
write elsewhere. Adopting it in existing release/CI entry points requires a later
coordinated edit; WO-05B is add-only while Part A works concurrently.

`tools/test-artifacts-retention.ps1 -EvidenceDirectory NEW_PATH` creates retained
fixtures and runs dry-run safety checks only. It never executes cleanup or deletes
fixtures. WO-05B's report records the actual validation scope and limitations.
