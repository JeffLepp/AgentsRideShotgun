# ARS engine probe

This desktop executable exercises the engine and its packaged MCP bridge with two
disposable workspaces. It does not call a model, write provider configuration, inject global
keyboard/pointer input, or drive the owner's normal browser profile. It launches real local
processes and an installed Chromium browser on its own workspace desktops. Browser fixtures
are local files; Chromium background network activity is not measured.

Build from the repository root:

```powershell
dotnet build engine-probe/Deskweave.EngineProbe.csproj -c Release
```

Launch as a hidden desktop process, with a new absolute output directory each run:

```powershell
$probeExe = (Resolve-Path -LiteralPath 'engine-probe/bin/Release/net10.0-windows/Deskweave.Probe.exe').Path
$probeOutput = [IO.Path]::GetFullPath((Join-Path $PWD ('artifacts/engine-probe-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$probeProcess = Start-Process -FilePath $probeExe -ArgumentList ('"' + $probeOutput + '"') -WindowStyle Hidden -PassThru
$probeProcess.Id
```

Read `progress.log` for completed assertions and `report.json` for the final result, exact
engine/bridge paths and SHA-256 hashes, all claims, and any exception. The process returns
0 on success, 1 on a failed assertion, and 2 on invalid invocation or the four-minute
watchdog. A watchdog failure also writes `timeout.txt`; absence of a completed report is
not a pass. Fixture processes have their own five-minute ceiling as an additional cleanup
backstop. Normal teardown verifies that the workspace jobs terminate observed descendants.

The default full run also runs `WorkspaceMemoryProbe`: empty workspaces return zero;
disposed workspaces and failed job/process reads return null. Live application and browser
jobs sum to within 25% + 4 MiB of fixture processes'
independent private resident page counts (`QueryWorkingSet` on their own pseudo-handles).
The fixtures touch 32 MiB and leave another 128 MiB committed but untouched, distinguishing
private working set from private commit. The native fallback is checked directly too.
These assertions are compiled by a build; they are only verified by a completed probe run.

Production memory sampling enumerates each job with `JobObjectBasicProcessIdList` and opens
processes with only `PROCESS_QUERY_LIMITED_INFORMATION`, rechecking job membership against
PID reuse. `GetProcessMemoryInfo` / `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize` is
preferred; EX2 requires Win10/11 22H2 with September 2023 updates. Windows 10 1809+ falls
back to `NtQueryInformationProcess(ProcessVmCounters)` / `VM_COUNTERS_EX2.PrivateWorkingSetSize`
(native layout: [phnt](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntpsapi.h)).
Microsoft's [hcsshim job sampler](https://github.com/microsoft/hcsshim/blob/main/internal/jobobject/jobobject.go)
uses the same native counter with query-limited process handles.
It does not substitute private commit. Exited/inaccessible processes contribute zero; a job
that keeps outgrowing eight bounded enumeration retries is retried on the next sample.
Sampling happens on a worker at the workspace page's existing glance cadence, not each frame.

The output directory retains browser screenshots and a path to its fixture for inspection.
Workspace/access/result stores use a short `f-{id}` sibling beneath the same artifacts
directory to keep Chromium's nested profile paths short. The probe creates no user
workspace and deletes no existing workspace. Inspect the output path before manually
removing a completed fixture directory.

The browser assertions compare actual native-window captures against two local pages with
different solid colors, exercise Unicode input and tab selection, inspect the browser's
actual profile argument, and look for a renderer with a restricted lower-integrity token.
These establish the observed behavior on the tested PC. They do not certify browser
downloads, arbitrary websites/apps, hostile-code isolation, model missions, other Windows
versions, or clean-machine compatibility.

For repeatable browser gates, set `DESKWEAVE_WORKSPACE_BROWSER` to the exact installed
fixture browser and record its version. WO-05 uses Chromium 156.0.8078.4 from
`$env:LOCALAPPDATA/ms-playwright/chromium-1248/chrome-win64/chrome.exe`. With no override,
this PC selects Edge, whose Windows account sync notice can cover the fixture page;
DOM success alone does not establish that native capture shows the page. The pixel
assertions must still pass. `--browser-capture-diagnostic OUTPUT` records native
captures before/after a CDP screenshot and tab activation, plus window metadata.
It does not alter the production capture implementation or the pixel oracle.

Before claiming a published executable has these fixes, compare its engine and bridge
hashes with the passing report. Different hashes require a targeted rerun against that
payload or evidence explaining and validating the difference. The app's overview, compact
mode, focus behavior, and actual packaged launch need their separate UI gate.

For lifecycle/update work, see [the lifecycle policy](../engine/LIFECYCLE.md).
Use `--isolate-suite EXE MODE OUTPUT` to keep the suite's controller windows on
another ARS desktop as well as its fixture apps. A private SDK installation needs
`DOTNET_ROOT` set to that SDK root before launching probe apphosts. No existing
live controller is used. In Windows PowerShell, use `Start-Process -Wait -PassThru`
for either probe executable so you can read its exit code before opening its report.
Browser fixtures can select an installed clean Chromium
with the process-scoped `DESKWEAVE_WORKSPACE_BROWSER` override; Edge's first-run
sign-in dialogs can otherwise obscure the native pixel oracle. This does not
alter the owner's browser or its profile.

For storage fixtures and light/dark/card renders, use `tools/check-storage.ps1`.
`--storage-snapshot ABSOLUTE_OUTPUT` records read-only live file accounting and
scan duration; it neither cleans files nor uses a controller.
The isolated suite's host folder is kept beneath its output directory.
