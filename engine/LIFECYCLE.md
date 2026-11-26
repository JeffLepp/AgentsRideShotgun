# Controller lifecycle and updates

## HiveMind shared-controller integration (0.3.10)

HiveMind can bundle this same controller and launch it with `--hivemind`. It uses
the existing per-user singleton and canonical ARS workspace/profile paths. An
ordinary standalone launch shows that controller rather than creating a second
one. Hosted startup skips Velopack, updater, Windows startup registration and
automatic provider-configuration maintenance. Hosted Settings identifies Windows
startup as managed by HiveMind and cannot write the ARS Run key.

To review a newer standalone build, save workspace work and use **Quit ARS** in
the tray before running the standalone installer. Open ARS from its normal
shortcut before opening Computer Workspaces in HiveMind. HiveMind attaches to
that controller when it is already running.

Authenticated owner clients use `deskweave/owner` protocol 1 for snapshot, show,
private frame and external-client admission. Admission is checked against the
canonical controller's actual workspace jobs; relays supply the kernel pipe peer
PID, not a tool argument. Closing a client does not quit the controller. Existing
MCP framing/API remains compatible; dispatched tool calls are never replayed by
the HiveMind relay after a broken session.

This source change does not merge old HiveMind-only browser profiles or require
standalone ARS installation for a bundled HiveMind runtime. The focused owner,
router and bridge probes validate isolated fixtures; signed release and real
service/account breadth remain separate acceptance work.

ARS 0.3.4 adds two tray actions: **Restart when workspaces stop** and **Install
update when workspaces stop**. Restart/update requests remain queued while any
workspace is running, including an idle workspace. Save work in its apps, close
them, then stop the workspace in the hub. The request retries every two seconds.
No restart/update action closes a workspace or asks its apps to discard work.
The existing workspace idle-sleep policy is independent and is not accelerated
by a pending update. Explicit **Quit ARS** still asks for confirmation and now
warns that unsaved app work is lost.

The same operations are available without showing or activating the hub:

```powershell
$ars = "$env:LOCALAPPDATA/ARSApp/current/ARS.exe"
& $ars --update-status | Out-String
& $ars --restart | Out-String
& $ars --update | Out-String
# A reviewed local Velopack feed, with releases.win.json and its packages:
& $ars --update --source 'C:/absolute/path/to/feed' | Out-String
```

These commands connect to the existing authenticated, same-user router. They
never launch a missing controller, create a workspace, or activate owner UI.
Status explains a deferral or a failed operation. An accepted restart returns 0;
a deferred request returns 1 but **remains queued**. `--update-status` returns 0
for a readable status. A second request does not replace an existing request.
Once accepted, the restart is committed; it cannot be cancelled through status.

An atomic maintenance gate shared with workspace creation closes the race
between checking for an empty controller and exiting. Background downloads are
serialized separately, so a network update check cannot block a plain restart.
Update downloads and package checksums use Velopack 1.2.0. Explicit local feeds
select their own target; they do not substitute an unrelated pending package.
The controller calls `WaitExitThenApplyUpdates` with that exact asset, then exits
through WPF cleanup. The updater restarts ARS in the tray with `--background`.
Plain restarts wait on the old process identity before opening the replacement
in the tray. A late explicit Quit cancels that plain restart.

Ticket repair serializes its read/check/publication with cooperating publishers,
conditional withdrawals, and stale-ticket cleanup using a retained per-ticket
file lock. This prevents repair from overwriting another controller's newly
published live ticket. Explicit owner reset/uninstall retains its prior
unconditional deletion semantics. Existing controllers in other Windows sessions
defer update/auto-apply; simultaneous startup of a new session during updater
handoff has not been validated as an atomic exclusion boundary.
If Windows denies even limited process inspection, maintenance stays deferred:
ARS cannot prove that the inaccessible process is unrelated to this install.
This can also defer cleanup on a multi-user PC. Close the other ARS sessions or
sign them out before maintenance; ARS does not bypass an unknown live controller.

## Bridge lifetime and replacement

A bridge lives exactly as long as its MCP client. It ends only on stdin EOF, a
failed client stream, or the death of its immediate MCP parent (watched through the
parent's actual process handle, including creation time to reject PID reuse). The
parent handle requests only synchronization and limited process information. If
that handle cannot be opened, a diagnostic is written to stderr and stdin EOF
still ends the bridge. Disconnect permits a 250 ms drain, then ends the process
even if the controller is blocked. Parent death exits immediately.

Nothing on the ARS side ends a bridge: not owner Quit, restart, update or crash,
not a pipe error, not ARS being unreachable when the bridge starts. An oversized
request (over 1 MB), a request past the 16-request backlog, or an unexpected
failure while forwarding is answered for that one request id (an `isError` tool
result for `tools/call`, a JSON-RPC error otherwise). While ARS cannot be reached
the bridge answers `initialize` and `tools/list` from its built-in copy of the
router's replies (`engine/Bridge/offline.json`), sends
`notifications/tools/list_changed` once the real list is back, and answers each
tool call with `isError` text:

- No quit mark (restarting, crashed, updating, not yet started): "ARS is
  restarting. Retry this call in a few seconds." A tool call may start the
  canonical install in the tray (`ARS.exe --background`) as before; the next call
  reconnects and the router wakes a stopped or slept workspace on its first call.
- Owner quit mark present: "The owner turned ARS off, so it will not come back on
  its own. Ask the user before doing this work another way." The bridge never
  starts ARS then, but stays connected; when the owner opens ARS again (which
  clears the mark) the next call reaches it without an MCP refresh.

No text invites the agent to do workspace work with its own tools. Uninstall
writes the quit mark and checks connection ownership without materializing
another bridge payload; bridges still attached keep their sessions until their
clients exit and answer with the turned-off text.
On the ARS side, a timeout or other failure inside one tool that is not the
caller's own cancel fails that call only; the pipe connection stays up, and a
request past 16 waiting on one connection is refused in order instead of closing it.

Product-managed MCP configurations now point to an immutable, content-addressed
self-contained bridge under `%LOCALAPPDATA%/ARS/bridge-hosts/`. The app verifies
the complete copied runtime before publishing its path. These bridges hold no
installed `current/Bridge` binaries open. They retain their MCP transports while
the controller restarts, reread the ticket, and replay initialization/context
on the next request. Interrupted calls receive an error; side-effecting tool
calls are never automatically replayed. Future controllers must retain protocol
compatibility with bridges already connected. Old cached hosts are retained so
other configured clients continue working; each distinct runtime costs about
80 MB in this build. Automatic cache garbage collection is not implemented.

`ARS.exe --bridge-path` prints/materializes this install's verified detached host
without starting WPF or changing agent settings. A controller locator in the host
preserves canonical-router autostart; nonstandard `background-router.json` stays
attach-only. Existing external guarded launchers with a hardcoded
`current/Bridge/ARS.WorkspaceBridge.exe` must migrate their `--bridge` argument
to that detached path once. Until migrated/reconnected, the updater defers with
an explicit legacy-bridge status; it never terminates an MCP client to get a lock.
Do not remove the launcher's workspace, profile, owner-input, or attach guards.

## Release and rollback preparation

Use a separately published payload and the version-matched Velopack package.
`tools/build-installer.ps1 -SkipPublish -PayloadDirectory PATH` packages it without
touching `out/` or the installed app. `tools/stage-update.ps1 -Mode Prepare` takes
`CandidateDirectory`, `ReleaseDirectory`, `BundleDirectory`, and optionally
`InstallRoot`. It archives the published candidate, Velopack release, complete
previous `current/` payload, and previous packages. `-Mode Verify` independently
checks their complete file/hash manifests. Neither operation changes installed
files or processes. The candidate is a new release, with its own package metadata;
do not use the older private same-version swap procedure for routine updates.

The initial 0.3.2-to-0.3.4 migration needs the old controller and old installed
bridges closed **after their work is saved**, because the old app lacks these
commands. Run the archived `release/ARS-Setup.exe` through Velopack once. Later
updates use the app's tray/CLI path. Retained previous payload/packages permit a
cold rollback; preserve the failed install before recovery and validate hashes.
Workspace/profile data lives separately and is not part of the payload archive.

## Verification boundary

`Deskweave.Probe --bridge-lifetime OUTPUT` tests EOF during a live connection and
a stalled handshake, parent death with stdin held by a different process, a bridge
surviving owner quit and reconnecting when ARS reopens, restart/closed-at-startup
answers, oversized and backlogged requests, the offline copy matching the router,
detached-host source locks, maintenance admission, and ticket reconnection.
After changing the tools or `RouterInstructions`, regenerate the bridge's copy with
`Deskweave.Probe --bridge-offline-json ABSOLUTE_PATH/engine/Bridge/offline.json`.
The existing engine router tests also run. The Minecraft preview contract suite
remains `--minecraft-preview-checks OUTPUT` (13 checks).

`--isolate-suite EXE MODE OUTPUT` runs engine/UI suites on a new private ARS
desktop (`MODE` is `full` or a probe mode such as `--lifecycle`). It never switches
the input desktop. Pop-out tests simulate their controller on that private host;
they do not certify the real owner desktop. Native Explorer tray integration is
explicitly skipped and recorded in `skipped.log` in isolated UI runs. Real
installed updates/restarts and native tray behavior require the post-WO-01 gate.

Velopack API contract: https://docs.velopack.io/reference/cs/Velopack/UpdateManager
