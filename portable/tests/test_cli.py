"""Private launcher ownership, malformed-state handling, and source-kit boundaries."""
import argparse
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import AsyncMock, Mock, patch
import zipfile

from deskweave import cli


class LauncherTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="DeskweaveLauncherTest-")
        self.root = Path(self.temp.name)
        self.connection = self.root / "connection.json"
        self.valid = {"url": "http://127.0.0.1:8765", "ownerToken": "owner-fixture-token", "agentToken": "agent-fixture-token"}

    def tearDown(self):
        self.temp.cleanup()

    def write(self, data):
        self.connection.write_text(json.dumps(data), encoding="utf-8")

    def test_connection_accepts_explicit_loopback_ports_and_normalizes_trailing_slash(self):
        for url in ("http://127.0.0.1:8765", "http://localhost:4000/", "http://[::1]:65432/"):
            with self.subTest(url=url):
                self.write({**self.valid, "url": url})
                self.assertEqual(cli.load_connection(self.connection)["url"], url.rstrip("/"))

    def test_connection_refuses_remote_ambiguous_or_incomplete_addresses(self):
        for url in ("https://127.0.0.1:8765", "http://example.test:8765", "http://127.0.0.1:8765.evil.test",
                    "http://127.0.0.1", "http://127.0.0.1:0", "http://localhost:not-a-port", "http://localhost:99999",
                    "http://user:password@127.0.0.1:8765", "http://127.0.0.1:8765/api", "http://127.0.0.1:8765?",
                    "http://127.0.0.1:8765?x=1", "http://127.0.0.1:8765#", "http://127.0.0.1:8765#token=x",
                    " http://127.0.0.1:8765", "http://local\nhost:8765", "http://[::1", "file:///private"):
            with self.subTest(url=url):
                self.write({**self.valid, "url": url})
                with self.assertRaises(ValueError):
                    cli.load_connection(self.connection)

    def test_connection_bad_shapes_and_tokens_report_setup_error_without_secrets(self):
        bad = [None, [], "secret-fixture", {}, {"url": None}, {"url": 123}, {"url": "http://127.0.0.1:8765"}]
        for token in (None, 123, "", "secret\nfixture", "secret#fixture", "日本語", "x" * 513):
            bad.append({**self.valid, "ownerToken": token})
            bad.append({**self.valid, "agentToken": token})
        for data in bad:
            with self.subTest(data_type=type(data).__name__):
                self.write(data)
                with self.assertRaisesRegex(ValueError, "Restart the Deskweave launcher") as raised:
                    cli.load_connection(self.connection)
                self.assertNotIn("secret-fixture", str(raised.exception))
        self.connection.write_text('{"ownerToken":"secret-fixture",', encoding="utf-8")
        with self.assertRaises(ValueError) as raised:
            cli.load_connection(self.connection)
        self.assertNotIn("secret-fixture", str(raised.exception))

    def test_open_uses_private_fragment_without_printing_tokens(self):
        self.write(self.valid)
        output = io.StringIO()
        with patch.object(cli.webbrowser, "open", return_value=True) as opened, redirect_stdout(output), redirect_stderr(output):
            result = cli.main(["open", "--connection", str(self.connection)])
        self.assertEqual(result, 0)
        opened.assert_called_once_with("http://127.0.0.1:8765/#token=owner-fixture-token")
        self.assertNotIn("owner-fixture-token", output.getvalue())
        self.assertNotIn("agent-fixture-token", output.getvalue())

    def test_bad_connection_exits_cleanly_without_opening_a_browser(self):
        self.write({})
        output = io.StringIO()
        with patch.object(cli.webbrowser, "open") as opened, redirect_stderr(output):
            result = cli.main(["open", "--connection", str(self.connection)])
        self.assertEqual(result, 1)
        self.assertIn("Restart the Deskweave launcher", output.getvalue())
        self.assertNotIn("Traceback", output.getvalue())
        opened.assert_not_called()

    def test_private_json_atomically_replaces_old_credentials_and_removes_pending_file(self):
        self.write({"ownerToken": "old-token"})
        pending = self.connection.with_suffix(".pending")
        pending.write_text("old incomplete content", encoding="utf-8")
        if os.name != "nt":
            pending.chmod(0o666)
        cli.private_json(self.connection, self.valid)
        self.assertEqual(json.loads(self.connection.read_text(encoding="utf-8")), self.valid)
        self.assertFalse(pending.exists())
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.connection.stat().st_mode), 0o600)

    def test_instance_lock_refuses_second_broker_and_releases_for_next_launch(self):
        first = cli.InstanceLock(self.root)
        try:
            with self.assertRaisesRegex(RuntimeError, "already has a running"):
                cli.InstanceLock(self.root)
        finally:
            first.close()
        second = cli.InstanceLock(self.root)
        second.close()

    def test_automatic_backend_selection_does_not_require_vm_on_mac_or_windows(self):
        for platform, expected in (("darwin", "browser"), ("win32", "browser"), ("linux", "desktop")):
            with patch.object(cli.sys, "platform", platform):
                self.assertEqual(cli.backend_name("auto"), expected)
                self.assertEqual(cli.backend_name("browser"), "browser")


class LauncherCleanupTests(unittest.IsolatedAsyncioTestCase):
    async def test_setup_and_cleanup_failures_still_remove_connection_and_release_os_lock(self):
        with tempfile.TemporaryDirectory(prefix="DeskweaveCleanupTest-") as folder:
            root = Path(folder)
            connection = root / "connection.json"
            connection.write_text('{"ownerToken":"fixture-stale"}', encoding="utf-8")
            args = argparse.Namespace(data_dir=root, host_platform="win32", backend="browser", container_bind=False, port=0, open=False)
            runner = Mock(setup=AsyncMock(side_effect=RuntimeError("fixture setup failure")),
                          cleanup=AsyncMock(side_effect=OSError("fixture cleanup failure")))
            with patch.object(cli, "workspace", return_value=Mock()), patch.object(cli.web, "AppRunner", return_value=runner):
                with self.assertRaisesRegex(OSError, "fixture cleanup failure"):
                    await cli.serve(args)
            runner.cleanup.assert_awaited_once()
            self.assertFalse(connection.exists())
            replacement = cli.InstanceLock(root)
            replacement.close()


class SourceKitTests(unittest.TestCase):
    def setUp(self):
        script = Path(__file__).resolve().parents[1] / "scripts/package.py"
        spec = importlib.util.spec_from_file_location("deskweave_package_fixture", script)
        self.package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.package)
        self.temp = tempfile.TemporaryDirectory(prefix="DeskweavePackageTest-")
        self.root = Path(self.temp.name)
        self.package.ROOT = self.root
        vendor = self.root / "web/vendor/novnc/core/rfb.js"
        vendor.parent.mkdir(parents=True)
        vendor.write_text("// pinned viewer fixture", encoding="utf-8")
        scripts = self.root / "scripts"
        scripts.mkdir()
        self.lock = {"files": {"core/rfb.js": hashlib.sha256(vendor.read_bytes()).hexdigest()}}
        (scripts / "novnc-lock.json").write_text(json.dumps(self.lock), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def test_archive_excludes_profiles_credentials_virtualenv_and_build_outputs(self):
        allowed = ("README.md", "start.sh", "Start Deskweave.command", "web/app.js", "deskweave/cli.py", "tests/test_server.py")
        excluded = ("connection.json", ".env", ".state/container-connection.json", ".venv/secret.py", "profiles/cookies.json",
                    "artifacts/fixture.png", "out/private.txt", "deskweave/__pycache__/cli.pyc")
        for relative in allowed + excluded:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(relative, encoding="utf-8")
        with redirect_stdout(io.StringIO()):
            self.package.main()
        archive_path = self.root / "dist/Deskweave-Portable-0.1.0-test-kit.zip"
        manifest = json.loads(archive_path.with_suffix(".manifest.json").read_text(encoding="utf-8"))
        with zipfile.ZipFile(archive_path) as archive:
            names = set(archive.namelist())
            for relative in allowed:
                self.assertIn("Deskweave-Portable/" + relative, names)
            for relative in excluded:
                self.assertNotIn("Deskweave-Portable/" + relative, names)
            for relative in ("start.sh", "Start Deskweave.command"):
                mode = archive.getinfo("Deskweave-Portable/" + relative).external_attr >> 16
                self.assertTrue(mode & stat.S_IXUSR)
            for relative, digest in manifest["files"].items():
                self.assertEqual(hashlib.sha256(archive.read("Deskweave-Portable/" + relative)).hexdigest(), digest)
        self.assertEqual(hashlib.sha256(archive_path.read_bytes()).hexdigest(), manifest["sha256"])

    def test_modified_vendor_is_refused_before_archive_is_created(self):
        (self.root / "web/vendor/novnc/core/rfb.js").write_text("changed viewer", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "Vendored viewer changed"):
            self.package.main()
        self.assertFalse((self.root / "dist/Deskweave-Portable-0.1.0-test-kit.zip").exists())


if __name__ == "__main__":
    unittest.main()
