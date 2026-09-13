"""Owned Linux X11 desktops. No host display, transport auth, or control lease logic.

TigerVNC is the display server, not a connection to an existing desktop. All X11
tools and capture helpers receive the workspace's own DISPLAY and XAUTHORITY.
The same-user processes retain ordinary filesystem permissions; this is not a VM.
"""
from __future__ import annotations

import ctypes
import io
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import string
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from typing import Any

import psutil


class NativeError(RuntimeError):
    """An operation failed or was refused; input failures must not be replayed."""


_TOOLS = {
    "server": ("Xtigervnc", "Xvnc"),
    "password": ("tigervncpasswd", "vncpasswd"),
    "config": ("tigervncconfig", "vncconfig"),
    "xauth": ("xauth",),
    "input": ("xdotool",),
    "wm": ("openbox",),
    "terminal": ("xterm",),
    "browser": ("chromium", "chromium-browser", "google-chrome", "google-chrome-stable"),
}
_PATH = "/usr/local/bin:/usr/bin:/bin"
_OUTPUT_LIMIT = 256 * 1024
_KEYS = {
    "enter": "Return", "return": "Return", "tab": "Tab", "escape": "Escape",
    "esc": "Escape", "backspace": "BackSpace", "delete": "Delete", "insert": "Insert",
    "home": "Home", "end": "End", "pageup": "Prior", "pagedown": "Next",
    "left": "Left", "right": "Right", "up": "Up", "down": "Down", "space": "space",
    "arrowleft": "Left", "arrowright": "Right", "arrowup": "Up", "arrowdown": "Down",
    **{f"f{i}": f"F{i}" for i in range(1, 13)},
}
_MODIFIERS = {"ctrl": "ctrl", "control": "ctrl", "alt": "alt", "shift": "shift", "super": "super", "meta": "super"}


def dependency_status() -> dict[str, str | None]:
    """Resolve only system tools, never workspace or caller-controlled PATH entries."""
    return {name: next((found for item in candidates if (found := shutil.which(item, path=_PATH))), None)
            for name, candidates in _TOOLS.items()}


def _integer(value: Any, name: str, minimum: int, maximum: int) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"{name} must be an integer between {minimum} and {maximum}.")
    return value


def _key_chord(value: str) -> str:
    if not isinstance(value, str) or not 1 <= len(value) <= 64:
        raise ValueError("key must be one short key or modifier chord.")
    parts = value.split("+")
    modifiers = []
    for part in parts[:-1]:
        modifier = _MODIFIERS.get(part.lower())
        if modifier is None or modifier in modifiers:
            raise ValueError("Use each of Ctrl, Alt, Shift or Super at most once.")
        modifiers.append(modifier)
    last = parts[-1]
    key = _KEYS.get(last.lower())
    if key is None and re.fullmatch(r"[A-Za-z0-9]", last):
        # Chords name physical letter keys; Shift is an explicit modifier. An
        # uppercase spelling such as Ctrl+L must not inject an extra Shift.
        key = last.lower()
    if key is None:
        raise ValueError("Unsupported key; use a letter, digit, navigation key, or F1-F12.")
    return "+".join([*modifiers, key])


def _command(argv: list[str], timeout: float) -> tuple[list[str], float]:
    if not isinstance(argv, list) or not 1 <= len(argv) <= 64:
        raise ValueError("argv must be a nonempty list of at most 64 strings.")
    if any(not isinstance(item, str) or "\0" in item for item in argv) or not argv[0]:
        raise ValueError("argv must contain strings without NUL bytes and a nonempty executable.")
    if sum(len(item.encode("utf-8")) for item in argv) > 32768:
        raise ValueError("argv exceeds 32 KiB.")
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not 0.1 <= timeout <= 60:
        raise ValueError("timeout must be between 0.1 and 60 seconds.")
    return list(argv), float(timeout)


def _live(process: psutil.Process) -> bool:
    try:
        return process.is_running() and process.status() != psutil.STATUS_ZOMBIE
    except psutil.NoSuchProcess:
        return False
    except psutil.AccessDenied:
        # An unreadable process is not evidence of exit.
        return True


def _descendants(process: psutil.Process) -> list[psutil.Process]:
    try:
        return process.children(recursive=True) if process.is_running() else []
    except psutil.NoSuchProcess:
        return []
    except psutil.AccessDenied as error:
        raise NativeError(f"Cannot verify the owned child tree for PID {process.pid}.") from error


def _terminate(process: subprocess.Popen[bytes]) -> None:
    """Ask the subreaper to clean up; retain an exact-identity fallback snapshot."""
    # Keep observed descendants on this exact Popen across a failed cleanup retry,
    # including descendants whose supervisor has already exited.
    children = list(getattr(process, "_deskweave_children", []))
    if process.poll() is None:
        try:
            identity = psutil.Process(process.pid)
            children.extend([identity, *_descendants(identity)])
            process._deskweave_children = children
            identity.terminate()
        except psutil.NoSuchProcess:
            pass
        try:
            process.wait(timeout=4)
        except subprocess.TimeoutExpired:
            pass
    remaining = [item for item in children if _live(item)]
    for child in reversed(remaining):
        try:
            child.kill()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    if remaining:
        psutil.wait_procs(remaining, timeout=1)
    if any(_live(item) for item in children):
        raise NativeError("Owned process cleanup could not confirm exit for every observed descendant.")
    if process.poll() is None:
        process.wait(timeout=1)
    process._deskweave_children = []


class NativeWorkspace:
    rfb_host = "127.0.0.1"

    def __init__(self, root: Path, width: int = 1024, height: int = 640, fps: int = 12):
        self.root = Path(root).resolve()
        self.files = self.root / "files"
        self.width = _integer(width, "width", 640, 3840)
        self.height = _integer(height, "height", 480, 2160)
        self.fps = _integer(fps, "fps", 1, 30)
        self.rfb_port: int | None = None
        self.vnc_password: str | None = None
        self.display: str | None = None
        self._private = self.root / ".runtime"
        self._env: dict[str, str] = {}
        self._tools: dict[str, str | None] = {}
        self._processes: list[subprocess.Popen[bytes]] = []
        self._server: subprocess.Popen[bytes] | None = None
        self._wm: subprocess.Popen[bytes] | None = None
        self._browser: subprocess.Popen[bytes] | None = None
        self._running = False
        self._viewer_input = False
        self._started_at: str | None = None
        self._error: str | None = None
        self._lock = threading.RLock()
        self._watch_stop = threading.Event()

    def _environment(self) -> dict[str, str]:
        # Build from an allowlist: never inherit host display/session, credentials,
        # proxies, startup hooks, browser settings or Python module-search overrides.
        return {
            "PATH": _PATH, "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8",
            "HOME": str(self.root / "home"), "USER": "deskweave", "LOGNAME": "deskweave",
            "DISPLAY": str(self.display), "XAUTHORITY": str(self._private / "Xauthority"),
            "XDG_CONFIG_HOME": str(self.root / "home" / ".config"),
            "XDG_CACHE_HOME": str(self.root / "home" / ".cache"),
            "XDG_DATA_HOME": str(self.root / "home" / ".local" / "share"),
            "XDG_RUNTIME_DIR": str(self._private / "xdg"),
            "TMPDIR": str(self._private / "tmp"), "XDG_SESSION_TYPE": "x11",
            "DESKWEAVE_WORKSPACE_ROOT": str(self.root),
        }

    def _spawn(self, argv: list[str], *, persistent: bool, pipes: bool = False) -> subprocess.Popen[bytes]:
        self._processes = [process for process in self._processes if process.poll() is None
                           or any(_live(item) for item in getattr(process, "_deskweave_children", []))]
        if len(self._processes) >= 24:
            raise NativeError("This workspace has reached its 24 owned-launch limit.")
        # Python's helper avoids preexec_fn in the broker's worker threads. It owns
        # a separate session and adopts daemonized grandchildren as a Linux subreaper.
        command = [sys.executable, str(Path(__file__).resolve()), "--owned-process",
                   str(os.getpid()), "keep" if persistent else "finish", "--", *argv]
        with open(self._private / "processes.log", "ab", buffering=0) as log:
            process = subprocess.Popen(command, cwd=self.files, env=self._env,
                                       stdin=subprocess.PIPE if pipes else subprocess.DEVNULL,
                                       stdout=subprocess.PIPE if pipes else log,
                                       stderr=subprocess.PIPE if pipes else log,
                                       start_new_session=True, close_fds=True)
        self._processes.append(process)
        return process

    def _observe(self, parent: subprocess.Popen[bytes]) -> list[psutil.Process]:
        observed = {(item.pid, item.create_time()): item
                    for item in getattr(parent, "_deskweave_children", []) if _live(item)}
        if parent.poll() is None:
            try:
                identity = psutil.Process(parent.pid)
                observed[(identity.pid, identity.create_time())] = identity
                for item in _descendants(identity):
                    observed[(item.pid, item.create_time())] = item
            except psutil.NoSuchProcess:
                pass
        parent._deskweave_children = [item for item in observed.values() if _live(item)]
        return parent._deskweave_children

    def _execute(self, argv: list[str], timeout: float = 5, *, data: bytes = b"",
                 limit: int = _OUTPUT_LIMIT) -> dict[str, Any]:
        process = self._spawn(argv, persistent=False, pipes=True)
        output = [bytearray(), bytearray()]
        truncated = [False, False]

        def drain(stream: Any, index: int) -> None:
            try:
                while chunk := stream.read(65536):
                    remaining = max(0, limit - len(output[index]))
                    output[index].extend(chunk[:remaining])
                    truncated[index] |= len(chunk) > remaining
            finally:
                stream.close()

        readers = [threading.Thread(target=drain, args=(stream, index), daemon=True)
                   for index, stream in enumerate((process.stdout, process.stderr))]
        for reader in readers:
            reader.start()
        timed_out = False
        try:
            try:
                if data:
                    process.stdin.write(data)
                process.stdin.close()
            except BrokenPipeError:
                # A command can close stdin before exiting. Its remaining work
                # still has the same timeout and process-tree cleanup bound.
                pass
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            _terminate(process)
        finally:
            if process.poll() is None:
                _terminate(process)
            for reader in readers:
                reader.join(timeout=2)
            self._processes = [item for item in self._processes if item is not process]
        return {"exitCode": process.returncode, "stdout": bytes(output[0]), "stderr": bytes(output[1]),
                "timedOut": timed_out, "truncated": any(truncated)}

    def _checked(self, argv: list[str], timeout: float = 5, *, data: bytes = b"",
                 limit: int = _OUTPUT_LIMIT, input_action: bool = False) -> bytes:
        result = self._execute(argv, timeout, data=data, limit=limit)
        diagnostic = result["stderr"].decode("utf-8", errors="replace").strip()
        if result["exitCode"] != 0 or result["timedOut"] or result["truncated"] or (input_action and diagnostic):
            suffix = " Input may be partial; inspect the screen before continuing. Do not replay automatically." if input_action else ""
            raise NativeError((diagnostic[-1500:] or "Owned desktop command failed or exceeded its bound.") + suffix)
        return result["stdout"]

    def start(self) -> dict[str, Any]:
        with self._lock:
            if self._running:
                self._ensure_running()
                return self.status()
            if self._processes:
                self._stop_locked()
                if self._processes:
                    raise NativeError(self._error or "Earlier owned processes still need cleanup.")
            if sys.platform != "linux":
                raise NativeError("This backend requires Linux. It never attaches to a host Windows or Mac desktop.")
            self._tools = dependency_status()
            missing = [name for name, path in self._tools.items() if path is None and name != "browser"]
            if missing:
                raise NativeError("Missing Linux desktop dependencies: " + ", ".join(missing))
            self._error = None
            try:
                for path in (self.root, self.files, self.root / "home", self._private,
                             self._private / "tmp", self._private / "xdg"):
                    if path.is_symlink():
                        raise NativeError("Workspace-owned runtime directories must not be symbolic links.")
                    path.mkdir(mode=0o700, parents=True, exist_ok=True)
                os.chmod(self._private, 0o700)
                os.chmod(self._private / "xdg", 0o700)
                candidates = [100 + secrets.randbelow(10000) for _ in range(32)]
                display_number = next((number for number in candidates
                                       if not Path(f"/tmp/.X11-unix/X{number}").exists()
                                       and not Path(f"/tmp/.X{number}-lock").exists()), None)
                if display_number is None:
                    raise NativeError("No unused owned display number was found.")
                self.display = f":{display_number}"
                self._env = self._environment()
                with socket.socket() as reservation:
                    reservation.bind((self.rfb_host, 0))
                    self.rfb_port = reservation.getsockname()[1]
                self.vnc_password = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(8))
                password = self._checked([str(self._tools["password"]), "-f"],
                                         data=(self.vnc_password + "\n").encode("ascii"))
                if len(password) != 8:
                    raise NativeError("VNC password encoder did not return one full-control credential.")
                for name, content in (("passwd", password), ("Xauthority", b"")):
                    descriptor = os.open(self._private / name, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
                    with os.fdopen(descriptor, "wb") as stream:
                        stream.write(content)
                    os.chmod(self._private / name, 0o600)
                self._checked([str(self._tools["xauth"]), "-f", self._env["XAUTHORITY"],
                               "add", self.display, "MIT-MAGIC-COOKIE-1", secrets.token_hex(16)])
                server = [str(self._tools["server"]), self.display, "-geometry", f"{self.width}x{self.height}",
                          "-depth", "24", "-auth", self._env["XAUTHORITY"], "-nolisten", "tcp",
                          "-localhost", "yes", "-interface", self.rfb_host, "-rfbport", str(self.rfb_port),
                          "-SecurityTypes", "VncAuth", "-PasswordFile", str(self._private / "passwd"),
                          "-FrameRate", str(self.fps), "-CompareFB", "2", "-AlwaysShared", "1",
                          "-AcceptKeyEvents", "0", "-AcceptPointerEvents", "0",
                          "-AcceptCutText", "0", "-SendCutText", "0", "-SendPrimary", "0", "-SetPrimary", "0",
                          "-AcceptSetDesktopSize", "0", "-AllowOverride", "AcceptKeyEvents,AcceptPointerEvents",
                          "-desktop", "Deskweave"]
                self._server = self._spawn(server, persistent=True)
                deadline = time.monotonic() + 12
                while True:
                    if self._server.poll() is not None:
                        raise NativeError("The owned TigerVNC display failed to start; inspect .runtime/processes.log.")
                    try:
                        geometry = self._checked([str(self._tools["input"]), "getdisplaygeometry"], timeout=1)
                        if geometry.strip() == f"{self.width} {self.height}".encode("ascii"):
                            break
                    except NativeError:
                        pass
                    if time.monotonic() >= deadline:
                        raise NativeError("The owned authenticated X11 display did not become ready.")
                    time.sleep(0.1)
                self._wm = self._spawn([str(self._tools["wm"]), "--sm-disable"], persistent=True)
                time.sleep(0.15)
                if self._wm.poll() is not None:
                    raise NativeError("The workspace window manager failed to start.")
                self._running = True
                self._started_at = datetime.now(timezone.utc).isoformat()
                self.set_viewer_input(False)
                self._watch_stop = threading.Event()
                threading.Thread(target=self._watch, args=(self._watch_stop,), daemon=True,
                                 name="deskweave-display-health").start()
                return self.status()
            except BaseException as error:
                self._error = str(error)
                self._stop_locked()
                raise

    def _watch(self, stop: threading.Event) -> None:
        while not stop.wait(0.25):
            with self._lock:
                if stop is not self._watch_stop or not self._running:
                    return
                try:
                    for process in self._processes:
                        self._observe(process)
                except (NativeError, psutil.AccessDenied) as error:
                    self._error = str(error)
                    self._stop_locked()
                    return
                if self._server.poll() is not None or self._wm.poll() is not None:
                    self._error = "The owned display or window manager exited; its workspace processes were stopped."
                    self._stop_locked()
                    return

    def _ensure_running(self) -> None:
        if self._running and (self._server.poll() is not None or self._wm.poll() is not None):
            self._error = "The owned display or window manager exited."
            self._stop_locked()
        if not self._running:
            raise NativeError(self._error or "Start this workspace's desktop before using it.")

    def _stop_locked(self) -> None:
        self._watch_stop.set()
        self._running = False
        self._viewer_input = False
        failures = []
        pending = []
        for process in reversed(self._processes):
            try:
                _terminate(process)
            except (OSError, subprocess.TimeoutExpired, NativeError, psutil.AccessDenied) as error:
                failures.append(str(error))
                pending.append(process)
        self._processes = pending
        self._server = self._wm = self._browser = None
        self.rfb_port = None
        self.vnc_password = None
        self.display = None
        self._started_at = None
        if failures:
            self._error = "Some owned processes could not be confirmed stopped: " + "; ".join(failures)

    def stop(self) -> None:
        with self._lock:
            self._stop_locked()
            if self._processes:
                raise NativeError(self._error or "Some owned processes remain unconfirmed.")

    def status(self) -> dict[str, Any]:
        with self._lock:
            if self._running:
                try:
                    self._ensure_running()
                except NativeError:
                    pass
            processes: dict[int, psutil.Process] = {}
            metrics_complete = True
            for parent in self._processes:
                try:
                    for process in self._observe(parent):
                        processes[process.pid] = process
                except (NativeError, psutil.AccessDenied) as error:
                    metrics_complete = False
                    self._error = str(error)
                    for process in getattr(parent, "_deskweave_children", []):
                        if _live(process):
                            processes[process.pid] = process
            rss = 0
            cpu = 0.0
            for process in processes.values():
                try:
                    rss += process.memory_info().rss
                    usage = process.cpu_times()
                    cpu += usage.user + usage.system
                except psutil.NoSuchProcess:
                    pass
                except psutil.AccessDenied:
                    metrics_complete = False
            return {"running": self._running, "width": self.width, "height": self.height,
                    "processCount": len(processes), "rssBytes": rss, "cpuSeconds": round(cpu, 3),
                    "metricsComplete": metrics_complete,
                    "startedAt": self._started_at, "backend": "linux-x11-tigervnc",
                    "isolation": "Separate X11 display; same Linux user and filesystem permissions. Not a VM.",
                    "error": self._error, "viewerInput": self._viewer_input}

    def set_viewer_input(self, enabled: bool) -> None:
        if type(enabled) is not bool:
            raise ValueError("enabled must be a boolean.")
        with self._lock:
            self._ensure_running()
            try:
                for parameter in ("AcceptKeyEvents", "AcceptPointerEvents"):
                    tool = str(self._tools["config"])
                    self._checked([tool, "-display", str(self.display), "-set", f"{parameter}={int(enabled)}"])
                    actual = self._checked([tool, "-display", str(self.display), "-get", parameter]).strip().lower()
                    if actual not in ((b"1", b"true", b"yes", b"on") if enabled else (b"0", b"false", b"no", b"off")):
                        raise NativeError("TigerVNC did not confirm the requested viewer-input state.")
                self._viewer_input = enabled
            except BaseException:
                self._error = "Viewer-input control failed; the desktop was stopped to prevent an unconfirmed control grant."
                self._stop_locked()
                raise

    def screenshot(self) -> bytes:
        with self._lock:
            self._ensure_running()
            image = self._checked([sys.executable, str(Path(__file__).resolve()), "--capture", str(self.display)],
                                  timeout=8, limit=32 * 1024 * 1024)
            if not image.startswith(b"\x89PNG\r\n\x1a\n"):
                raise NativeError("Owned display capture did not return a PNG.")
            return image

    def click(self, x: int, y: int, button: int = 1) -> None:
        _integer(x, "x", 0, self.width - 1)
        _integer(y, "y", 0, self.height - 1)
        _integer(button, "button", 1, 3)
        with self._lock:
            self._ensure_running()
            self._checked([str(self._tools["input"]), "mousemove", "--sync", str(x), str(y),
                           "click", "--clearmodifiers", str(button)], input_action=True)

    def type_text(self, text: str) -> None:
        # libxdo may skip unmappable Unicode and still exit successfully. Refuse the
        # whole unsupported request before injecting anything; never claim readback.
        if not isinstance(text, str) or not 1 <= len(text) <= 4096 or any(not " " <= char <= "~" for char in text):
            raise ValueError("Native type_text currently accepts 1-4096 printable ASCII characters only; use key for Return/Tab. Unicode is not confirmed by this route.")
        with self._lock:
            self._ensure_running()
            self._checked([str(self._tools["input"]), "type", "--clearmodifiers", "--delay", "1", "--file", "-"],
                          timeout=15, data=text.encode("ascii"), input_action=True)

    def key(self, key: str) -> None:
        chord = _key_chord(key)
        with self._lock:
            self._ensure_running()
            self._checked([str(self._tools["input"]), "key", "--clearmodifiers", "--", chord], input_action=True)

    def scroll(self, dx: int, dy: int) -> None:
        _integer(dx, "dx", -20, 20)
        _integer(dy, "dy", -20, 20)
        with self._lock:
            self._ensure_running()
            for amount, negative, positive in ((dy, 4, 5), (dx, 6, 7)):
                if amount:
                    self._checked([str(self._tools["input"]), "click", "--clearmodifiers", "--repeat", str(abs(amount)),
                                   "--delay", "30", str(positive if amount > 0 else negative)], input_action=True)

    def _application_window(self, process: subprocess.Popen[bytes], kind: str) -> str | None:
        if process.poll() is not None:
            return None
        owned_pids = {item.pid for item in self._observe(process)}
        pattern = "[Cc]hrom|[Gg]oogle-chrome" if kind == "browser" else "[Xx][Tt]erm"
        result = self._execute([str(self._tools["input"]), "search", "--onlyvisible", "--class", pattern], timeout=2)
        if result["exitCode"] != 0 or result["timedOut"] or result["truncated"]:
            return None
        for window in result["stdout"].decode("ascii", errors="ignore").splitlines():
            if not window.isdecimal():
                continue
            try:
                pid = int(self._checked([str(self._tools["input"]), "getwindowpid", window], timeout=2).strip())
            except (ValueError, NativeError):
                continue
            if pid in owned_pids:
                return window
        return None

    def launch(self, kind: str) -> dict[str, Any]:
        if kind not in ("terminal", "browser"):
            raise ValueError("launch kind must be 'terminal' or 'browser'.")
        with self._lock:
            self._ensure_running()
            if kind == "browser":
                if self._browser is not None and self._browser.poll() is None:
                    if self._application_window(self._browser, kind):
                        return {"kind": kind, "pid": self._browser.pid, "running": True, "reused": True}
                    _terminate(self._browser)
                    self._processes = [item for item in self._processes if item is not self._browser]
                    self._browser = None
                if self._tools["browser"] is None:
                    raise NativeError("No supported Chromium browser is installed; no download was started.")
                if os.geteuid() == 0:
                    raise NativeError("Launch the pilot as an unprivileged Linux user so Chromium can keep its normal sandbox.")
                argv = [str(self._tools["browser"]), f"--user-data-dir={self.root / 'browser-profile'}",
                        "--no-first-run", "--no-default-browser-check", "--ozone-platform=x11", "about:blank"]
            else:
                argv = [str(self._tools["terminal"]), "-T", "Deskweave", "-geometry", "100x28+24+24",
                        "-fa", "DejaVu Sans Mono", "-fs", "11", "-e", "/bin/sh"]
            process = self._spawn(argv, persistent=True)
            try:
                deadline = time.monotonic() + 12
                while not self._application_window(process, kind):
                    if process.poll() is not None or time.monotonic() >= deadline:
                        raise NativeError(f"The {kind} did not open an owned visible window; inspect .runtime/processes.log. Browser sandbox failures are never bypassed.")
                    time.sleep(0.1)
            except BaseException:
                _terminate(process)
                self._processes = [item for item in self._processes if item is not process]
                raise
            if kind == "browser":
                self._browser = process
            return {"kind": kind, "pid": process.pid, "running": True, "reused": False}

    def run(self, argv: list[str], timeout: float = 30) -> dict[str, Any]:
        argv, timeout = _command(argv, timeout)
        with self._lock:
            self._ensure_running()
            result = self._execute(argv, timeout)
            return {**result, "stdout": result["stdout"].decode("utf-8", errors="replace"),
                    "stderr": result["stderr"].decode("utf-8", errors="replace")}


def _owned_process(parent_pid: int, persistent: bool, argv: list[str]) -> int:
    """Own one launch tree even when its children double-fork or call setsid()."""
    if sys.platform != "linux":
        raise NativeError("The owned-process supervisor requires Linux.")
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.prctl(36, 1, 0, 0, 0) != 0 or libc.prctl(1, signal.SIGTERM, 0, 0, 0) != 0:
        raise OSError(ctypes.get_errno(), "Could not enable Linux child supervision")
    stopping = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stopping.set())
    signal.signal(signal.SIGINT, lambda *_: stopping.set())
    if os.getppid() != parent_pid:
        return 125
    child = subprocess.Popen(argv, close_fds=True)
    supervisor = psutil.Process()
    exit_code = None
    try:
        while not stopping.wait(0.05):
            exit_code = child.poll()
            if exit_code is not None:
                # Reap children adopted from an exited intermediate parent.
                while True:
                    try:
                        if os.waitpid(-1, os.WNOHANG)[0] == 0:
                            break
                    except ChildProcessError:
                        break
                if not persistent or not any(_live(item) for item in _descendants(supervisor)):
                    break
    finally:
        deadline = time.monotonic() + 2
        while True:
            children = [item for item in _descendants(supervisor) if _live(item)]
            if not children:
                break
            force = time.monotonic() >= deadline
            for item in reversed(children):
                try:
                    item.kill() if force else item.terminate()
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    pass
            if force:
                psutil.wait_procs(children, timeout=0.5)
                break
            time.sleep(0.05)
        try:
            child.wait(timeout=0.5)
        except subprocess.TimeoutExpired:
            pass
        while True:
            try:
                if os.waitpid(-1, os.WNOHANG)[0] == 0:
                    break
            except ChildProcessError:
                break
    if exit_code is None:
        return 128 + signal.SIGTERM
    return exit_code if exit_code >= 0 else 128 - exit_code


def _capture(display: str) -> None:
    from PIL import ImageGrab, features
    if sys.platform != "linux" or not display.startswith(":") or os.environ.get("DISPLAY") != display:
        raise NativeError("Capture requires this helper's explicit owned Linux display.")
    if not features.check_feature("xcb"):
        raise NativeError("Pillow was built without XCB; owned X11 capture is unavailable.")
    # An explicit xdisplay disables Pillow's host screenshot-tool fallbacks. This
    # process alone has the workspace's XAUTHORITY; no broker environment is changed.
    image = ImageGrab.grab(xdisplay=display)
    output = io.BytesIO()
    image.save(output, format="PNG")
    sys.stdout.buffer.write(output.getvalue())


if __name__ == "__main__":
    if len(sys.argv) >= 6 and sys.argv[1] == "--owned-process" and sys.argv[4] == "--":
        raise SystemExit(_owned_process(int(sys.argv[2]), sys.argv[3] == "keep", sys.argv[5:]))
    if len(sys.argv) == 3 and sys.argv[1] == "--capture":
        _capture(sys.argv[2])
    else:
        raise SystemExit("This module is an internal owned-process/capture helper.")
