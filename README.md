# Deskweave

**Agents get their own screen. You keep yours.** A native Windows app for local agent testing.

Deskweave is an independent private copy of HiveMind Agent Workspaces. Its source,
dependencies, native shell, and local publishing tools live in this folder. You can move
the whole folder to another checkout without linking back to HiveMind's source tree.

## Open the app

The private **0.2.0** package is available: double-click **Start Deskweave.cmd**, or open
**out/Deskweave.exe** directly. It includes the .NET desktop runtime and MCP bridge.
`tools/build-installer.ps1` builds **Deskweave-Setup.exe**: a per-user install (no admin) with a
Start menu shortcut and an Apps & features uninstall that also takes Deskweave out of the agents'
configs. It is unsigned for now, so Windows SmartScreen warns on first open.
Exact package hashes, test results and remaining MVP gates are in [VALIDATION.md](VALIDATION.md).

First launch detects **Claude Code** and **Codex**. **Start** consents to connecting the enabled
agents; closing it connects nothing. Settings > Agents can connect later. Agent sessions that were
already open need a restart to see Deskweave. No Deskweave sign-in is required.

Once connected, an agent that needs a screen while Deskweave is closed starts it in the background
(tray only). An agent that goes 30 seconds without a workspace action lets go of it, so the corner
fades; a workspace nobody uses for 30 minutes sleeps and wakes on the next agent call. When the PC
is at its limit, the quietest workspace sleeps to make room, or the call waits its turn.

A connected agent's first workspace tool call creates or reuses its project's workspace.
Subfolders of a repository share it; nested repositories and Git worktrees get their own.
Without a repository, Deskweave recognizes common project manifests. Work outside a project
goes to one **Scratch** workspace. Connecting or listing tools starts no computer.
Private workspaces and old records are retained, never reassigned automatically.

The connection tells agents to use Deskweave for their own browser/GUI work and app testing.
An explicit request to open a page for you in your own browser stays a desktop request through
the agent's normal approved tools. Code, builds, unit tests and ordinary files stay in those
tools too. These are connected-agent instructions, not interception of arbitrary programs;
real model adoption is checked with `tools/check-phase-in.ps1` (Claude Code and Codex both chose
Deskweave for screen work and stayed out of it otherwise, 4/4 each; see VALIDATION.md).

- **Corner window:** appears for activity, fades when quiet, and stays while hovered or pinned.
  Move it by its name pill and resize from its edges. Click the screen to use it while the
  agent waits. The preview stays on the same workspace while your pointer is over it.
- **Hub:** shows working and recent workspaces. Open one for its screen, history and files.
- **Settings:** agent connections, light/dark theme, corner visibility, startup and available
  privacy controls.
- **Tray:** show the corner, pause/resume every agent, open Settings, or quit. Closing the hub
  hides it; quitting ends running desktops while retaining saved files.

The default pause shortcut is **Ctrl+Alt+P** (Settings shows the actual shortcut if Windows
already uses it). Sleep/wake, waiting at capacity, complete history/accounts behavior, and
final accessibility and scaling acceptance remain on the MVP plan. This is a private build,
not a signed public installer. The separate [portable test kit](portable/README.md) is outside
this Windows product's delivery.

## Browser work

The engine finds installed Chrome or Edge and opens it on the workspace's Windows desktop,
using a separate browser profile. Deskweave displays real captures of that desktop. Sign in
within the workspace when a website needs it. Websites and AI providers can still use the
network. Normal Windows file permissions apply; a separate desktop is not a VM or a security
sandbox.

## Build a private copy

Development requires Windows x64 and the .NET 10 SDK. From this folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish.ps1 -Launch
```

The script refuses to publish while any Deskweave process is running in the current Windows
session, including a debug build or a copy in another folder. It publishes into fresh staging
and checks required app, runtime, and bridge files before replacing `out/`. The previous
package is retained under `artifacts/previous/`. If replacement fails with the output absent,
the script restores the previous package. A build or payload-check failure leaves the existing
package in place.

This workflow changes no HiveMind installation, stock catalog, feed, development registry,
startup setting, or provider connection. Runtime packages are restored during development;
the published app carries its runtime. A clean-PC public installer is a separate release task.

## Data and the HiveMind relationship

Deskweave owns `%LOCALAPPDATA%\Deskweave\` and `%APPDATA%\Deskweave\`. HiveMind workspaces
are not imported or opened automatically. Existing provider-owned login locations remain
provider-owned. The copy has distinct desktop, bridge, pipe, MCP, and application identities.

Read [BACKPORTS.md](BACKPORTS.md) before syncing engine changes. The per-file origin and
SHA-256 baseline are in [upstream-baseline.json](upstream-baseline.json). To compare both
sides against that baseline without copying or modifying anything:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\compare-upstream.ps1 -HiveMindRoot C:\path\to\HiveMind
```

[PRODUCT.md](PRODUCT.md) defines scope and limits. [DESIGN.md](DESIGN.md) records the
brand, interface system, and competitor research. [VALIDATION.md](VALIDATION.md) records
what was actually exercised on the standalone build. Version **0.2.0** is a private preview;
the working name is not a trademark or domain-clearance claim.
