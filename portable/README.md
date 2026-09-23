# Deskweave portable test kit

One private agent workspace, with a quiet viewer and Windows, Mac and Linux appearances.
This is a working development kit for hardware testing. It is separate from Deskweave's
installed Windows app and is not a signed Mac application or Linux package.

| Mode | Runs on | Workspace | Extra VM required? |
|---|---|---|---|
| Linux desktop | Linux | Dedicated X display, Openbox, terminal, optional Chromium | No |
| Browser | Mac, Linux, Windows | Private Chrome/Chromium/Edge profile and web pages | No |
| Container desktop | Existing Docker/Podman Linux runtime | The Linux desktop above | On Mac/Windows, the runtime uses a Linux VM |

Start with one workspace on the 4 GB laptop. Desktop mode starts without a browser. Browser
mode starts one private browser when you click Start. The viewer uses your existing browser;
opening the viewer alone starts no workspace or model. Appearance defaults to the host OS
and can be changed independently. A Mac appearance does not turn Linux applications into Mac apps.

## Linux laptop: first test

Requires Python 3.10 or newer. On Debian/Ubuntu with TigerVNC packages available:

```sh
sudo apt-get update
sudo apt-get install --no-install-recommends ca-certificates python3 python3-venv tigervnc-standalone-server tigervnc-tools xauth xdotool openbox xterm xfonts-base fonts-dejavu-core libxcb1
cd Deskweave-Portable
sh scripts/setup.sh
sh start.sh --backend desktop
```

Install Chromium separately if you want browser work (`chromium chromium-sandbox` on Debian).
Preserve the browser's sandbox package. Package names and Chromium packaging
vary by distribution; Ubuntu's Chromium may be a Snap requiring additional environment
support. `.venv/bin/python -m deskweave doctor --backend desktop` reports missing tools and XCB capture support; it does not
install or start anything. Other distributions need equivalents of the packages above.
The actual distro is still needed to confirm its install path.

The private viewer opens locally. Click **Start workspace**, then **Take control** and
**Terminal**. Type a small command, release control, and try an agent. Stop retains files.
Keep the launcher running; Ctrl+C stops the broker and owned workspace.

## Mac: first test without a VM

Install Python 3.10+ and Chrome, Chromium, Edge, Brave or Vivaldi if none are present. From
Terminal in the extracted folder:

```sh
sh scripts/setup.sh
sh start.sh --backend browser --scale 2
```

Three things a clean Mac does on the first run, none of them a fault in the kit:

- **The zip is quarantined.** A kit that arrived through a browser, AirDrop or Messages
  carries `com.apple.quarantine`, so double-clicking `Start Deskweave.command` is refused as
  coming from an unidentified developer. Either right-click it and choose **Open** once, or
  run `xattr -dr com.apple.quarantine .` in the extracted folder. A kit copied with `scp` or
  `git clone` is not quarantined.
- **`python3` may be a stub.** macOS ships no Python; `/usr/bin/python3` is a placeholder that
  offers to install the Command Line Tools, roughly a gigabyte. Accept it, or install Python
  from python.org or Homebrew and re-run setup.
- **The firewall may ask.** If the macOS firewall is set to block incoming connections it can
  prompt about `python3`. The broker listens on `127.0.0.1` only; declining is safe and the
  viewer still works.

`--scale 2` is what makes the frame sharp on a Retina display: without it the viewer upscales
a 1024x640 image and text goes soft. It costs about four times the pixels per frame, so leave
it at 1 on a small laptop.

After setup, `Start Deskweave.command` is the same launcher. Browser mode controls web
content. It does not control Finder, native Mac applications, or a graphical terminal.
Take control to use the URL bar, click, drag or type inside the workspace. The owner's existing
browser profile is not reused. Apple Silicon and Intel detection paths are implemented;
native Mac hardware validation remains outstanding.

Browser downloads are directed to the workspace's `files/downloads/` folder, retained on
Stop and never automatically opened. There is no download manager and no tab strip; this
viewer owns one page, so a link that asks for a new tab and `window.open` both open in that
same page instead of disappearing into a window nobody is watching. Back, forward and reload
are next to the address bar. The download policy must be accepted by Chromium before startup
succeeds.

Linux can also use `--backend browser`. Windows can run `start.ps1` after creating `.venv`
and installing `requirements.txt`; the native Windows Deskweave app remains the normal
Windows product. `--browser-path` selects a particular installed Chromium executable.

## Mac app (local build)

`mac/` packages the same broker and viewer as an ordinary Mac application: its own window
and Dock icon, no Terminal or separate browser tab. It runs browser mode at Retina scale.
Closing the window or quitting stops the workspace and its browser; files are kept.

Build on a Mac, because PyInstaller cannot build a Mac app from Windows or Linux. Use Python
3.10+ from python.org or Homebrew; Apple's Command Line Tools Python is too old:

```sh
sh mac/build.sh
open dist/mac/Deskweave.app
```

The build is unsigned. It opens normally on the Mac that built it. Copied to another Mac,
the first launch is blocked: allow it under System Settings > Privacy & Security > Open
Anyway, or run `xattr -dr com.apple.quarantine Deskweave.app`. The app shares the kit's data
folder and `connection.json`, so only one of them runs at a time; a second shows the reason
in its window. For an MCP agent, use the app itself as the command:

```json
{ "command": "/Applications/Deskweave.app/Contents/MacOS/Deskweave", "args": ["mcp"] }
```

To try source changes without rebuilding: `PYTHONPATH=. .build-venv/bin/python mac/app.py`.
The app shell has only been exercised through the same code on Windows; Mac validation is
outstanding.

## Viewer and agent control

- **Take control / Release control:** explicit handoff; agents cannot acquire while you own control.
- **Agent access:** off by default. Enable it in Details to allow workspace screenshots and
  actions through the local MCP bridge. Linux desktop also supports same-user commands;
  browser mode is web-only. Each bridge has its own exclusive, renewable lease.
- **Compact / Collapse:** reduces viewer chrome or hides the viewer. A collapsed/hidden
  viewer stops requesting frames/disconnects RFB; the workspace keeps running.
- **Stop:** closes owned processes and retains `files/` and the private browser profile.
  It turns off agent access so an agent cannot immediately restart the stopped workspace.
  Closing a viewer tab alone does not stop the workspace.

Takeover revokes queued and future agent input immediately. An action already accepted by
the runtime may finish (commands have a 60-second maximum); Details shows the active action.
Do not interpret takeover as undoing a command that has already begun.

Native modes run with your normal OS account permissions. A private display/profile prevents
ordinary input collisions; **it is not a filesystem sandbox for hostile code**. Agent access
on the Linux desktop includes local command execution. Workspace children receive a reduced environment and a
private working directory, but those measures do not remove account filesystem permissions.
VNC input is also gated at the display server; hiding an input button is not the enforcement.
Clipboard sharing is disabled, so text cannot be copied back out of the workspace; pasting in
works. Desktop agent text currently accepts printable ASCII; it rejects unsupported text before
typing. Browser agent text supports Unicode. Browser viewer drag is a left-button press, move
and release, which is enough for selecting text and moving a slider but is not drag-and-drop
between windows. Audio, a downloads UI and arbitrary native application control are not
implemented.

### Connect an MCP agent

The launcher writes two private files in its data folder without printing credentials:
`connection.json` holds the owner token for `open`, `shutdown` and `sample`, and
`agent-connection.json` holds only what the MCP bridge needs. Give agents the second one;
the bridge runs as the agent, so it never sees the owner token. The workspace itself lives
in the `workspace/` subfolder, outside the folder that holds the owner file. Default data folders:

| Host | Data folder |
|---|---|
| Linux | `~/.local/share/deskweave-portable/` (respects `XDG_DATA_HOME`) |
| Mac | `~/Library/Application Support/Deskweave Portable/` |
| Windows | `%LOCALAPPDATA%/DeskweavePortable/` |

Configure the agent's stdio MCP server with an absolute interpreter path, this folder as
working directory, and these arguments:

```json
{
  "command": "/absolute/path/Deskweave-Portable/.venv/bin/python",
  "args": ["-m", "deskweave", "mcp", "--connection", "/absolute/path/to/agent-connection.json"],
  "cwd": "/absolute/path/Deskweave-Portable"
}
```

No model provider or account is built in. The bridge supports the initialize-based MCP
revisions through 2025-11-25. It advertises status, acquire/release, start, screenshot, launch,
navigate and input; Linux desktop mode also advertises bounded argv commands. The owner must
enable access in the viewer first. Browser mode does not expose arbitrary command execution.
Do not share connection files or include them in test reports. For a closed viewer, run
`.venv/bin/python -m deskweave open` to reopen an authenticated local link.
Use `.venv/bin/python -m deskweave shutdown` to close the broker and owned workspace gracefully.

## Optional Linux container on a Mac or Windows PC

Use an already-installed, running Docker or Podman runtime. This helper does not install
virtualization software or change its settings:

```sh
python3 scripts/container.py start
python3 scripts/container.py status
python3 scripts/container.py stop
```

Use `--runtime podman` if needed. The helper builds a Linux image and owns only the named
`deskweave-portable-pilot` container and `deskweave-portable-data` volume. It publishes one
random port on `127.0.0.1`, caps container memory at 1024 MiB, and mounts no host home,
desktop socket, or browser profile. The private host MCP connection lives in
`.state/container-connection.json` and holds only the URL and agent token. The volume persists after Stop. Docker/Podman VM memory
and the viewer are additional costs beyond the container limit. Chromium retains its
normal sandbox; hosts that block it must report the failure, not disable it to get a pass.
The helper uses a pinned official Playwright seccomp profile that allows Chromium to create
its sandbox namespaces. Its upstream source and exact policy comparison are recorded under
`docker/seccomp/`. This is an explicit per-container policy, not the Docker daemon's default;
it adds no `SYS_ADMIN` capability or privileged mode. A named Linux volume avoids Chromium
profile failures observed on Windows-backed bind mounts.

## Collect the first hardware result

Record your OS/version, CPU, RAM, desktop session and browser version. Start with no other
Deskweave workspace or local model. In the kit folder (using `.venv/bin/python`):

```sh
.venv/bin/python -m deskweave doctor --backend desktop
.venv/bin/python -m deskweave sample --seconds 30 --output artifacts/idle.json
```

Use `--backend browser` for Mac. Sample once before Start, once with the workspace idle,
and once with a representative browser page. Rename output files each time. These samples
count workspace processes only. Also record system memory and responsiveness in the host's
system monitor; process RSS can double-count shared pages. A container's cgroup memory is
a different measurement. Record startup-to-first-frame, typing latency, collapse behavior,
and whether Stop returns memory and leaves no workspace processes. The laptop and Mac
results determine the next defaults; no “fastest” or “smooth on 4 GB” claim is made yet.

Implementation decisions and upstream sources: [RESEARCH.md](RESEARCH.md). Actual executed
checks and limits: [VALIDATION.md](VALIDATION.md). Private packaging:
`.venv/bin/python scripts/package.py`. It checks viewer hashes and excludes credentials,
profiles, virtual environments and test artifacts.
