# Deskweave

Linux/Mac hardware testing now has a separate [portable test kit](portable/README.md):
Linux virtual desktops and native Chromium browser workspaces behind the same local viewer.
See its [validation record](portable/VALIDATION.md) for actual platform coverage. This does
not replace the Windows app described below.

**Your agents. Room to work.** A native Windows app for local agent workspaces.

Deskweave is an independent private copy of HiveMind Agent Workspaces. Its source,
dependencies, native shell, and local publishing tools live in this folder. You can move
the whole folder to another checkout without linking back to HiveMind's source tree.

## Open the app

The private **0.2.0** package is available: double-click **Start Deskweave.cmd**, or open
**out/Deskweave.exe** directly. It includes the .NET desktop runtime and MCP bridge.
The 76-check native UI gate and actual published-app appearance/lifecycle checks passed
on the owner's PC. Exact hashes, current scope and earlier engine/browser evidence are
recorded separately in [VALIDATION.md](VALIDATION.md).

Use **Appearance** in the top toolbar to choose **Windows** (cool graphite and blue),
**Mac** (silver and light), or **Linux** (graphite and mint). Your choice is saved; Windows
is the default on this PC. Mac moves the existing window controls to the left as traffic
lights; Windows and Linux keep them on the right. All three looks use the same Windows
workspace engine and preserve running work when switched.

**New workspace** creates one and starts its computer. To let an outside agent in, click
the link icon in the rail and connect **Claude Code** or **Codex** (or copy the setup for any
other MCP client). A connected agent gets a workspace the first time it uses one:

- a workspace kept for its project folder, if there is one;
- else one kept for that agent (**Only Claude Code**, **Only Codex**);
- else a shared one (**Any agent**), the idle one first;
- else a new workspace named after its folder and kept for it, so the next session and any
  other agent working there land in the same place.

Right-click a tile and choose **Agents** to change this. **Just me** keeps outside agents
out. Several agents in one workspace take turns; one that goes quiet for 30 seconds hands
over. Clicking a workspace's screen pauses whichever agent is working; it carries on 20
seconds after you stop. At most one running workspace per 3 GB of memory (2 to 10) is started
for agents; past that they are told to ask you. Nothing is asked at first run and nothing
needs a sign-in; the built-in Claude supervisor keeps its own setup.

- **Overview:** see actual workspaces, running state, attention, and desktop previews.
- **Workspace actions:** rename a tile, open its folder, or delete it. Deletion asks for
  confirmation and stops that workspace's desktop before removing its saved files.
- **Focus:** open a workspace's desktop, conversation, controls, and connection settings.
- **Compact:** keep the monitor beside another app. Collapse it to a small bar, or pin it
  on top. Expanding returns to the same workspaces.
- **Window memory:** full and compact positions, size, pin state, view mode, and selected
  workspace are saved and restored.
- **Close:** hide to the notification area and keep work running. **Quit Deskweave** in
  the tray ends this app and its running desktops; saved files remain.

Keyboard: **Ctrl+N** creates, **Ctrl+F** searches workspaces, **Ctrl+1** opens overview,
**Ctrl+2** opens the selected workspace, **Ctrl+Shift+M** switches compact mode, and
**F1** opens help. The optional engine corner view has a separate **Ctrl+Alt+D** shortcut
when enabled.

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
