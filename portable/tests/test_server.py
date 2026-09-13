"""Owner HTTP boundary and control races through the real loopback broker."""
import asyncio
import base64
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest

from aiohttp import CookieJar
from aiohttp.test_utils import TestClient, TestServer

from deskweave.server import Broker, make_app


PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")


class OwnedWorkspace:
    """Records backend calls; a gate makes accepted-action races deterministic."""

    def __init__(self):
        self.running = False
        self.calls = []
        self.fail_start = False
        self.entered = threading.Event()
        self.finish = threading.Event()
        self.viewer_input = False
        self.refuse_status_during_action = False

    def status(self):
        if self.refuse_status_during_action and self.entered.is_set() and not self.finish.is_set():
            raise RuntimeError("Status attempted to enter the busy backend")
        return {"backend": "browser", "commandsSupported": True, "running": self.running, "width": 640, "height": 480,
                "startedAt": "2026-09-09T12:00:00Z" if self.running else None,
                "processCount": 2 if self.running else 0, "rssBytes": 2048 if self.running else 0,
                "cpuSeconds": 1 if self.running else 0, "isolation": "test owned profile", "error": None}

    def start(self):
        self.calls.append(("start",))
        if self.fail_start:
            raise RuntimeError("fixture startup failed")
        self.running = True
        return self.status()

    def stop(self):
        self.calls.append(("stop",))
        self.running = False
        self.viewer_input = False
        return self.status()

    def set_viewer_input(self, enabled):
        self.calls.append(("viewer_input", enabled))
        self.viewer_input = enabled

    def launch(self, kind):
        self.calls.append(("launch", kind))
        return {"launched": kind}

    def navigate(self, url):
        self.calls.append(("navigate", url))
        return {"url": url}

    def click(self, x, y, button):
        self.calls.append(("click", x, y, button))

    def scroll(self, dx, dy):
        self.calls.append(("scroll", dx, dy))

    def key(self, key):
        self.calls.append(("key", key))

    def type_text(self, text):
        self.calls.append(("text", text))

    def screenshot(self):
        self.calls.append(("screenshot",))
        return PNG

    def run(self, argv, timeout):
        self.calls.append(("run", argv, timeout))
        self.entered.set()
        if not self.finish.wait(8):
            raise RuntimeError("fixture action was not released")
        self.calls.append(("run_finished",))
        return {"exitCode": 0, "stdout": "finished"}


class ServerBoundaryTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="DeskweaveHttpTest-")
        self.web_root = Path(self.temp.name) / "web"
        self.web_root.mkdir()
        (self.web_root / "index.html").write_text("<!doctype html><title>Fixture viewer</title>", encoding="utf-8")
        (self.web_root / ".private").write_text("private fixture", encoding="utf-8")
        (Path(self.temp.name) / "outside.txt").write_text("outside fixture", encoding="utf-8")
        self.workspace = OwnedWorkspace()
        self.broker = Broker(self.workspace, owner_token="owner-fixture-token", agent_token="agent-fixture-token", host_platform="darwin")
        self.client = TestClient(TestServer(make_app(self.broker, self.web_root)), cookie_jar=CookieJar(unsafe=True))
        await self.client.start_server()
        self.owner_headers = {"Authorization": "Bearer owner-fixture-token", "X-Deskweave-Request": "1"}
        self.agent_headers = {"Authorization": "Bearer agent-fixture-token", "X-Deskweave-Request": "1"}

    async def asyncTearDown(self):
        self.workspace.finish.set()
        await self.client.close()
        self.temp.cleanup()

    async def post(self, action, data=None, expected=200):
        response = await self.client.post("/api/" + action, json={} if data is None else data, headers=self.owner_headers)
        result = await response.json()
        self.assertEqual(response.status, expected, result)
        return result

    async def agent(self, name, arguments=None, client="agent-one", expected=200):
        response = await self.client.post("/api/agent-call", json={"client": client, "name": name, "arguments": arguments or {}}, headers=self.agent_headers)
        result = await response.json()
        self.assertEqual(response.status, expected, result)
        return result

    async def acquire(self, client="agent-one"):
        result = await self.agent("workspace_acquire", client=client)
        return result["result"]["lease"]

    async def until(self, predicate):
        deadline = time.monotonic() + 3
        while not predicate():
            if time.monotonic() >= deadline:
                self.fail("Concurrent request did not reach the expected boundary")
            await asyncio.sleep(.005)

    async def test_page_and_status_do_not_start_workspace_or_disclose_credentials(self):
        page = await self.client.get("/")
        self.assertEqual(page.status, 200)
        response = await self.client.get("/api/status", headers=self.owner_headers)
        state = await response.json()
        self.assertFalse(state["running"])
        self.assertEqual(state["hostPlatform"], "darwin")
        self.assertEqual(state["transport"], "frames")
        self.assertEqual(state["workload"], "web")
        self.assertEqual(self.workspace.calls, [])
        serialized = json.dumps(state) + await page.text()
        self.assertNotIn(self.broker.owner_token, serialized)
        self.assertNotIn(self.broker.agent_token, serialized)
        self.assertIn("no-store", response.headers["Cache-Control"])

    async def test_owner_routes_require_authentication_and_agent_token_is_not_owner(self):
        for path in ("/api/status", "/api/frame", "/rfb"):
            for auth in ({}, self.agent_headers):
                with self.subTest(path=path, auth=bool(auth)):
                    response = await self.client.get(path, headers=auth)
                    self.assertEqual(response.status, 401)
        for action in ("start", "stop", "takeover", "release", "input", "agent", "connect"):
            with self.subTest(action=action):
                response = await self.client.post("/api/" + action, json={}, headers={"X-Deskweave-Request": "1"})
                self.assertEqual(response.status, 401)
        self.assertEqual(self.workspace.calls, [])

    async def test_non_ascii_credentials_are_refused_without_server_error(self):
        response = await self.client.get("/api/status", headers={"Authorization": "Bearer café"})
        self.assertEqual(response.status, 401)
        response = await self.client.post("/api/agent-call", json={"client": "one", "name": "workspace_status", "arguments": {}},
                                          headers={"Authorization": "Bearer café", "X-Deskweave-Request": "1"})
        self.assertEqual(response.status, 401)
        response = await self.client.post("/api/session", json={"token": "日本語"}, headers={"X-Deskweave-Request": "1"})
        self.assertEqual(response.status, 401)
        await self.post("agent", {"enabled": True})
        await self.acquire()
        await self.agent("workspace_input", {"lease": "日本語", "kind": "key", "key": "Enter"}, expected=409)
        self.assertNotIn(("key", "Enter"), self.workspace.calls)

    async def test_private_launch_link_sets_http_only_cookie_and_invalid_token_is_refused(self):
        invalid = await self.client.post("/api/session", json={"token": "incorrect"}, headers={"X-Deskweave-Request": "1"})
        self.assertEqual(invalid.status, 401)
        response = await self.client.post("/api/session", json={"token": self.broker.owner_token}, headers={"X-Deskweave-Request": "1"})
        self.assertEqual(response.status, 200)
        cookie = response.cookies[self.broker.cookie]
        self.assertTrue(cookie["httponly"])
        self.assertEqual(cookie["samesite"], "Strict")
        self.assertNotEqual(cookie.value, self.broker.owner_token)
        status = await self.client.get("/api/status")
        self.assertEqual(status.status, 200)
        mutation = await self.client.post("/api/start", json={})
        self.assertEqual(mutation.status, 403)
        self.assertEqual(self.workspace.calls, [])

    async def test_origin_and_host_validation_precede_backend_side_effects(self):
        cases = [{"Host": "evil.example"}, {"Host": "127.0.0.1.evil.example"},
                 {"Origin": "https://evil.example"}, {"Origin": "null"},
                 {"Origin": "http://localhost:1"}]
        for headers in cases:
            with self.subTest(headers=headers):
                response = await self.client.post("/api/start", json={}, headers={**self.owner_headers, **headers})
                self.assertEqual(response.status, 403)
        self.assertEqual(self.workspace.calls, [])
        origin = str(self.client.make_url("/")).rstrip("/")
        response = await self.client.get("/api/status", headers={**self.owner_headers, "Origin": origin})
        self.assertEqual(response.status, 200)

    async def test_mutations_require_marker_json_and_object_payload(self):
        response = await self.client.post("/api/start", json={}, headers={"Authorization": "Bearer owner-fixture-token"})
        self.assertEqual(response.status, 403)
        response = await self.client.post("/api/start", data="{}", headers=self.owner_headers)
        self.assertEqual(response.status, 415)
        response = await self.client.post("/api/start", data="{invalid", headers={**self.owner_headers, "Content-Type": "application/json"})
        self.assertEqual(response.status, 400)
        for data in ([], True, None, "start"):
            response = await self.client.post("/api/start", data=json.dumps(data), headers={**self.owner_headers, "Content-Type": "application/json"})
            self.assertEqual(response.status, 400)
        self.assertEqual(self.workspace.calls, [])

    async def test_hidden_and_outside_files_are_not_served(self):
        for path in ("/.private", "/%2e%2e/outside.txt", "/missing"):
            response = await self.client.get(path)
            self.assertEqual(response.status, 404)

    async def test_start_does_not_grant_input_and_owner_release_revokes_it(self):
        state = await self.post("start")
        self.assertTrue(state["running"])
        self.assertIsNone(state["controller"])
        self.assertFalse(self.workspace.viewer_input)
        for action, data in (("input", {"kind": "click", "x": 5, "y": 6}), ("launch", {"kind": "browser"}), ("navigate", {"url": "https://example.test"})):
            await self.post(action, data, expected=409)
        self.assertNotIn(("click", 5, 6, 1), self.workspace.calls)
        await self.post("takeover")
        self.assertTrue(self.workspace.viewer_input)
        await self.post("input", {"kind": "click", "x": 5, "y": 6})
        self.assertIn(("click", 5, 6, 1), self.workspace.calls)
        state = await self.post("release")
        self.assertIsNone(state["controller"])
        self.assertFalse(self.workspace.viewer_input)
        await self.post("input", {"kind": "key", "key": "Enter"}, expected=409)
        self.assertNotIn(("key", "Enter"), self.workspace.calls)

    async def test_owner_input_payloads_reach_backend_without_coordinate_or_text_changes(self):
        await self.post("start")
        await self.post("takeover")
        inputs = [({"kind": "click", "x": 639, "y": 479, "button": 3}, ("click", 639, 479, 3)),
                  ({"kind": "scroll", "dx": -2, "dy": 5}, ("scroll", -2, 5)),
                  ({"kind": "key", "key": "Meta+ArrowLeft"}, ("key", "Meta+ArrowLeft")),
                  ({"kind": "text", "text": "café 日本語\nline"}, ("text", "café 日本語\nline"))]
        for data, call in inputs:
            await self.post("input", data)
            self.assertIn(call, self.workspace.calls)
        await self.post("input", {"kind": "unknown"}, expected=400)
        frame = await self.client.get("/api/frame", headers=self.owner_headers)
        self.assertEqual(frame.status, 200)
        self.assertEqual(frame.content_type, "image/png")
        self.assertEqual(await frame.read(), PNG)

    async def test_agent_authentication_and_enablement_are_distinct_from_owner(self):
        for token in ("owner-fixture-token", "wrong"):
            response = await self.client.post("/api/agent-call", json={"client": "one", "name": "workspace_status", "arguments": {}},
                                              headers={"Authorization": "Bearer " + token, "X-Deskweave-Request": "1"})
            self.assertEqual(response.status, 401)
        await self.agent("workspace_status", expected=409)
        await self.post("agent", {"enabled": True})
        state = await self.agent("workspace_status")
        self.assertTrue(state["result"]["agentEnabled"])
        for value in ("true", 1, None):
            await self.post("agent", {"enabled": value}, expected=400)
        await self.post("agent", {"enabled": False})
        await self.agent("workspace_status", expected=409)

    async def test_agent_leases_conflict_expire_and_reject_stale_tickets(self):
        await self.post("agent", {"enabled": True})
        lease = await self.acquire()
        self.assertEqual(await self.acquire(), lease)
        await self.agent("workspace_acquire", client="agent-two", expected=409)
        await self.agent("workspace_input", {"lease": "wrong", "kind": "key", "key": "Enter"}, expected=409)
        self.broker.control.expires = time.monotonic() - 1
        await self.agent("workspace_input", {"lease": lease, "kind": "key", "key": "Enter"}, expected=409)
        fresh = await self.acquire("agent-two")
        self.assertNotEqual(fresh, lease)
        await self.agent("workspace_input", {"lease": lease, "kind": "key", "key": "Enter"}, expected=409)
        self.assertNotIn(("key", "Enter"), self.workspace.calls)

    async def test_owner_takeover_requires_explicit_release_before_agent_can_acquire(self):
        await self.post("agent", {"enabled": True})
        old = await self.acquire()
        await self.post("takeover")
        await self.agent("workspace_acquire", expected=409)
        await self.agent("workspace_input", {"lease": old, "kind": "key", "key": "Enter"}, expected=409)
        await self.post("release")
        self.assertNotEqual(await self.acquire(), old)

    async def test_agent_eligibility_changes_preserve_owner_control_until_release(self):
        await self.post("start")
        await self.post("takeover")
        for enabled in (True, False, True):
            state = await self.post("agent", {"enabled": enabled})
            self.assertEqual(state["controller"], "owner")
            self.assertTrue(self.workspace.viewer_input)
            await self.agent("workspace_acquire", expected=409)
        await self.post("release")
        self.assertTrue(await self.acquire())

    async def test_takeover_revokes_queued_agent_write_before_accepted_action_finishes(self):
        await self.post("start")
        await self.post("agent", {"enabled": True})
        lease = await self.acquire()
        accepted = asyncio.create_task(self.agent("workspace_run", {"lease": lease, "argv": ["fixture"], "timeout": 30}))
        pending = takeover = None
        try:
            await self.until(self.workspace.entered.is_set)
            state = await self.client.get("/api/status", headers=self.owner_headers)
            self.assertEqual((await state.json())["activeAction"], "workspace_run")
            pending = asyncio.create_task(self.agent("workspace_input", {"lease": lease, "kind": "key", "key": "Enter"}, expected=409))
            await asyncio.sleep(.02)
            takeover = asyncio.create_task(self.post("takeover"))
            await self.until(lambda: self.broker.control.controller == "owner")
            self.assertFalse(accepted.done())
            self.assertFalse(takeover.done())
            self.workspace.finish.set()
            result, _, _ = await asyncio.gather(accepted, pending, takeover)
            self.assertEqual(result["result"]["exitCode"], 0)
            self.assertIn(("run_finished",), self.workspace.calls)
            self.assertNotIn(("key", "Enter"), self.workspace.calls)
            self.assertTrue(self.workspace.viewer_input)
            self.assertIsNone(self.broker.active_action)
        finally:
            self.workspace.finish.set()
            await asyncio.gather(*(item for item in (accepted, pending, takeover) if item), return_exceptions=True)

    async def test_disabling_agent_revokes_queued_action_while_accepted_action_completes(self):
        await self.post("agent", {"enabled": True})
        lease = await self.acquire()
        accepted = asyncio.create_task(self.agent("workspace_run", {"lease": lease, "argv": ["fixture"], "timeout": 30}))
        pending = disable = None
        try:
            await self.until(self.workspace.entered.is_set)
            pending = asyncio.create_task(self.agent("workspace_input", {"lease": lease, "kind": "text", "text": "stale"}, expected=409))
            await asyncio.sleep(.02)
            disable = asyncio.create_task(self.post("agent", {"enabled": False}))
            await self.until(lambda: not self.broker.control.enabled)
            self.workspace.finish.set()
            await asyncio.gather(accepted, pending, disable)
            self.assertNotIn(("text", "stale"), self.workspace.calls)
            self.assertIsNone(self.broker.control.controller)
        finally:
            self.workspace.finish.set()
            await asyncio.gather(*(item for item in (accepted, pending, disable) if item), return_exceptions=True)

    async def test_failed_start_is_reported_and_can_be_retried_then_stopped(self):
        self.workspace.fail_start = True
        result = await self.post("start", expected=503)
        self.assertIn("fixture startup failed", result["error"])
        self.assertFalse(self.workspace.running)
        self.workspace.fail_start = False
        await self.post("start")
        await self.post("takeover")
        await self.post("agent", {"enabled": True})
        state = await self.post("stop")
        self.assertFalse(state["running"])
        self.assertIsNone(state["controller"])
        self.assertFalse(state["agentEnabled"])
        self.assertFalse(self.workspace.viewer_input)

    async def test_cleanup_waits_for_accepted_action_then_stops_owned_workspace(self):
        await self.post("start")
        await self.post("agent", {"enabled": True})
        lease = await self.acquire()
        accepted = asyncio.create_task(self.agent("workspace_run", {"lease": lease, "argv": ["fixture"], "timeout": 30}))
        cleanup = None
        try:
            await self.until(self.workspace.entered.is_set)
            cleanup = asyncio.create_task(self.broker.cleanup(None))
            await self.until(lambda: self.broker.closed)
            self.assertFalse(self.broker.control.enabled)
            self.assertFalse(cleanup.done())
            self.workspace.finish.set()
            await asyncio.gather(accepted, cleanup)
            self.assertLess(self.workspace.calls.index(("run_finished",)), self.workspace.calls.index(("stop",)))
            self.assertFalse(self.workspace.running)
        finally:
            self.workspace.finish.set()
            await asyncio.gather(*(item for item in (accepted, cleanup) if item), return_exceptions=True)

    async def test_status_remains_responsive_using_aged_sample_during_accepted_action(self):
        await self.post("start")
        await self.post("agent", {"enabled": True})
        lease = await self.acquire()
        self.workspace.refuse_status_during_action = True
        accepted = asyncio.create_task(self.agent("workspace_run", {"lease": lease, "argv": ["fixture"], "timeout": 30}))
        try:
            await self.until(self.workspace.entered.is_set)
            self.broker.last_sample -= 5
            response = await asyncio.wait_for(self.client.get("/api/status", headers=self.owner_headers), 1)
            self.assertEqual(response.status, 200)
            state = await response.json()
            self.assertEqual(state["activeAction"], "workspace_run")
            self.assertGreaterEqual(state["metricsAgeSeconds"], 5)
            self.assertEqual(state["processCount"], 2)
            self.assertFalse(accepted.done())
        finally:
            self.workspace.finish.set()
            await accepted

    async def test_cancelled_operation_keeps_lock_until_its_thread_finishes(self):
        async def operation():
            async with self.broker.lock:
                await self.broker.call("run", ["fixture"], 30)

        followup_entered = asyncio.Event()

        async def followup():
            async with self.broker.lock:
                followup_entered.set()
                self.workspace.calls.append(("followup",))

        action = asyncio.create_task(operation())
        pending = None
        try:
            await self.until(self.workspace.entered.is_set)
            action.cancel()
            pending = asyncio.create_task(followup())
            await asyncio.sleep(.02)
            self.assertTrue(self.broker.lock.locked())
            self.assertFalse(action.done())
            self.assertFalse(followup_entered.is_set())
            self.workspace.finish.set()
            with self.assertRaises(asyncio.CancelledError):
                await action
            await pending
            self.assertLess(self.workspace.calls.index(("run_finished",)), self.workspace.calls.index(("followup",)))
        finally:
            self.workspace.finish.set()
            await asyncio.gather(*(item for item in (action, pending) if item), return_exceptions=True)


if __name__ == "__main__":
    unittest.main()
