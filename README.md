# Deskweave

**Agents get their own screen. You keep yours.** A native Windows app for local agent testing.

Deskweave is an independent private copy of HiveMind Agent Workspaces. Its source,
dependencies, native shell, and local publishing tools live in this folder. You can move
the whole folder to another checkout without linking back to HiveMind's source tree.

## Open the app

The current private checkout delivery is **0.2.11**, published and verified at **out/Deskweave.exe**.
Double-click **Start Deskweave.cmd** or open that executable directly. The matching private
installer is **artifacts/installer/0.2.11/Deskweave-Setup.exe**; exact evidence is in
[VALIDATION.md](VALIDATION.md).
The Windows x64 package includes the .NET desktop runtime and MCP bridge. Its installation
floor is Windows build 19041; that technical floor does not establish support for every edition.
`tools/build-installer.ps1` builds **Deskweave-Setup.exe**: a per-user install (no admin) with a
Start menu shortcut and an Apps & features uninstall that also takes Deskweave out of the agents'
configs. It is unsigned; Windows reputation and application-control policies can warn or block it.
Exact hashes and tests are in [VALIDATION.md](VALIDATION.md); [LAUNCH.md](LAUNCH.md) lists release limits.

First launch detects **Claude Code** and **Codex**. **Start** consents to connecting the enabled
agents; closing it connects nothing. Settings > Agents can connect later. Agent sessions that were
already open may need to restart or reconnect to see Deskweave. Setup finishes in the tray.
No Deskweave sign-in is required.

Deskweave does not need an API key. Claude/Codex authentication remains in those providers.
Ordinary installations need no extra profile. Changing the login within the same provider
configuration keeps the connection; Deskweave does not create accounts or copy credentials.
Connection discovery covers their default and effective configuration roots plus existing
numbered profiles `.claude1`–`.claude9` and `.codex1`–`.codex9`. These are configuration
profiles, not inferred login identities. A provider's switch applies to all detected profiles;
each target is verified separately. Arbitrary custom profile locations and other local stdio
MCP clients use the copied setup and need their own compatibility check.

**Public launch is blocked.** A new neutral Codex trial ran an invisible test window on the
owner's `Default` desktop through its ordinary shell. Workspace tools enforce their own route;
the current MCP connection does not enforce other execution paths. See the
[execution review](docs/execution-boundary-review.md) and [release decision](docs/release-readiness-2026-09-21.md).

Once connected, an agent that needs a screen while Deskweave is closed starts it in the background
(tray only). An agent that goes 30 seconds without a workspace action lets go of it, so the corner
fades; a workspace nobody uses for 15 minutes sleeps and starts again on the next agent call.
Sleep closes its open apps while retaining saved files and its last picture. Running command jobs
prevent idle cleanup until they finish. When the PC
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
  Automatic views and notifications stay hidden while a fullscreen game or video is visible on
  either monitor, even if you use a normal app on another screen. An explicit tray request can
  show a temporary glance. Minimized or covered fullscreen apps permit normal behavior again.
- **Hub:** a compact vertical strip with small running previews and text Recent rows. Click a row
  to expand its screen inline. Running screens accept input; stopped screens show their last
  picture when available. Start screen opens a new screen; Show more opens history and files.
- **Settings:** agent connections, light/dark theme, corner visibility, startup and available
  privacy controls.
- **Tray:** show the corner, pause/resume every agent, open Settings, or quit. Closing the hub
  hides it; quitting ends running desktops while retaining saved files.

The default pause shortcut is **Ctrl+Alt+P** (Settings shows the actual shortcut if Windows
already uses it). Private 0.2.11 prevents timed-out preview captures from accumulating queued
work. Its Windows 10 checks and installed Windows 11 upgrade, cold MCP, browser click/readback
and screenshot workflow pass. Defender reported no threats in app/installer scans.
The prior 0.2.10 installed Windows 11 upgrade, cold-start, browser click/readback and screenshot
run passed with the normal corner enabled.
The prior installed 0.2.9 browser/control run failed with the corner enabled, while a diagnostic
run with the corner disabled passed and restored that setting. These single-run timings are
not a benchmark. Automatic execution routing, human sign-in handoff, broader acceptance
and signed download/Store acceptance remain release gates. The managed-session integration
choice remains a product decision. The separate
[portable test kit](portable/README.md) is outside
this Windows product's delivery.

## Browser work

The engine finds installed Chrome or Edge and opens it on the workspace's Windows desktop,
using a separate browser profile. Deskweave displays real captures of that desktop. The requested
handoff for human sign-in while preserving that browser session is not yet implemented;
the owner's ordinary browser credentials are not imported. Websites and AI providers can still use the
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
what was actually exercised on the standalone build. Version **0.2.11** is a private candidate;
the working name is not a trademark or domain-clearance claim.
