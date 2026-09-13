"""Local protocol and opt-in real installed-browser regression gates."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
from pathlib import Path
import socket
import struct
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

import psutil

from deskweave.browser import BrowserWorkspace, _CDP, _key_spec, discover_browser
from deskweave.control import Refused


class BrowserContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="DeskweaveBrowserUnit-")
        self.workspace = BrowserWorkspace(self.temp.name, width=640, height=480)

    def tearDown(self):
        self.workspace.stop()
        self.temp.cleanup()

    def test_private_environment_drops_secrets_and_host_display(self):
        with patch.dict(os.environ, {"ANTHROPIC_API_KEY": "fixture-secret", "OPENAI_API_KEY": "fixture-secret",
                                     "DISPLAY": ":owner", "WAYLAND_DISPLAY": "owner", "HTTP_PROXY": "owner"}):
            env = self.workspace._environment()
        for name in ("ANTHROPIC_API_KEY", "OPENAI_API_KEY", "DISPLAY", "WAYLAND_DISPLAY", "HTTP_PROXY"):
            self.assertNotIn(name, env)
        self.assertEqual(Path(env["HOME"]), self.workspace.root / "home")
        self.assertEqual(Path(env["LOCALAPPDATA"]), self.workspace.root / "home/AppData/Local")

    def test_profile_cannot_be_claimed_twice_in_same_process(self):
        self.workspace._environment()
        self.workspace._claim_profile()
        other = BrowserWorkspace(self.temp.name)
        with self.assertRaises(Refused):
            other._claim_profile()
        other.stop()
        self.assertTrue(self.workspace._lockfile.exists())
        self.workspace.stop()
        self.assertFalse(self.workspace._lockfile.exists())

    def test_file_urls_cannot_escape_workspace(self):
        self.workspace._environment()
        for url in (Path(self.temp.name).as_uri(), "file://remote/share/file.html", "javascript:alert(1)",
                    "data:text/html,test", "https://user:password@example.test", "https://[invalid", "http://localhost:bad"):
            with self.subTest(url=url), self.assertRaises(Refused):
                self.workspace.navigate(url)

    def test_invalid_request_is_refused_before_connection(self):
        for operation in (lambda: self.workspace.click(-1, 20), lambda: self.workspace.click(float("nan"), 20),
                          lambda: self.workspace.click(1, 1, True), lambda: self.workspace.scroll(0, 21),
                          lambda: self.workspace.type_text("\x00"), lambda: self.workspace.type_text("x" * 4097),
                          lambda: self.workspace.key("Ctrl+Ctrl+A"), lambda: self.workspace.launch("terminal"),
                          lambda: self.workspace.run("echo hello"), lambda: self.workspace.run(["x"], 61),
                          lambda: self.workspace.run([""]), lambda: self.workspace.run(["x", "\u6f22" * 11000])):
            with self.assertRaises(Refused):
                operation()

    def test_keyboard_browser_event_names_and_releases(self):
        self.assertEqual(_key_spec("Ctrl+a")[1], ("a", "KeyA", 65))
        self.assertEqual(_key_spec("Meta+ArrowLeft")[1][0], "ArrowLeft")
        self.assertEqual(_key_spec("Shift+Tab")[1][0], "Tab")
        connection = Mock()
        with patch.object(self.workspace, "_connection", return_value=connection):
            self.workspace.key("Ctrl+Shift+A")
        events = [call.args[1] for call in connection.call.call_args_list]
        self.assertEqual([event["type"] for event in events],
                         ["rawKeyDown", "rawKeyDown", "keyDown", "keyUp", "keyUp", "keyUp"])
        self.assertEqual(events[-1]["modifiers"], 0)
        self.assertEqual(events[-2]["modifiers"], 2)

    def test_interrupted_input_still_attempts_all_releases(self):
        connection = Mock()
        connection.call.side_effect = [None, None, Refused("fixture timeout"), None, None, None]
        with patch.object(self.workspace, "_connection", return_value=connection), self.assertRaises(Refused):
            self.workspace.key("Ctrl+Shift+A")
        events = [call.args[1] for call in connection.call.call_args_list]
        self.assertEqual([event["type"] for event in events[-3:]], ["keyUp", "keyUp", "keyUp"])
        self.assertEqual(events[-1]["modifiers"], 0)
        connection.reset_mock(side_effect=True)
        connection.call.side_effect = [Refused("fixture timeout"), None]
        with patch.object(self.workspace, "_connection", return_value=connection), self.assertRaises(Refused):
            self.workspace.click(20, 20)
        self.assertEqual(connection.call.call_args_list[-1].args[1]["type"], "mouseReleased")

    def test_incomplete_stop_preserves_profile_ownership_and_reports_failure(self):
        self.workspace._environment()
        self.workspace._claim_profile()
        process = Mock(spec=psutil.Process)
        with patch.object(self.workspace, "_refresh_processes", return_value=[process]), \
                patch.object(self.workspace, "_terminate"), self.assertRaises(Refused):
            self.workspace.stop()
        self.assertTrue(self.workspace._lockfile.exists())
        self.assertIsNotNone(self.workspace.status()["error"])

    def test_unicode_uses_insert_text_without_global_keyboard(self):
        connection = Mock()
        with patch.object(self.workspace, "_connection", return_value=connection):
            self.workspace.type_text("Deskweave café 漢字 🙂")
        connection.call.assert_called_once_with("Input.insertText", {"text": "Deskweave café 漢字 🙂"})

    def test_endpoint_must_be_page_on_exact_loopback_port(self):
        for endpoint in ("ws://example.test:1234/devtools/page/1", "ws://127.0.0.1:9999/devtools/page/1",
                         "ws://127.0.0.1:1234/devtools/browser/1", "wss://127.0.0.1:1234/devtools/page/1"):
            with self.assertRaises(Refused):
                _CDP(endpoint, 1234)

    def test_discovery_refuses_nonexistent_explicit_override(self):
        with self.assertRaises(Refused):
            discover_browser(Path(self.temp.name) / "missing-browser")

    def test_pid_reuse_does_not_terminate_unrelated_process(self):
        dead_identity = Mock(spec=psutil.Process)
        dead_identity.is_running.return_value = False
        with patch("deskweave.browser.psutil.wait_procs", return_value=([], [])):
            BrowserWorkspace._terminate([dead_identity])
        dead_identity.terminate.assert_not_called()
        dead_identity.kill.assert_not_called()


@unittest.skipUnless(os.environ.get("DESKWEAVE_BROWSER_TEST") == "1", "Opt in to the real installed-browser gate.")
class RealBrowserTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="DeskweaveBrowser-")
        self.workspace = BrowserWorkspace(self.temp.name, width=640, height=480,
                                          browser_path=os.environ.get("DESKWEAVE_BROWSER_EXECUTABLE"))
        try:
            self.workspace.start()
        except Exception:
            self.workspace.stop()
            self.temp.cleanup()
            raise

    def tearDown(self):
        self.workspace.stop()
        self.temp.cleanup()

    def evaluate(self, expression):
        result = self.workspace._connection().call("Runtime.evaluate", {"expression": expression, "returnByValue": True})
        self.assertNotIn("exceptionDetails", result)
        return result["result"].get("value")

    def test_real_fixture_input_frame_profile_navigation_restart_and_cleanup(self):
        fixture = self.workspace.files / "fixture-25% café.html"
        fixture.write_text("""<!doctype html><meta charset=utf-8><title>Deskweave fixture</title>
<style>body{margin:0;background:#e2ecdf;height:1800px}input,button,output{position:absolute;font:20px sans-serif}
input{left:20px;top:20px;width:240px;height:32px}button{left:20px;top:80px;width:120px;height:36px}
output{left:20px;top:140px}</style><input id=editor><button id=apply>Apply</button><output id=result>Waiting</output>
<script>apply.onclick=()=>{result.textContent=editor.value;document.body.style.background='#80b080'};</script>""", encoding="utf-8")
        self.assertTrue(self.workspace.navigate(fixture.as_uri())["ok"])
        original = self.workspace.screenshot()
        self.assertEqual(struct.unpack(">II", original[16:24]), (640, 480))
        pid = self.workspace._process.pid
        self.workspace.click(80, 35)
        self.workspace.type_text("Deskweave café 漢字 🙂")
        self.assertEqual(self.evaluate("editor.value"), "Deskweave café 漢字 🙂")
        self.workspace.key("Meta+A" if sys.platform == "darwin" else "Ctrl+A")
        self.workspace.type_text("Confirmed Unicode ✓")
        self.workspace.click(80, 98)
        self.assertEqual(self.evaluate("result.textContent"), "Confirmed Unicode ✓")
        changed = self.workspace.screenshot()
        self.assertNotEqual(changed, original)
        self.workspace.scroll(0, 3)
        deadline = time.monotonic() + 2
        while self.evaluate("window.scrollY") == 0 and time.monotonic() < deadline:
            time.sleep(0.03)
        self.assertGreater(self.evaluate("window.scrollY"), 0)
        self.assertEqual(struct.unpack(">II", self.workspace.screenshot()[16:24]), (640, 480))
        with self.assertRaises(Refused):
            self.workspace.navigate((self.workspace.files / "missing.html").as_uri())
        # A listening loopback socket without an HTTP server cannot supply a page.
        # Close it immediately before navigation to exercise a real protocol error.
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            unused_port = reserved.getsockname()[1]
        with self.assertRaises(Refused):
            self.workspace.navigate(f"http://127.0.0.1:{unused_port}/deskweave-fixture")
        self.assertTrue(self.workspace.running)
        self.assertTrue(self.workspace.navigate(fixture.as_uri())["ok"])
        self.assertEqual(self.workspace._process.pid, pid)
        self.assertTrue(self.workspace.launch("browser")["reused"])
        with self.assertRaises(Refused):
            self.workspace.launch("terminal")
        status = self.workspace.status()
        self.assertGreater(status["processCount"], 1)
        self.assertGreater(status["rssBytes"], 0)
        self.assertGreaterEqual(status["cpuSeconds"], 0)
        command = psutil.Process(pid).cmdline()
        self.assertIn(f"--user-data-dir={self.workspace.profile}", command)
        self.assertFalse(any(arg in ("--no-sandbox", "--single-process", "--disable-web-security") for arg in command))
        self.evaluate("localStorage.setItem('deskweave-fixture', 'retained')")
        owned = [(p["pid"], p["created"]) for p in status["processes"]]
        self.workspace.stop()
        self.assertFalse(self.workspace.status()["running"])
        self.assertTrue(fixture.exists())
        for process_id, created in owned:
            try:
                process = psutil.Process(process_id)
                self.assertFalse(process.is_running() and process.create_time() == created)
            except psutil.NoSuchProcess:
                pass
        self.workspace.start()
        self.workspace.navigate(fixture.as_uri())
        self.assertEqual(self.evaluate("localStorage.getItem('deskweave-fixture')"), "retained")

    def test_web_only_command_refusal_starts_no_subprocess(self):
        with patch.object(self.workspace, "_spawn") as spawn, self.assertRaises(Refused):
            self.workspace.run([sys.executable, "-c", "raise SystemExit('must not execute')"])
        spawn.assert_not_called()
        self.assertFalse(self.workspace.status()["commandsSupported"])

    def test_attachment_download_is_saved_in_owned_folder_with_exact_contents(self):
        payload = "Deskweave local attachment: caf\u00e9 \u6f22\u5b57\n".encode("utf-8")

        class Attachment(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Disposition", 'attachment; filename="DeskweaveFixture.txt"')
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Attachment)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        original_url = self.workspace.status()["url"]
        try:
            with self.assertRaises(Refused):
                self.workspace.navigate(f"http://127.0.0.1:{server.server_port}/attachment")
            download = self.workspace.downloads / "DeskweaveFixture.txt"
            deadline = time.monotonic() + 10
            while not download.is_file() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertEqual(download.read_bytes(), payload)
            self.assertEqual(self.workspace.status()["downloadDirectory"], str(self.workspace.files / "downloads"))
            self.assertEqual(self.workspace.status()["url"], original_url)
            self.assertEqual(self.evaluate("location.href"), original_url)
            self.workspace.stop()
            self.assertEqual(download.read_bytes(), payload)
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=2)

    def test_unavailable_download_policy_cleans_startup_before_page_navigation(self):
        other = BrowserWorkspace(self.workspace.root / "policy-failure")
        original = _CDP.call
        called = []

        def fail_policy(connection, method, *args, **kwargs):
            called.append(method)
            if method == "Browser.setDownloadBehavior":
                raise Refused("Fixture: required download policy unavailable")
            return original(connection, method, *args, **kwargs)

        try:
            with patch.object(_CDP, "call", fail_policy), self.assertRaises(Refused):
                other.start()
            self.assertNotIn("Page.navigate", called)
            self.assertFalse(other.status()["running"])
            self.assertEqual(other.status()["processCount"], 0)
            self.assertFalse(other._lockfile.exists())
        finally:
            other.stop()

    def test_failed_browser_start_cleans_owned_process_and_profile_lock(self):
        other = BrowserWorkspace(self.workspace.root / "failed-start", browser_path=sys.executable)
        try:
            with self.assertRaises(Refused):
                other.start()
            self.assertFalse(other.status()["running"])
            self.assertEqual(other.status()["processCount"], 0)
            self.assertFalse(other._lockfile.exists())
        finally:
            other.stop()


if __name__ == "__main__":
    unittest.main()
