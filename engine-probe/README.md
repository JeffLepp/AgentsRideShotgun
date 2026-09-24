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
$deskweaveProbeExe = (Resolve-Path -LiteralPath 'engine-probe/bin/Release/net10.0-windows/Deskweave.Probe.exe').Path
$deskweaveProbeOutput = [IO.Path]::GetFullPath((Join-Path $PWD ('artifacts/engine-probe-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
$deskweaveProbeProcess = Start-Process -FilePath $deskweaveProbeExe -ArgumentList ('"' + $deskweaveProbeOutput + '"') -WindowStyle Hidden -PassThru
$deskweaveProbeProcess.Id
```

Read `progress.log` for completed assertions and `report.json` for the final result, exact
engine/bridge paths and SHA-256 hashes, all claims, and any exception. The process returns
0 on success, 1 on a failed assertion, and 2 on invalid invocation or the four-minute
watchdog. A watchdog failure also writes `timeout.txt`; absence of a completed report is
not a pass. Fixture processes have their own five-minute ceiling as an additional cleanup
backstop. Normal teardown verifies that the workspace jobs terminate observed descendants.

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

Before claiming a published executable has these fixes, compare its engine and bridge
hashes with the passing report. Different hashes require a targeted rerun against that
payload or evidence explaining and validating the difference. The app's overview, compact
mode, focus behavior, and actual packaged launch need their separate UI gate.
