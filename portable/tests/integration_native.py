"""Linux-only, no-model fixture gate; run in an isolated explicitly named container.

Evidence is retained under --output. --workspace-root can keep runtime/profile
files on Linux storage when --output is a host bind mount. No host display, home,
browser profile, arbitrary process or external website is touched by this test.
"""
from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path
import platform
import sys
import time

from PIL import Image
import psutil

from deskweave.native import NativeWorkspace, _live, dependency_status


def main(output: Path, workspace_root: Path | None = None, skip_browser: bool = False) -> int:
    output.mkdir(parents=True, exist_ok=True)
    workspace = NativeWorkspace(workspace_root or output / "workspace")
    claims: list[str] = []
    metrics: dict = {}
    observed: dict[int, psutil.Process] = {}
    failure = None

    def check(condition: bool, text: str) -> None:
        if not condition:
            raise AssertionError(text)
        claims.append(text)
        print("PASS " + text, flush=True)

    def wait(predicate, seconds: float = 8):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            result = predicate()
            if result:
                return result
            time.sleep(0.1)
        raise AssertionError("Fixture condition did not complete within its bound.")

    def children() -> list[psutil.Process]:
        result = []
        for parent in workspace._processes:
            if parent.poll() is None:
                try:
                    owner = psutil.Process(parent.pid)
                    result.extend([owner, *owner.children(recursive=True)])
                except psutil.NoSuchProcess:
                    pass
        for process in result:
            observed[process.pid] = process
        return result

    def measure(label: str) -> None:
        current = workspace.status()
        current["observedAt"] = time.time()
        for name in ("memory.current", "memory.peak", "memory.max", "pids.current"):
            path = Path("/sys/fs/cgroup") / name
            if path.exists():
                current["cgroup_" + name] = path.read_text().strip()
        metrics[label] = current
        children()

    def run(argv: list[str]) -> str:
        result = workspace.run(argv)
        if result["exitCode"] != 0 or result["timedOut"]:
            raise AssertionError("Fixture command failed: " + result["stderr"][-1500:])
        return result["stdout"].strip()

    def window(class_name: str) -> str | None:
        result = workspace.run(["xdotool", "search", "--onlyvisible", "--class", class_name], timeout=2)
        return result["stdout"].splitlines()[-1] if result["exitCode"] == 0 and result["stdout"].strip() else None

    def capture(name: str) -> Image.Image:
        png = workspace.screenshot()
        (output / name).write_bytes(png)
        image = Image.open(io.BytesIO(png)).convert("RGB")
        check(image.size == (1024, 640), name + " captures the exact owned desktop dimensions")
        return image

    try:
        check(sys.platform == "linux" and os.geteuid() != 0, "Probe runs on Linux as an unprivileged user")
        check(not os.environ.get("DISPLAY") and not os.environ.get("WAYLAND_DISPLAY"), "Probe imports no host display connection")
        measure("beforeStart")
        started = time.monotonic()
        initial = workspace.start()
        metrics["desktopStartSeconds"] = round(time.monotonic() - started, 3)
        check(initial["running"] and initial["viewerInput"] is False, "Owned desktop starts with viewer input disabled")
        first_display = workspace.display
        first_port = workspace.rfb_port
        check(workspace.start()["running"] and workspace.rfb_port == first_port, "Repeated start retains the same owned display")
        measure("desktopIdleBegin")
        time.sleep(1)
        measure("desktopIdleEnd")
        check(not any("chrom" in process.name().lower() for process in children()), "Desktop startup launches no browser")
        capture("desktop-idle.png")
        check((workspace._private.stat().st_mode & 0o777) == 0o700, "Runtime credentials live in a private directory")
        check(all(((workspace._private / name).stat().st_mode & 0o777) == 0o600 for name in ("passwd", "Xauthority")), "VNC password and Xauthority files have mode0600")
        environment = json.loads(run([sys.executable, "-c", "import json,os; print(json.dumps({'cwd':os.getcwd(),'display':os.environ['DISPLAY'],'home':os.environ['HOME'],'authority':os.environ['XAUTHORITY'],'wayland':os.environ.get('WAYLAND_DISPLAY'),'bus':os.environ.get('DBUS_SESSION_BUS_ADDRESS')}))"]))
        (output / "command-environment.json").write_text(json.dumps(environment, indent=2))
        check(environment["cwd"] == str(workspace.files) and environment["display"] == first_display
              and environment["home"] == str(workspace.root / "home") and environment["bus"] is None
              and environment["wayland"] is None, "Real command reports the owned display, files and scrubbed session environment")
        workspace.set_viewer_input(True)
        check(workspace.status()["viewerInput"], "TigerVNC confirms owner viewer-input enable")
        workspace.set_viewer_input(False)
        check(not workspace.status()["viewerInput"], "TigerVNC confirms viewer-input revoke")

        workspace.launch("terminal")
        terminal = wait(lambda: window("XTerm"))
        run(["xdotool", "windowactivate", "--sync", terminal])
        typed_file = workspace.files / "typed-result.txt"
        workspace.type_text("printf '%s' 'native-input-oracle' > typed-result.txt")
        workspace.key("Return")
        wait(lambda: typed_file.exists() and typed_file.read_text() == "native-input-oracle")
        check(run(["xdotool", "getwindowname", terminal]) == "Deskweave", "Terminal window identity agrees with the explicit launch")
        check(typed_file.read_text() == "native-input-oracle", "Native key input agrees with an independent resulting-file oracle")
        check(not workspace.status()["viewerInput"], "Agent XTest input works while VNC viewer input remains disabled")
        terminal_image = capture("terminal-result.png")
        check(len(terminal_image.resize((64, 40)).getcolors(2560) or []) > 3, "Terminal capture contains rendered application content")
        measure("terminalOpen")

        children()
        workspace.stop()
        check(all(not _live(process) for process in observed.values()), "Browser-independent stop terminates every observed desktop/terminal process")
        check(typed_file.read_text() == "native-input-oracle", "Browser-independent stop retains terminal output")
        workspace.start()
        check(workspace.status()["running"] and typed_file.read_text() == "native-input-oracle", "Browser-independent restart preserves saved files")
        measure("desktopRestarted")

        if not skip_browser:
            page = workspace.files / "browser-fixture.html"
            page.write_text("""<!doctype html><meta charset='utf-8'><title>Deskweave browser fixture</title>
    <style>html,body{margin:0;background:rgb(0,90,200);color:white;font:20px sans-serif}main{padding:50px}input,button{font:20px sans-serif;padding:12px}</style>
    <main><h1>Deskweave local browser fixture</h1><input id='note' autofocus><button onclick="document.title='DW '+document.querySelector('#note').value;document.querySelector('#result').textContent=document.querySelector('#note').value">Confirm</button><p id='result'></p></main>""", encoding="utf-8")
            browser_start = time.monotonic()
            launched = workspace.launch("browser")
            browser = wait(lambda: window("[Cc]hrom"), seconds=15)
            run(["xdotool", "windowactivate", "--sync", browser])
            workspace.key("Alt+F10")
            workspace.key("Ctrl+L")
            workspace.type_text(page.as_uri())
            workspace.key("Return")
            wait(lambda: "Deskweave browser fixture" in run(["xdotool", "getwindowname", browser]), seconds=12)
            metrics["browserFirstPageSeconds"] = round(time.monotonic() - browser_start, 3)
            workspace.type_text("portable ASCII 123")
            workspace.key("Tab")
            workspace.key("Return")
            wait(lambda: "DW portable ASCII 123" in run(["xdotool", "getwindowname", browser]))
            check(True, "Browser key input and native window-title readback agree on the local page result")
            browser_image = capture("browser-result.png")
            blue = sum(1 for red, green, blue in browser_image.resize((128, 80)).getdata()
                       if red < 12 and 78 <= green <= 102 and 188 <= blue <= 212)
            check(blue > 2000, "Independent native capture agrees with the browser fixture's blue page")
            check(workspace.launch("browser")["pid"] == launched["pid"], "Repeated browser launch reuses its owned process tree")
            details = []
            for process in children():
                try:
                    command = process.cmdline()
                    if "chrom" not in process.name().lower():
                        continue
                    fields = {}
                    for line in Path(f"/proc/{process.pid}/status").read_text().splitlines():
                        key, _, value = line.partition(":")
                        if key in ("Uid", "Gid", "NoNewPrivs", "Seccomp", "Seccomp_filters", "NSpid"):
                            fields[key] = value.strip()
                    details.append({"pid": process.pid, "command": command, "status": fields})
                except (psutil.NoSuchProcess, psutil.AccessDenied, FileNotFoundError, PermissionError):
                    continue
            (output / "browser-processes.json").write_text(json.dumps(details, indent=2))
            profile = f"--user-data-dir={workspace.root / 'browser-profile'}"
            check(any(profile in item["command"] for item in details), "Actual browser command line uses only this workspace's profile")
            check(details and all("--no-sandbox" not in item["command"] for item in details), "Observed browser processes do not disable Chromium's sandbox")
            renderers = [item for item in details if "--type=renderer" in item["command"]]
            check(any(item["status"].get("NoNewPrivs") == "1" and item["status"].get("Seccomp") == "2" for item in renderers),
                  "An actual browser renderer reports NoNewPrivs and seccomp filtering")
            measure("browserIdleBegin")
            time.sleep(1)
            measure("browserIdleEnd")
        workspace.stop()
        check(not workspace.status()["running"], "Stop removes the owned desktop registration")
        check(all(not _live(process) for process in observed.values()), "Stop terminates every observed owned process including detached descendants")
        check(typed_file.read_text() == "native-input-oracle", "Stop retains workspace files")
        workspace.start()
        check(typed_file.read_text() == "native-input-oracle" and workspace.status()["running"], "Restart retains the same saved files in a fresh owned desktop")
    except BaseException as error:
        failure = {"type": type(error).__name__, "message": str(error)}
        print("FAIL " + str(error), flush=True)
    finally:
        children()
        try:
            workspace.stop()
        except BaseException as error:
            failure = failure or {"type": type(error).__name__, "message": "Final cleanup: " + str(error)}
        if (workspace._private / "processes.log").exists():
            (output / "runtime-processes.log").write_bytes((workspace._private / "processes.log").read_bytes())
        for name in ("typed-result.txt", "browser-fixture.html"):
            if (workspace.files / name).exists():
                (output / name).write_bytes((workspace.files / name).read_bytes())
        report = {"browserSkipped": skip_browser, "workspaceRoot": str(workspace.root), "status": "passed" if failure is None else "failed", "claims": claims, "failure": failure,
                  "platform": platform.platform(), "tools": dependency_status(), "modelCalls": 0,
                  "hostDisplayInputEvents": 0, "navigation": "Local file fixture only; browser background traffic unmeasured",
                  "measurements": metrics, "observedPids": sorted(observed),
                  "allObservedProcessesStopped": all(not _live(process) for process in observed.values()),
                  "limits": "One constrained Linux container on this Windows owner's existing Docker VM. Not native laptop/Mac, cold VM startup, broad app compatibility or comparative performance evidence."}
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if failure is None else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--workspace-root", type=Path)
    parser.add_argument("--skip-browser", action="store_true")
    args = parser.parse_args()
    raise SystemExit(main(args.output.resolve(), args.workspace_root.resolve() if args.workspace_root else None, args.skip_browser))
