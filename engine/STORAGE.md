# Storage in ARS

Choose **Storage** in the tray or **Settings → Storage** in the hub. The total
counts logical file lengths in ARS's Local/Roaming data and installed `ARSApp`
folders, once per path. It includes each workspace's size, last use and running
state, browser profiles separately from caches, captures, logs, temp, update
packages, old payloads, detached bridge hosts and metadata. Project folders and
the owner's shared browser are outside this total. Developer build artifacts
are also outside it. Links are skipped and unreadable paths produce warnings;
the figure is not allocated disk space, deduplicated hard-link usage or an
instantaneous filesystem snapshot.

The Storage page opens with one **Measure storage** button. A measurement takes
about a second (38k files, 6.9 GB measured 2026-11-21). It shows the total and
four rows, each with its size and a **Remove** link:

| Row | Counts | Remove deletes |
| --- | --- | --- |
| Browser sign-ins | `browser-profiles` | `browser-profiles` (asks once inline) |
| Caches, logs and temp files | `caches`, `logs`, `temp` | all three, no question |
| Recordings and workspace files | `recorder-runs`, `captures`, `workspace-data` | `recorder-runs`, `captures` (asks once inline) |
| App and updates | `current`, `packages`, `versions`, `bridge-hosts`, `settings` | old `packages`, `versions` |

Rows holding user data turn **Remove** into **Confirm** with a one-line
warning. Nothing else asks: there is no file preview. The row then reports the
freed size, or why some files stayed (a running workspace). **Automatic cleanup**
is the one submenu: an on/off switch and the recording limit. Delete a whole
workspace from its right-click menu. The age-based `workspaces` category stays
in the CLI. Starting
a workspace invalidates its eligibility: cleanup holds the same admission lock
as startup and never stops a runtime. Live connection tickets also protect
workspaces held by another controller. Other Windows sessions using this
installation defer app cleanup. The current install is never a candidate, and
reparse points and paths beneath them are refused.

Browser cache deletion is limited to Chromium `Cache`, `Code Cache`, `GPUCache`,
`DawnCache`, `GrShaderCache` and `ShaderCache` at the profile root or directly
under `Default` / `Profile N`. Cookies, passwords, Sessions, Local Storage,
IndexedDB, service-worker storage and all other profile files are user data.
**Browser sign-ins → Remove** clears that data while keeping the workspace.
Recordings remove clears each stopped workspace's `recorder-runs/` folder,
including recordings and associated run files. Under **Automatic cleanup**, the
recording limit defaults to **Keep all**.
When automatic cleanup is on, a finite limit prunes the oldest runs from stopped
workspaces until the aggregate recorded size fits or only protected runs remain.
Running workspaces can temporarily exceed this limit. Workspace age comes from its saved `LastUsed`, never
the newest cache file. Missing/corrupt age metadata prevents age-based deletion.

Automatic cleanup is enabled by default and runs at startup and hourly. It
keeps each stopped workspace's browser caches below 256 MiB and logs below
64 MiB, oldest files first, and expires logs after 30 days. Captures expire after
7 days and have a 1 GiB cap; temporary files expire after 7 days. There is no
automatic workspace or saved-profile deletion. The Storage page exposes the
on/off switch and the recording limit. Cache, log and age limits keep these
defaults unless edited in the policy file. They persist in `%LOCALAPPDATA%/ARS/storage-policy.json`. The last automatic
cleanup receipt is `storage-last-retention.json`. Active producers still own
their normal screenshot-writing limits.

After a successful version change, automatic retention prunes old full update
packages, keeping the installed version, the newest older rollback and every
newer pending package. Cleanup shares the update-download lock. Explicit old
payload cleanup removes only version-labelled `app-X.Y.Z` trees older than the
newest rollback. Unlabelled `.private-retained-*` archives remain protected,
because ARS cannot infer their recovery value. Bridge hosts are report-only:
an older host can still be referenced by a connected agent configuration.

## Authenticated CLI and agents

```powershell
$ars = "$env:LOCALAPPDATA/ARSApp/current/ARS.exe"
& $ars --storage | Out-String
& $ars --storage-clean caches --dry-run | Out-String
& $ars --storage-clean caches | Out-String
& $ars --storage-open | Out-String
```

`--storage` returns a JSON report. Cleanup categories are `caches`, `logs`,
`captures`, `temp`, `browser-profiles`, `recorder-runs`, `workspaces` (age-based),
`packages`, `versions`, and `retention` (apply
the safe retention limits once). `--dry-run` selects without deleting; omitting
it is an explicit deletion request. Workspace deletion includes saved browser
profiles: inspect its dry run first. Result JSON includes preview/freed bytes,
paths, skipped workspaces/files and errors. Exit 0 means the request completed
without errors; skipped/protected files are reported separately. Exit 1 means a
transport, inspection or cleanup error; invalid CLI syntax returns 2.

These commands use the existing same-user pipe capability, reject workspace
processes like the lifecycle commands, and neither start a missing controller
nor create a workspace. `--storage-open` explicitly shows the hub's Storage page
without activating it or injecting input. MCP's read-only `storage` tool reports
usage without acquiring control or creating a workspace; refresh MCP tools to
discover it. Cleanup is deliberately restricted to the CLI and owner UI.

## Verification

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-storage.ps1`.
It discovers the installed SDK in this process, builds with warnings as errors,
and runs disposable accounting, safety, policy, authentication and UI-render
fixtures on a private ARS desktop. Receipts and light/dark/card screenshots go
in its new output directory. It changes no owner workspace/profile or install.
Installation still uses the [guarded release and rollback workflow](LIFECYCLE.md).
