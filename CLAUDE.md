# ARS (Deskweave repo)

.NET 10 Windows app. Version lives in `Directory.Build.props`.

- `app/ARS.csproj`: tray/controller UI (WPF)
- `engine/Deskweave.Engine.csproj`: workspace engine and MCP bridge (`engine/Bridge`)
- `engine-probe/`: engine and bridge regression probe. See `engine-probe/README.md` for how to build, launch it hidden and read `report.json`.
- `ui-probe/`: UI gate, run as `Deskweave.UiProbe <out> | --mvp <out> [scene] | --browser-ui | --corner-rendering | --corner-docking | --storage | --lifecycle | --scaling <out>`

## Build and test
- Build one project: `dotnet build <project>.csproj -c Release`
- Bounded dev build: `tools/build-bounded.ps1 [-Kind Build|Publish] -Run` (it only previews unless you pass `-Run`)
- Live checks: `tools/check-live-*.ps1`. Storage fixtures: `tools/check-storage.ps1`
- Give every probe run a new absolute output folder under `artifacts/`. A run with no completed report has not passed.

## Policy docs
- Release and WinGet steps: `tools/RELEASING.md`. Never overwrite a published version or installer.
- Lifecycle and updates: `engine/LIFECYCLE.md`. Storage: `engine/STORAGE.md`. Minecraft: `engine/MINECRAFT.md`.
- Artifact cleanup: `tools/ARTIFACTS-RETENTION.md`
