"""Loopback viewer and explicit owner/agent arbitration for one owned workspace."""
from __future__ import annotations

import asyncio
import base64
import hmac
import json
from pathlib import Path
import secrets
import sys
import time
from urllib.parse import urlsplit

from aiohttp import WSMsgType, web

from . import __version__
from .control import Control, Refused


def token_equal(value, expected):
    return isinstance(value, str) and hmac.compare_digest(value.encode("utf-8"), expected.encode("utf-8"))


def container_memory():
    result = {"containerMemoryBytes": None, "containerMemoryPeakBytes": None, "limits": {"memoryMiB": None}}
    if sys.platform != "linux" or not (Path("/.dockerenv").exists() or Path("/run/.containerenv").exists()):
        return result
    for filename, field in (("memory.current", "containerMemoryBytes"), ("memory.peak", "containerMemoryPeakBytes"), ("memory.max", "limit")):
        try:
            value = (Path("/sys/fs/cgroup") / filename).read_text().strip()
            if value.isdecimal():
                if field == "limit":
                    result["limits"]["memoryMiB"] = int(value) / 1048576
                else:
                    result[field] = int(value)
        except OSError:
            pass
    return result


class Broker:
    def __init__(self, workspace, *, owner_token=None, agent_token=None, host_platform=None, shutdown=None):
        self.workspace = workspace
        self.owner_token = owner_token or secrets.token_urlsafe(32)
        self.agent_token = agent_token or secrets.token_urlsafe(32)
        self.cookie = "deskweave_" + secrets.token_hex(8)
        self.sessions: set[str] = set()
        self.control = Control()
        self.lock = asyncio.Lock()
        self.viewers: set[web.WebSocketResponse] = set()
        self.host_platform = host_platform or sys.platform
        self.active_action: str | None = None
        self.closed = False
        self.last_state = None
        self.last_sample = 0.0
        self.shutdown = shutdown

    def owner(self, request):
        cookie = request.cookies.get(self.cookie, "")
        bearer = request.headers.get("Authorization", "")
        return cookie in self.sessions or token_equal(bearer, "Bearer " + self.owner_token)

    async def call(self, method, *args, **kwargs):
        operation = getattr(self.workspace, method, None)
        if not callable(operation):
            raise Refused(f"This workspace does not support {method}.")
        task = asyncio.create_task(asyncio.to_thread(operation, *args, **kwargs))
        try:
            return await asyncio.shield(task)
        except asyncio.CancelledError:
            # A cancelled HTTP task cannot release the operation lock while its
            # synchronous process/input operation is still running in a thread.
            try:
                await task
            finally:
                raise

    async def status(self):
        if self.active_action and self.last_state is not None:
            result = dict(self.last_state)
        else:
            result = await self.call("status")
            self.last_state = dict(result)
            self.last_sample = time.monotonic()
        browser = result.get("backend") == "browser" or not hasattr(self.workspace, "display")
        return {**result, "version": __version__, "hostPlatform": self.host_platform,
                "transport": "frames" if browser else "rfb", "workload": "web" if browser else "linux",
                "commandsSupported": result.get("commandsSupported", not browser),
                "agentEnabled": self.control.enabled, "controller": self.control.controller,
                "activeAction": self.active_action, "metricsAgeSeconds": round(time.monotonic() - self.last_sample, 2),
                **container_memory()}

    async def viewers_close(self, app=None):
        await asyncio.gather(*(ws.close(code=1000, message=b"Control changed") for ws in list(self.viewers)))

    async def input_enabled(self, enabled):
        if (await self.call("status")).get("running"):
            await self.call("set_viewer_input", enabled)

    async def cleanup(self, app):
        self.closed = True
        self.control.set_enabled(False)
        await self.viewers_close()
        async with self.lock:
            await self.call("stop")


def make_app(broker: Broker, web_root: Path | None = None):
    root = web_root or Path(__file__).resolve().parents[1] / "web"

    @web.middleware
    async def guard(request, handler):
        # Host validation also rejects DNS rebinding. No remote-host deployment mode.
        try:
            host = urlsplit("http://" + request.host).hostname
        except ValueError:
            raise web.HTTPBadRequest(text="Invalid host")
        if host not in ("localhost", "127.0.0.1", "::1"):
            raise web.HTTPForbidden(text="Deskweave accepts loopback hosts only")
        origin = request.headers.get("Origin")
        if origin and origin != f"http://{request.host}":
            raise web.HTTPForbidden(text="Origin refused")
        if request.method != "GET":
            if request.headers.get("X-Deskweave-Request") != "1":
                raise web.HTTPForbidden(text="Missing request marker")
            if request.content_type != "application/json":
                raise web.HTTPUnsupportedMediaType(text="JSON required")
        path = request.path
        if path == "/api/agent-call":
            if not token_equal(request.headers.get("Authorization", ""), "Bearer " + broker.agent_token):
                raise web.HTTPUnauthorized(text="Agent authentication required")
        elif path.startswith("/api/") and path != "/api/session" or path == "/rfb":
            if not broker.owner(request):
                raise web.HTTPUnauthorized(text="Open the private launch link to connect")
        try:
            response = await handler(request)
        except (ValueError, TypeError, KeyError) as exc:
            response = web.json_response({"error": str(exc)}, status=400)
        except Refused as exc:
            response = web.json_response({"error": str(exc)}, status=409)
        except (RuntimeError, OSError, asyncio.TimeoutError) as exc:
            response = web.json_response({"error": str(exc)}, status=503)
        response.headers.update({"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
                                 "Referrer-Policy": "no-referrer", "Cross-Origin-Resource-Policy": "same-origin",
                                 "Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"})
        return response

    app = web.Application(middlewares=[guard], client_max_size=64 * 1024)
    # An open viewer socket would otherwise hold the runner's graceful shutdown for its full timeout.
    app.on_shutdown.append(broker.viewers_close)
    app.on_cleanup.append(broker.cleanup)

    async def payload(request):
        data = await request.json()
        if not isinstance(data, dict):
            raise ValueError("Expected a JSON object")
        return data

    async def session(request):
        data = await payload(request)
        token = data.get("token", "")
        if not token_equal(token, broker.owner_token):
            raise web.HTTPUnauthorized(text="Invalid launch link")
        if len(broker.sessions) >= 16:
            broker.sessions.pop()
        value = secrets.token_urlsafe(32)
        broker.sessions.add(value)
        response = web.json_response({"connected": True})
        response.set_cookie(broker.cookie, value, httponly=True, samesite="Strict", path="/")
        return response

    async def status(request):
        return web.json_response(await broker.status())

    async def change(request):
        data = await payload(request)
        action = request.match_info["action"]
        if action == "shutdown":
            if broker.shutdown is None:
                raise Refused("This broker is managed by its embedding host")
            broker.control.set_enabled(False)
            broker.control.owner = False
            await broker.viewers_close()
            asyncio.get_running_loop().call_later(0.1, broker.shutdown)
            return web.json_response({"shuttingDown": True})
        if action in ("takeover", "release", "stop", "agent"):
            # Revoke before waiting for any in-flight operation. Accepted operations
            # can finish, but queued operations must re-check their lease under lock.
            if action == "takeover":
                broker.control.takeover()
            elif action == "agent":
                if type(data.get("enabled")) is not bool:
                    raise ValueError("enabled must be a boolean")
                broker.control.set_enabled(data["enabled"])
            else:
                broker.control.owner = False
                broker.control.revoke()
                if action == "stop":
                    broker.control.set_enabled(False)
            await broker.viewers_close()
        async with broker.lock:
            if action == "start":
                await broker.call("start")
                await broker.input_enabled(broker.control.owner)
            elif action == "stop":
                await broker.call("stop")
            elif action in ("takeover", "release", "agent"):
                await broker.input_enabled(broker.control.owner)
            elif action in ("launch", "navigate", "input", "history"):
                if broker.control.controller != "owner":
                    raise Refused("Take control before sending input or opening an application")
                if action in ("launch", "navigate"):
                    # Status serves the cached sample while a long owner action holds the backend.
                    broker.active_action = "workspace_" + action
                    try:
                        await broker.call(action, data.get("kind" if action == "launch" else "url"))
                    finally:
                        broker.active_action = None
                elif action == "history":
                    await broker.call("history", data.get("direction"))
                else:
                    await dispatch_input(broker, data)
            else:
                raise web.HTTPNotFound()
        return web.json_response(await broker.status())

    async def connect(request):
        await payload(request)
        async with broker.lock:
            state = await broker.status()
            if not state["running"] or state["transport"] != "rfb":
                raise Refused("The desktop is not running")
            return web.json_response({"password": broker.workspace.vnc_password,
                                      "width": state["width"], "height": state["height"]})

    async def frame(request):
        async with broker.lock:
            data = await broker.call("screenshot")
        return web.Response(body=data, content_type="image/png")

    async def relay(request):
        if len(broker.viewers) >= 4:
            raise Refused("Four viewers are already connected")
        async with broker.lock:
            state = await broker.status()
            if not state["running"] or state["transport"] != "rfb":
                raise Refused("Start a Linux desktop first")
            await broker.input_enabled(broker.control.owner)
            reader, writer = await asyncio.wait_for(asyncio.open_connection(broker.workspace.rfb_host, broker.workspace.rfb_port), 5)
        ws = web.WebSocketResponse(protocols=("binary",), max_msg_size=1024 * 1024, heartbeat=30, compress=False)
        try:
            await ws.prepare(request)
            broker.viewers.add(ws)

            async def inbound():
                async for msg in ws:
                    if msg.type == WSMsgType.BINARY:
                        writer.write(msg.data)
                        await writer.drain()
                    elif msg.type == WSMsgType.TEXT:
                        await ws.close(code=1003, message=b"Binary RFB required")
                        break

            async def outbound():
                while not ws.closed:
                    data = await reader.read(65536)
                    if not data:
                        break
                    await ws.send_bytes(data)

            tasks = [asyncio.create_task(inbound()), asyncio.create_task(outbound())]
            try:
                done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                for task in done:
                    task.result()
            finally:
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
        finally:
            broker.viewers.discard(ws)
            writer.close()
            await writer.wait_closed()
            await ws.close()
        return ws

    async def agent_call(request):
        data = await payload(request)
        client, name, args = data.get("client"), data.get("name"), data.get("arguments", {})
        if not isinstance(client, str) or not 1 <= len(client) <= 128 or not isinstance(args, dict):
            raise ValueError("A bounded client ID and argument object are required")
        if not broker.control.enabled:
            raise Refused("Agent access is off. Enable it in Deskweave first.")
        async with broker.lock:
            if not broker.control.enabled:
                raise Refused("Agent access was revoked")
            if name == "workspace_status":
                result = await broker.status()
            elif name == "workspace_acquire":
                result = broker.control.acquire(client)
                await broker.input_enabled(False)
            elif name == "workspace_release":
                broker.control.check(client, args.get("lease"))
                broker.control.revoke()
                result = {"released": True}
            elif name == "workspace_screenshot":
                result = {"png": base64.b64encode(await broker.call("screenshot")).decode("ascii")}
            else:
                broker.control.check(client, args.get("lease"))
                broker.active_action = name
                try:
                    if name == "workspace_start":
                        result = await broker.call("start")
                        await broker.input_enabled(False)
                    elif name == "workspace_launch":
                        result = await broker.call("launch", args.get("kind"))
                    elif name == "workspace_navigate":
                        result = await broker.call("navigate", args.get("url"))
                    elif name == "workspace_run":
                        result = await broker.call("run", args.get("argv"), args.get("timeout", 30))
                    elif name == "workspace_input":
                        await dispatch_input(broker, args)
                        result = {"accepted": True, "note": "Input sent; inspect a screenshot to verify the result."}
                    else:
                        raise ValueError("Unknown workspace tool")
                finally:
                    broker.active_action = None
        return web.json_response({"result": result})

    async def asset(request):
        relative = request.match_info.get("asset", "") or "index.html"
        file = (root / relative).resolve()
        if not file.is_relative_to(root.resolve()) or not file.is_file() or any(p.startswith(".") for p in Path(relative).parts):
            raise web.HTTPNotFound()
        return web.FileResponse(file)

    app.router.add_post("/api/session", session)
    app.router.add_get("/api/status", status)
    app.router.add_post("/api/connect", connect)
    app.router.add_get("/api/frame", frame)
    app.router.add_post("/api/agent-call", agent_call)
    app.router.add_post("/api/{action}", change)
    app.router.add_get("/rfb", relay)
    app.router.add_get("/{asset:.*}", asset)
    return app


async def dispatch_input(broker, data):
    kind = data.get("kind")
    if kind == "click":
        await broker.call("click", data.get("x"), data.get("y"), data.get("button", 1))
    elif kind == "drag":
        await broker.call("drag", data.get("x"), data.get("y"), data.get("toX"), data.get("toY"))
    elif kind == "scroll":
        await broker.call("scroll", data.get("dx", 0), data.get("dy", 0))
    elif kind == "key":
        await broker.call("key", data.get("key"))
    elif kind == "text":
        await broker.call("type_text", data.get("text"))
    else:
        raise ValueError("Input kind must be click, drag, scroll, key, or text")
