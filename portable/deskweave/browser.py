"""Owned Chromium page runtime. CDP targets our profile, never the user's browser."""
from __future__ import annotations

import base64
import json
import math
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import threading
import time
from urllib.parse import urlparse
from urllib.request import ProxyHandler, build_opener
from urllib.request import url2pathname

import psutil

from .control import Refused


def discover_browser(browser_path: str | Path | None = None) -> str:
    if browser_path is not None:
        candidate = Path(browser_path).expanduser()
        if not candidate.is_file() or (os.name != "nt" and not os.access(candidate, os.X_OK)):
            raise Refused("The selected browser executable does not exist or is not executable.")
        return str(candidate.resolve())
    candidates: list[str] = []
    if sys.platform == "darwin":
        for applications in (Path("/Applications"), Path.home() / "Applications"):
            # Brave, Vivaldi and Arc are ordinary Chromium builds and speak the same protocol.
            # A Mac with one of them and no Chrome used to be told to install a browser.
            for app, executable in (("Google Chrome", "Google Chrome"), ("Chromium", "Chromium"),
                                    ("Microsoft Edge", "Microsoft Edge"),
                                    ("Brave Browser", "Brave Browser"), ("Vivaldi", "Vivaldi"),
                                    ("Arc", "Arc")):
                candidates.append(str(applications / f"{app}.app/Contents/MacOS/{executable}"))
    elif os.name == "nt":
        for root in (os.environ.get("PROGRAMFILES"), os.environ.get("PROGRAMFILES(X86)"),
                     os.environ.get("LOCALAPPDATA")):
            if root:
                candidates.extend(str(Path(root) / path) for path in
                                  ("Google/Chrome/Application/chrome.exe", "Microsoft/Edge/Application/msedge.exe"))
    for executable in ("google-chrome", "google-chrome-stable", "chromium", "chromium-browser",
                       "microsoft-edge", "microsoft-edge-stable", "brave-browser", "brave",
                       "vivaldi", "chrome", "msedge"):
        found = shutil.which(executable)
        if found:
            candidates.append(found)
    for candidate in candidates:
        if Path(candidate).is_file():
            return str(Path(candidate).resolve())
    raise Refused("Install Chrome, Chromium, Edge, Brave, or Vivaldi, or pass --browser-path. "
                  "No browser was downloaded.")


class _CDP:
    """One synchronous connection; unsolicited events never accumulate in memory."""

    def __init__(self, endpoint: str, port: int):
        parsed = urlparse(endpoint)
        if parsed.scheme != "ws" or parsed.hostname != "127.0.0.1" or parsed.port != port:
            raise Refused("The browser returned an unexpected debugging endpoint.")
        if not parsed.path.startswith("/devtools/page/") or parsed.username or parsed.password:
            raise Refused("The browser did not return an owned page endpoint.")
        try:
            import websocket
        except ImportError as exc:
            raise Refused("The browser runtime requires the websocket-client package.") from exc
        self._socket = websocket.create_connection(endpoint, timeout=10, suppress_origin=True,
                                                   http_no_proxy=["127.0.0.1", "localhost"])
        self._lock = threading.RLock()
        self._sequence = 0

    def call(self, method: str, params: dict | None = None, timeout: float = 10) -> dict:
        with self._lock:
            self._sequence += 1
            sequence = self._sequence
            deadline = time.monotonic() + timeout
            self._socket.settimeout(timeout)
            self._socket.send(json.dumps({"id": sequence, "method": method, "params": params or {}}))
            while time.monotonic() < deadline:
                self._socket.settimeout(max(0.01, deadline - time.monotonic()))
                raw = self._socket.recv()
                if not raw:
                    raise Refused("The workspace browser connection closed.")
                if len(raw) > 32 * 1024 * 1024:
                    raise Refused("The browser response exceeded the workspace limit.")
                response = json.loads(raw)
                if response.get("id") != sequence:
                    continue
                if "error" in response:
                    raise Refused(f"Browser operation {method} failed: {response['error'].get('message', 'protocol error')}")
                return response.get("result", {})
            raise Refused(f"Browser operation {method} timed out.")

    def close(self) -> None:
        with self._lock:
            self._socket.close(timeout=1)


_NAMED_KEYS = {
    "enter": ("Enter", "Enter", 13), "return": ("Enter", "Enter", 13),
    "tab": ("Tab", "Tab", 9), "escape": ("Escape", "Escape", 27), "esc": ("Escape", "Escape", 27),
    "backspace": ("Backspace", "Backspace", 8), "delete": ("Delete", "Delete", 46),
    "insert": ("Insert", "Insert", 45),
    "home": ("Home", "Home", 36), "end": ("End", "End", 35),
    "pageup": ("PageUp", "PageUp", 33), "pagedown": ("PageDown", "PageDown", 34),
    "left": ("ArrowLeft", "ArrowLeft", 37), "right": ("ArrowRight", "ArrowRight", 39),
    "up": ("ArrowUp", "ArrowUp", 38), "down": ("ArrowDown", "ArrowDown", 40),
    "space": (" ", "Space", 32),
}
for _direction in ("left", "right", "up", "down"):
    _NAMED_KEYS[f"arrow{_direction}"] = _NAMED_KEYS[_direction]
for _number in range(1, 13):
    _NAMED_KEYS[f"f{_number}"] = (f"F{_number}", f"F{_number}", 111 + _number)
_MODIFIERS = {"ctrl": (2, "Control", "ControlLeft", 17), "control": (2, "Control", "ControlLeft", 17),
              "alt": (1, "Alt", "AltLeft", 18), "shift": (8, "Shift", "ShiftLeft", 16),
              "meta": (4, "Meta", "MetaLeft", 91), "cmd": (4, "Meta", "MetaLeft", 91),
              "super": (4, "Meta", "MetaLeft", 91)}


def _key_spec(chord: str) -> tuple[list[tuple], tuple[str, str, int]]:
    if not isinstance(chord, str) or not 0 < len(chord) <= 64:
        raise Refused("Use a named key or a chord such as Ctrl+A.")
    parts = chord.lower().split("+")
    modifiers = []
    for part in parts[:-1]:
        if part not in _MODIFIERS or _MODIFIERS[part] in modifiers:
            raise Refused("Unsupported or repeated key modifier.")
        modifiers.append(_MODIFIERS[part])
    key = parts[-1]
    if key in _NAMED_KEYS:
        return modifiers, _NAMED_KEYS[key]
    if len(key) == 1 and key.isascii() and key.isalnum():
        return modifiers, (key.upper() if any(x[0] == 8 for x in modifiers) else key,
                           f"Key{key.upper()}" if key.isalpha() else f"Digit{key}", ord(key.upper()))
    raise Refused("Unsupported key. Use type_text for Unicode or punctuation.")


# This viewer owns exactly one page. A target=_blank link or window.open otherwise creates a
# second page nobody is attached to: the click looks like it did nothing, and the orphan keeps
# running until Stop. Keeping the navigation here also keeps the address bar and history honest.
_SAME_TAB_SCRIPT = """
(() => {
  const sameTab = (url) => {
    try { if (url) location.assign(new URL(url, location.href).href); } catch (_) {}
    return window;
  };
  window.open = (url) => sameTab(url);
  document.addEventListener('click', (event) => {
    const anchor = event.target && event.target.closest && event.target.closest('a[target]');
    if (anchor && anchor.target && anchor.target !== '_self') anchor.target = '_self';
  }, true);
})();
"""


class BrowserWorkspace:
    rfb_host = None
    rfb_port = None

    def __init__(self, root: Path | str, width: int = 1024, height: int = 640, fps: int = 12,
                 browser_path: str | Path | None = None, scale: int = 1):
        if type(width) is not int or type(height) is not int or not (320 <= width <= 2560 and 240 <= height <= 1440):
            raise ValueError("Browser viewport must be between 320x240 and 2560x1440.")
        if type(fps) is not int or not 1 <= fps <= 30:
            raise ValueError("Frame rate must be between 1 and 30.")
        # 1 keeps the 4 GB laptop's frame cheap; a HiDPI screen needs 2 or the viewer upscales
        # every frame and the text goes soft. Costs about four times the pixels per frame.
        if type(scale) is not int or not 1 <= scale <= 3:
            raise ValueError("Frame scale must be 1, 2, or 3.")
        self.root = Path(root).expanduser().resolve()
        self.files = self.root / "files"
        self.downloads = self.files / "downloads"
        self.profile = self.root / "browser-profile"
        self.width, self.height, self.fps, self.scale = width, height, fps, scale
        self.browser_path = browser_path
        self._process: subprocess.Popen | None = None
        self._cdp: _CDP | None = None
        self._lock = threading.RLock()
        self._process_lock = threading.RLock()
        self._owned: dict[tuple[int, float], psutil.Process] = {}
        self._roots: dict[tuple[int, float], psutil.Process] = {}
        self._monitor_stop = threading.Event()
        self._monitor: threading.Thread | None = None
        self._lockfile = self.root / ".browser-owner.json"
        self._lock_identity: str | None = None
        self._log = None
        self._url = ""
        self._started_at: str | None = None
        self._error: str | None = None

    @property
    def running(self) -> bool:
        return self._process is not None and self._process.poll() is None and self._cdp is not None

    def _environment(self) -> dict[str, str]:
        allowed = {"PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "LANG", "LC_ALL", "TZ"}
        env = {key: value for key, value in os.environ.items() if key.upper() in allowed}
        home, temp = self.root / "home", self.root / "tmp"
        for directory in (self.root, self.files, self.downloads, self.profile, home, temp, home / ".config", home / ".cache",
                          home / ".local/share", home / "AppData/Roaming", home / "AppData/Local"):
            if directory.is_symlink():
                raise Refused("Workspace directories must not be symbolic links.")
            directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            if os.name != "nt":
                directory.chmod(0o700)
        env.update(HOME=str(home), USERPROFILE=str(home), TMPDIR=str(temp), TMP=str(temp), TEMP=str(temp),
                   XDG_CONFIG_HOME=str(home / ".config"), XDG_CACHE_HOME=str(home / ".cache"),
                   XDG_DATA_HOME=str(home / ".local/share"),
                   APPDATA=str(home / "AppData/Roaming"), LOCALAPPDATA=str(home / "AppData/Local"))
        return env

    def _claim_profile(self) -> None:
        identity = json.dumps({"pid": os.getpid(), "created": psutil.Process().create_time()})
        for attempt in range(2):
            try:
                descriptor = os.open(self._lockfile, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            except FileExistsError:
                try:
                    previous = json.loads(self._lockfile.read_text(encoding="utf-8"))
                    process = psutil.Process(int(previous["pid"]))
                    live = process.is_running() and process.create_time() == previous["created"]
                except psutil.NoSuchProcess:
                    live = False
                except (ValueError, KeyError, OSError, psutil.AccessDenied):
                    raise Refused("This browser profile is locked. Choose a different workspace folder.")
                if not live:
                    if "browserPid" in previous:
                        try:
                            browser = psutil.Process(previous["browserPid"])
                            live = browser.is_running() and browser.create_time() == previous["browserCreated"]
                        except psutil.NoSuchProcess:
                            pass
                        except (ValueError, KeyError, psutil.AccessDenied):
                            raise Refused("The existing browser profile owner could not be verified.")
                if live or attempt:
                    raise Refused("This browser profile is already owned by another runtime.")
                self._lockfile.unlink()
            else:
                with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                    stream.write(identity)
                self._lock_identity = identity
                return

    def _remember(self, process: psutil.Process, root: bool = False) -> None:
        try:
            identity = (process.pid, process.create_time())
            with self._process_lock:
                if identity not in self._owned:
                    self._owned[identity] = process
                    try:
                        process.cpu_percent()
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
                if root:
                    self._roots[identity] = process
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass

    def _refresh_processes(self) -> list[psutil.Process]:
        with self._process_lock:
            known = list(self._owned.values())
        for process in known:
            try:
                if process.is_running():
                    for child in process.children(recursive=True):
                        self._remember(child)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        with self._process_lock:
            alive = []
            for identity, process in list(self._owned.items()):
                try:
                    if process.is_running() and process.status() != psutil.STATUS_ZOMBIE:
                        alive.append(process)
                        continue
                except psutil.AccessDenied:
                    alive.append(process)
                    continue
                except psutil.NoSuchProcess:
                    pass
                self._owned.pop(identity, None)
                self._roots.pop(identity, None)
            return alive

    def _watch_processes(self) -> None:
        while not self._monitor_stop.wait(0.1):
            self._refresh_processes()

    def _spawn(self, argv: list[str], **kwargs) -> subprocess.Popen:
        options = {"start_new_session": True} if os.name != "nt" else {
            "creationflags": subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NO_WINDOW}
        process = subprocess.Popen(argv, cwd=self.files, env=self._environment(), **options, **kwargs)
        try:
            self._remember(psutil.Process(process.pid), root=True)
        except psutil.NoSuchProcess:
            pass
        return process

    def start(self) -> dict:
        with self._lock:
            if self.running:
                return self.status()
            self.stop()
            executable = discover_browser(self.browser_path)
            self._environment()
            self._claim_profile()
            try:
                active_port = self.profile / "DevToolsActivePort"
                active_port.unlink(missing_ok=True)
                self._monitor_stop.clear()
                self._monitor = threading.Thread(target=self._watch_processes, daemon=True, name="DeskweaveBrowserProcesses")
                self._monitor.start()
                self._log = (self.root / "browser.log").open("wb")
                argv = [executable, "--headless", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
                        f"--user-data-dir={self.profile}", f"--window-size={self.width},{self.height}",
                        "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
                        "--disable-sync", "--disable-component-update", "--metrics-recording-only", "--mute-audio", "about:blank"]
                self._process = self._spawn(argv, stdin=subprocess.DEVNULL, stdout=self._log, stderr=self._log)
                identity = json.loads(self._lock_identity)
                identity.update(browserPid=self._process.pid,
                                browserCreated=psutil.Process(self._process.pid).create_time())
                self._lock_identity = json.dumps(identity)
                self._lockfile.write_text(self._lock_identity, encoding="utf-8")
                deadline = time.monotonic() + 20
                port = None
                while time.monotonic() < deadline:
                    if self._process.poll() is not None:
                        raise Refused("The workspace browser exited during startup. Check browser.log; its sandbox was not disabled.")
                    try:
                        port = int(active_port.read_text(encoding="utf-8").splitlines()[0])
                        if not 0 < port <= 65535:
                            raise ValueError()
                        break
                    except (OSError, ValueError, IndexError):
                        time.sleep(0.05)
                if port is None:
                    raise Refused("The workspace browser did not become ready within 20 seconds.")
                opener = build_opener(ProxyHandler({}))
                with opener.open(f"http://127.0.0.1:{port}/json/list", timeout=5) as response:
                    raw = response.read(256 * 1024 + 1)
                if len(raw) > 256 * 1024:
                    raise Refused("Browser target discovery exceeded its limit.")
                pages = [page for page in json.loads(raw) if page.get("type") == "page" and page.get("url") == "about:blank"]
                if len(pages) != 1:
                    raise Refused("The new browser did not expose its expected blank page.")
                self._cdp = _CDP(pages[0]["webSocketDebuggerUrl"], port)
                # Set this before navigating any page: the OS Downloads known-folder
                # can remain the owner's even when Chromium has a private profile.
                self._cdp.call("Browser.setDownloadBehavior", {"behavior": "allow", "downloadPath": str(self.downloads),
                                                              "eventsEnabled": False})
                self._cdp.call("Page.enable")
                self._cdp.call("Emulation.setDeviceMetricsOverride", {
                    "width": self.width, "height": self.height, "deviceScaleFactor": self.scale,
                    "mobile": False})
                self._cdp.call("Page.addScriptToEvaluateOnNewDocument", {"source": _SAME_TAB_SCRIPT})
                welcome = self.files / ".deskweave-welcome.html"
                if not welcome.exists():
                    welcome.write_text("<!doctype html><meta charset=utf-8><title>Deskweave</title>"
                                       "<style>body{background:#101719;color:#f1f4f1;font:20px system-ui;padding:56px}"
                                       "h1{color:#b5e879}p{max-width:560px;line-height:1.6}</style>"
                                       "<h1>Your browser workspace.</h1><p>Open a website from Deskweave to begin. "
                                       "This browser uses its own profile. Native desktop applications are not part of this workspace.</p>",
                                       encoding="utf-8")
                self.navigate(welcome.as_uri())
                from datetime import datetime, timezone
                self._started_at = datetime.now(timezone.utc).isoformat()
                self._error = None
                return self.status()
            except Exception as exc:
                self._error = str(exc)
                try:
                    self.stop()
                except Exception as cleanup:
                    raise Refused(f"Browser startup failed and cleanup was incomplete: {cleanup}") from exc
                raise

    @staticmethod
    def _terminate(processes: list[psutil.Process]) -> None:
        for process in reversed(processes):
            try:
                if process.is_running():
                    process.terminate()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        _, alive = psutil.wait_procs(processes, timeout=2)
        for process in alive:
            try:
                process.kill()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        psutil.wait_procs(alive, timeout=2)

    def stop(self) -> None:
        with self._lock:
            self._monitor_stop.set()
            if self._monitor is not None:
                self._monitor.join(timeout=1)
                self._monitor = None
            processes = self._refresh_processes()
            if self._cdp is not None:
                try:
                    self._cdp.call("Browser.close", timeout=2)
                except Exception:
                    pass
                try:
                    self._cdp.close()
                except Exception:
                    pass
                self._cdp = None
                if self._process is not None:
                    try:
                        self._process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        pass
            self._terminate(processes)
            if self._process is not None:
                try:
                    self._process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    pass
            survivors = self._refresh_processes()
            if survivors or (self._process is not None and self._process.poll() is None):
                self._error = "Some owned workspace processes are still alive. Stop again before restarting."
                raise Refused(self._error)
            self._process = None
            self._started_at = None
            if self._log is not None:
                self._log.close()
                self._log = None
            if self._lock_identity is not None:
                try:
                    if self._lockfile.read_text(encoding="utf-8") == self._lock_identity:
                        self._lockfile.unlink()
                except OSError:
                    pass
                self._lock_identity = None

    def _current_url(self) -> str:
        """Clicking a link never went through navigate(), so the address bar kept showing
        whatever was last typed. Read the frame the page is actually on instead."""
        if not self.running:
            return self._url
        try:
            with self._lock:
                frame = self._cdp.call("Page.getFrameTree", timeout=3).get("frameTree", {}).get("frame", {})
            url = frame.get("url", "")
            if isinstance(url, str) and url:
                self._url = url
        except Exception:
            pass
        return self._url

    def status(self) -> dict:
        processes = []
        self._current_url()
        alive = self._refresh_processes()
        for process in alive:
            try:
                processes.append({"pid": process.pid, "created": process.create_time(), "name": process.name(),
                                  "rssBytes": process.memory_info().rss, "cpuPercent": process.cpu_percent(),
                                  "cpuSeconds": sum(process.cpu_times()[:2])})
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        return {"backend": "browser", "workload": "web", "transport": "frames", "nativePlatform": sys.platform,
                # width/height stay the CSS viewport the viewer maps clicks against; scale only
                # says how many device pixels each frame carries.
                "running": self.running, "width": self.width, "height": self.height, "fps": self.fps,
                "scale": self.scale, "dragSupported": True, "historySupported": True,
                "url": self._url, "processes": processes, "rssBytes": sum(p["rssBytes"] for p in processes),
                "cpuPercent": sum(p["cpuPercent"] for p in processes), "isolation": "owned-browser-profile",
                "processCount": len(alive), "cpuSeconds": sum(p["cpuSeconds"] for p in processes),
                "metricsComplete": len(processes) == len(alive),
                "startedAt": self._started_at, "error": self._error or (
                    "The workspace browser exited." if self._process is not None and self._process.poll() is not None else None),
                "viewerInput": False,
                "nativeApps": False, "commandsSupported": False, "downloadDirectory": str(self.downloads),
                "captureOnDemand": True}

    def _connection(self) -> _CDP:
        if not self.running:
            raise Refused("Start the browser workspace first.")
        return self._cdp  # type: ignore[return-value]

    def navigate(self, url: str) -> dict:
        if not isinstance(url, str) or len(url) > 8192:
            raise Refused("The URL is invalid or too long.")
        try:
            parsed = urlparse(url)
            hostname = parsed.hostname
            parsed.port  # Validate malformed ports before sending any browser action.
        except ValueError as exc:
            raise Refused("The URL has an invalid host or port.") from exc
        if parsed.scheme not in ("http", "https", "file"):
            raise Refused("Open an HTTP, HTTPS, or workspace file URL.")
        if parsed.scheme == "file":
            if parsed.netloc not in ("", "localhost"):
                raise Refused("Only local workspace files can be opened.")
            path = Path(url2pathname(parsed.path)).resolve()
            if not path.is_relative_to(self.files.resolve()) or not path.is_file():
                raise Refused("The file must exist inside this workspace's files folder.")
        elif not hostname or parsed.username or parsed.password:
            raise Refused("Use a website URL without embedded credentials.")
        with self._lock:
            connection = self._connection()
            result = connection.call("Page.navigate", {"url": url}, timeout=15)
            if result.get("errorText") or result.get("isDownload"):
                raise Refused(f"Navigation was not confirmed: {result.get('errorText', 'the URL started a download')}.")
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                frame = connection.call("Page.getFrameTree", timeout=3).get("frameTree", {}).get("frame", {})
                state = connection.call("Runtime.evaluate", {"expression": "document.readyState", "returnByValue": True}, timeout=3)
                if (not result.get("loaderId") or frame.get("loaderId") == result["loaderId"]) and state.get("result", {}).get("value") in ("interactive", "complete"):
                    self._url = frame.get("url", url)
                    return {"ok": True, "url": self._url}
                time.sleep(0.05)
            connection.call("Page.stopLoading", timeout=2)
            raise Refused("Navigation did not become ready within 15 seconds.")

    def screenshot(self) -> bytes:
        with self._lock:
            result = self._connection().call("Page.captureScreenshot", {
                "format": "png", "captureBeyondViewport": False})
            if len(result.get("data", "")) > 24 * 1024 * 1024:
                raise Refused("The browser screenshot exceeded the workspace limit.")
            data = base64.b64decode(result.get("data", ""), validate=True)
            if not data.startswith(b"\x89PNG\r\n\x1a\n") or len(data) < 24 or len(data) > 16 * 1024 * 1024:
                raise Refused("The browser did not return a bounded PNG frame.")
            if struct.unpack(">II", data[16:24]) != (self.width * self.scale, self.height * self.scale):
                raise Refused("The captured browser viewport changed size unexpectedly.")
            return data

    def _point(self, x: float, y: float) -> tuple[float, float]:
        if isinstance(x, bool) or isinstance(y, bool) or not isinstance(x, (int, float)) or not isinstance(y, (int, float)):
            raise Refused("Coordinates must be numbers.")
        if not math.isfinite(x) or not math.isfinite(y) or not 0 <= x < self.width or not 0 <= y < self.height:
            raise Refused("Coordinates are outside the browser viewport.")
        return x, y

    def click(self, x: float, y: float, button: int = 1, count: int = 1) -> dict:
        x, y = self._point(x, y)
        if type(button) is not int or button not in (1, 2, 3) or type(count) is not int or count not in (1, 2):
            raise Refused("Mouse button must be 1, 2, or 3; count must be 1 or 2.")
        with self._lock:
            connection = self._connection()
            for index in range(1, count + 1):
                event = {"x": x, "y": y, "button": {1: "left", 2: "middle", 3: "right"}[button], "clickCount": index}
                try:
                    connection.call("Input.dispatchMouseEvent", {**event, "type": "mousePressed"})
                finally:
                    connection.call("Input.dispatchMouseEvent", {**event, "type": "mouseReleased"})
            return {"ok": True}

    def drag(self, x: float, y: float, to_x: float, to_y: float) -> dict:
        """Press, move, release. Without it there is no text selection, no slider, and no map."""
        x, y = self._point(x, y)
        to_x, to_y = self._point(to_x, to_y)
        with self._lock:
            connection = self._connection()
            event = {"button": "left", "clickCount": 1, "buttons": 1}
            connection.call("Input.dispatchMouseEvent", {**event, "type": "mousePressed", "x": x, "y": y})
            try:
                # Two moves, not one: a single jump to the end point reads as a click to pages
                # that track movement, and selection never starts.
                connection.call("Input.dispatchMouseEvent", {
                    **event, "type": "mouseMoved", "x": (x + to_x) / 2, "y": (y + to_y) / 2})
                connection.call("Input.dispatchMouseEvent", {**event, "type": "mouseMoved", "x": to_x, "y": to_y})
            finally:
                connection.call("Input.dispatchMouseEvent", {
                    **event, "type": "mouseReleased", "x": to_x, "y": to_y, "buttons": 0})
            return {"ok": True}

    def history(self, direction: str) -> dict:
        """Back, forward, reload. A viewer with only an address bar cannot undo a click."""
        if direction not in ("back", "forward", "reload"):
            raise Refused("History must be back, forward, or reload.")
        with self._lock:
            connection = self._connection()
            if direction == "reload":
                connection.call("Page.reload", {}, timeout=15)
            else:
                history = connection.call("Page.getNavigationHistory", timeout=5)
                entries = history.get("entries", [])
                index = history.get("currentIndex", 0) + (-1 if direction == "back" else 1)
                if not 0 <= index < len(entries):
                    raise Refused(f"There is no page to go {direction} to.")
                connection.call("Page.navigateToHistoryEntry", {"entryId": entries[index]["id"]}, timeout=15)
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                state = connection.call("Runtime.evaluate", {
                    "expression": "document.readyState", "returnByValue": True}, timeout=3)
                if state.get("result", {}).get("value") in ("interactive", "complete"):
                    break
                time.sleep(0.05)
        return {"ok": True, "url": self._current_url()}

    def type_text(self, text: str) -> dict:
        if not isinstance(text, str) or not 1 <= len(text) <= 4096 or "\x00" in text:
            raise Refused("Text must contain 1–4096 characters and no null bytes.")
        with self._lock:
            self._connection().call("Input.insertText", {"text": text})
            return {"ok": True}

    def key(self, chord: str) -> dict:
        modifiers, (key, code, virtual) = _key_spec(chord)
        with self._lock:
            connection = self._connection()
            mask = 0
            held = []
            try:
                for bit, mod_key, mod_code, mod_virtual in modifiers:
                    mask |= bit
                    event = {"key": mod_key, "code": mod_code, "windowsVirtualKeyCode": mod_virtual}
                    held.append((bit, event))
                    connection.call("Input.dispatchKeyEvent", {**event, "type": "rawKeyDown", "modifiers": mask})
                event = {"key": key, "code": code, "windowsVirtualKeyCode": virtual, "modifiers": mask}
                down = {**event, "type": "keyDown"}
                if mask & (1 | 2 | 4) == 0 and (len(key) == 1 or key == "Enter"):
                    down["text"] = "\r" if key == "Enter" else key
                try:
                    connection.call("Input.dispatchKeyEvent", down)
                finally:
                    connection.call("Input.dispatchKeyEvent", {**event, "type": "keyUp"})
            finally:
                release_error = None
                for bit, event in reversed(held):
                    mask &= ~bit
                    try:
                        connection.call("Input.dispatchKeyEvent", {**event, "type": "keyUp", "modifiers": mask})
                    except Exception as exc:
                        release_error = exc
                if release_error is not None:
                    raise Refused("Key input may be partial; releasing all modifiers could not be confirmed.") from release_error
            return {"ok": True}

    def scroll(self, dx: int = 0, dy: int = 0) -> dict:
        if any(type(value) is not int or abs(value) > 20 for value in (dx, dy)):
            raise Refused("Scroll deltas must be integer steps between -20 and 20.")
        with self._lock:
            self._connection().call("Input.dispatchMouseEvent", {"type": "mouseWheel", "x": self.width / 2,
                                    "y": self.height / 2, "deltaX": dx * 80, "deltaY": dy * 80})
            return {"ok": True}

    def launch(self, kind: str) -> dict:
        if kind != "browser":
            raise Refused("A browser workspace has no graphical terminal or native desktop applications.")
        reused = self.running
        self.start()
        return {"kind": "browser", "pid": self._process.pid, "running": True, "reused": reused}

    def set_viewer_input(self, enabled: bool) -> None:
        """The HTTP broker authorizes each manual action; there is no RFB input path."""

    def run(self, argv: list[str], timeout: float = 30) -> dict:
        raise Refused("Browser workspaces support web navigation and input. Commands require the Linux Desktop backend.")
