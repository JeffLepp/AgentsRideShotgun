"""Private pilot launcher. Does not install packages or change host virtualization."""
from __future__ import annotations

import argparse
import asyncio
import json
import os
from pathlib import Path
import signal
import sys
import time
import webbrowser

from aiohttp import web
import psutil

from . import __version__


def data_default():
    if sys.platform == "darwin":
        return Path.home() / "Library/Application Support/Deskweave Portable"
    if sys.platform == "win32":
        return Path(os.environ.get("LOCALAPPDATA", Path.home())) / "DeskweavePortable"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "deskweave-portable"


def backend_name(value):
    return ("desktop" if sys.platform == "linux" else "browser") if value == "auto" else value


def workspace(args):
    # The workspace is a subfolder so the owner connection file is never inside its tree.
    root = args.data_dir / "workspace"
    if backend_name(args.backend) == "desktop":
        from .native import NativeWorkspace
        if sys.platform != "linux":
            raise ValueError("Desktop mode needs Linux. Use browser mode here, or run the Linux container.")
        return NativeWorkspace(root, args.width, args.height, args.fps)
    from .browser import BrowserWorkspace
    return BrowserWorkspace(root, args.width, args.height, args.fps,
                            browser_path=args.browser_path, scale=args.scale)


def move_legacy_workspace(data_dir):
    """0.1.0 used the data folder itself as the workspace; carry its files and profile over once."""
    root = data_dir / "workspace"
    if root.exists() or not (data_dir / "files").is_dir():
        return
    root.mkdir(mode=0o700)
    for name in ("files", "home", "browser-profile"):
        if (data_dir / name).exists() and not (data_dir / name).is_symlink():
            (data_dir / name).rename(root / name)


class InstanceLock:
    """An OS lock lives only as long as the broker; no stale PID-based ownership."""
    def __init__(self, root):
        root.mkdir(parents=True, exist_ok=True, mode=0o700)
        if os.name != "nt":
            root.chmod(0o700)
        self.file = (root / ".broker.lock").open("a+b")
        self.file.seek(0)
        self.file.write(b"0")
        self.file.flush()
        self.file.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.file.close()
            raise RuntimeError("This data folder already has a running Deskweave broker. Use its launch link or another folder.") from None

    def close(self):
        self.file.close()


def private_json(path, data):
    temp = path.with_suffix(".pending")
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        if os.name != "nt":
            os.fchmod(output.fileno(), 0o600)
        json.dump(data, output, indent=2)
        output.write("\n")
    os.replace(temp, path)


async def serve(args):
    from .server import Broker, make_app
    args.data_dir = args.data_dir.expanduser().resolve()
    instance = InstanceLock(args.data_dir)
    connection = args.data_dir / "connection.json"
    bridge = args.data_dir / "agent-connection.json"
    runner = None
    try:
        move_legacy_workspace(args.data_dir)
        stopped = asyncio.Event()
        broker = Broker(workspace(args), host_platform=args.host_platform, shutdown=stopped.set)
        runner = web.AppRunner(make_app(broker), access_log=None, shutdown_timeout=65)
        await runner.setup()
        host = "0.0.0.0" if args.container_bind else "127.0.0.1"
        if args.container_bind and not Path("/.dockerenv").exists() and not Path("/run/.containerenv").exists():
            raise ValueError("--container-bind is only for a container published to host loopback")
        site = web.TCPSite(runner, host, args.port)
        await site.start()
        port = site._server.sockets[0].getsockname()[1]
        url = f"http://127.0.0.1:{port}"
        shared = {"version": __version__, "url": url, "agentToken": broker.agent_token, "pid": os.getpid(),
                  "backend": backend_name(args.backend), "commandsSupported": backend_name(args.backend) == "desktop"}
        # The MCP bridge runs as the agent, so its file must not carry the owner token.
        private_json(bridge, shared)
        private_json(connection, {**shared, "ownerToken": broker.owner_token})
        print(f"Deskweave {__version__} · {backend_name(args.backend)} workspace", flush=True)
        print(f"Local viewer: {url}  (use 'python -m deskweave open --connection ...' to authenticate)", flush=True)
        print(f"Private connection file: {connection}", flush=True)
        if args.open:
            await asyncio.to_thread(webbrowser.open, f"{url}/#token={broker.owner_token}")
        loop = asyncio.get_running_loop()
        previous = {}
        for signum in (signal.SIGINT, signal.SIGTERM):
            try:
                previous[signum] = signal.signal(signum, lambda *_: loop.call_soon_threadsafe(stopped.set))
            except (ValueError, OSError):
                pass
        try:
            await stopped.wait()
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
    finally:
        try:
            if runner:
                await runner.cleanup()
        finally:
            try:
                connection.unlink(missing_ok=True)
                bridge.unlink(missing_ok=True)
            finally:
                instance.close()


def load_connection(path, owner=True):
    from urllib.parse import urlsplit
    import re
    try:
        data = json.loads(path.expanduser().read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError):
        raise ValueError("The connection file is unreadable. Restart the Deskweave launcher to create a new one.") from None
    if not isinstance(data, dict) or not isinstance(data.get("url"), str):
        raise ValueError("The connection file is incomplete. Restart the Deskweave launcher to create a new one.")
    url = data["url"]
    try:
        parsed = urlsplit(url)
        valid = (parsed.scheme == "http" and parsed.hostname in ("127.0.0.1", "localhost", "::1")
                 and not parsed.username and not parsed.password and parsed.path in ("", "/")
                 and "?" not in url and "#" not in url and parsed.port is not None and parsed.port > 0
                 and not any(character.isspace() for character in url))
    except ValueError:
        valid = False
    if not valid:
        raise ValueError("Connection must name a local HTTP broker and port. Restart the Deskweave launcher.")
    data["url"] = url.rstrip("/")
    if any(not isinstance(data.get(key), str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,512}", data[key])
           for key in (("ownerToken", "agentToken") if owner else ("agentToken",))):
        raise ValueError("Connection authentication is incomplete. Restart the Deskweave launcher.")
    if not owner:
        data.pop("ownerToken", None)
    return data


def build_parser():
    parser = argparse.ArgumentParser(description="Deskweave portable private test kit")
    parser.add_argument("--version", action="version", version=__version__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("serve", "doctor"):
        p = commands.add_parser(name)
        p.add_argument("--backend", choices=("auto", "desktop", "browser"), default="auto")
        p.add_argument("--data-dir", type=Path, default=data_default())
        p.add_argument("--browser-path", default=None)
        p.add_argument("--width", type=int, default=1024)
        p.add_argument("--height", type=int, default=640)
        p.add_argument("--fps", type=int, default=12)
        # 2 on a HiDPI screen, or the viewer upscales every frame; 1 keeps a small laptop cheap.
        p.add_argument("--scale", type=int, default=1, choices=(1, 2, 3))
        if name == "serve":
            p.add_argument("--port", type=int, default=0)
            p.add_argument("--open", action="store_true")
            p.add_argument("--container-bind", action="store_true")
            p.add_argument("--host-platform", default=None)
    for name in ("open", "mcp", "sample", "shutdown"):
        p = commands.add_parser(name)
        p.add_argument("--connection", type=Path,
                       default=data_default() / ("agent-connection.json" if name == "mcp" else "connection.json"))
        if name == "sample":
            p.add_argument("--seconds", type=int, default=30)
            p.add_argument("--output", type=Path, required=True)
    return parser


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    args = build_parser().parse_args(argv)
    try:
        if args.command == "serve":
            if not 0 <= args.port <= 65535:
                raise ValueError("port must be 0–65535")
            asyncio.run(serve(args))
        elif args.command == "doctor":
            kind = backend_name(args.backend)
            result = {"platform": sys.platform, "architecture": __import__("platform").machine(),
                      "python": sys.version.split()[0], "memoryMiB": round(psutil.virtual_memory().total / 1048576),
                      "backend": kind, "workspaceStarted": False}
            if kind == "desktop":
                from .native import dependency_status
                from PIL import features
                result["dependencies"] = dependency_status()
                result["captureXcb"] = features.check_feature("xcb")
                result["ready"] = sys.platform == "linux" and result["captureXcb"] and all(v for k, v in result["dependencies"].items() if k != "browser")
            else:
                item = workspace(args)
                result.update(item.status())
                from .browser import discover_browser
                try:
                    result["browserExecutable"] = discover_browser(args.browser_path)
                    result["ready"] = True
                except RuntimeError as exc:
                    result["ready"] = False
                    result["error"] = str(exc)
            print(json.dumps(result, indent=2))
        elif args.command == "open":
            data = load_connection(args.connection)
            webbrowser.open(f"{data['url']}/#token={data['ownerToken']}")
        elif args.command == "mcp":
            from .mcp import stdio
            stdio(load_connection(args.connection, owner=False))
        elif args.command == "shutdown":
            from .mcp import http_json
            data = load_connection(args.connection)
            http_json(data["url"] + "/api/shutdown", data["ownerToken"], {})
            print("Broker shutdown requested; accepted actions finish before owned processes close.")
        else:
            if not 1 <= args.seconds <= 3600:
                raise ValueError("seconds must be 1–3600")
            from .mcp import http_json
            data = load_connection(args.connection)
            samples = []
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                samples.append({"at": time.time(), **http_json(data["url"] + "/api/status", data["ownerToken"])})
                time.sleep(1)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps({"scope": "Workspace process tree only; excludes viewer and container/VM overhead.", "samples": samples}, indent=2), encoding="utf-8")
            print(f"Wrote {len(samples)} samples to {args.output}")
    except (OSError, ValueError, RuntimeError) as exc:
        print(f"Deskweave: {exc}", file=sys.stderr)
        return 1
    return 0
