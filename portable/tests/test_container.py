"""Container ownership and launch policy, without invoking Docker or a browser."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


KIT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("deskweave_container_script_under_test", KIT / "scripts/container.py")
container = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(container)


class ContainerOwnershipTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="deskweave-container-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.policy = self.root / "docker/seccomp/playwright-v1.58.2.json"
        self.policy.parent.mkdir(parents=True)
        self.policy.write_bytes((KIT / "docker/seccomp/playwright-v1.58.2.json").read_bytes())
        for patcher in (mock.patch.object(container, "ROOT", self.root),
                        mock.patch.object(container, "SECCOMP", self.policy),
                        mock.patch.object(container.shutil, "which", return_value="mock-docker")):
            patcher.start()
            self.addCleanup(patcher.stop)

    @staticmethod
    def result(argv, value=None, code=0):
        return subprocess.CompletedProcess(argv, code, "" if value is None else json.dumps(value), "")

    def test_unrelated_same_name_container_is_refused_before_any_mutation(self):
        unrelated = [{"Config": {"Labels": {"owner": "another-application"}}}]
        for action in ("start", "stop", "open", "status"):
            with self.subTest(action=action), mock.patch.object(container.sys, "argv", ["container.py", action]), \
                    mock.patch.object(container.subprocess, "run", return_value=self.result([], unrelated)) as run:
                with self.assertRaisesRegex(RuntimeError, "not owned by this pilot"):
                    container.main()
                self.assertEqual(1, run.call_count)
                self.assertEqual(["mock-docker", "inspect", container.NAME], run.call_args.args[0])
        self.assertFalse((self.root / ".state").exists())

    def test_existing_unlabeled_volume_is_refused_before_build_or_launch(self):
        for labels in (None, {}, {"ai.deskweave.pilot": "another-owner"}):
            calls = []
            def docker(argv, **kwargs):
                calls.append(argv[1:])
                if argv[1:] == ["inspect", container.NAME]:
                    return self.result(argv, code=1)
                if argv[1:] == ["volume", "inspect", container.VOLUME]:
                    return self.result(argv, [{"Labels": labels}])
                self.fail("The refusal must happen before any further Docker call")
            with self.subTest(labels=labels), mock.patch.object(container.sys, "argv", ["container.py", "start"]), \
                    mock.patch.object(container.subprocess, "run", side_effect=docker):
                with self.assertRaisesRegex(RuntimeError, "without Deskweave ownership"):
                    container.main()
            self.assertEqual([["inspect", container.NAME], ["volume", "inspect", container.VOLUME]], calls)
        self.assertFalse((self.root / ".state").exists())

    def test_fresh_volume_is_labeled_before_bounded_private_launch(self):
        calls = []
        started = False
        connection = {"url": "http://0.0.0.0:8765", "ownerToken": "fixture-owner-token", "agentToken": "fixture-agent-token"}
        running = {"Config": {"Labels": {"ai.deskweave.pilot": "0.1.0"}},
                   "State": {"Running": True},
                   "NetworkSettings": {"Ports": {"8765/tcp": [{"HostIp": "127.0.0.1", "HostPort": "49123"}]}}}

        def docker(argv, **kwargs):
            nonlocal started
            self.assertEqual(self.root, kwargs["cwd"])
            command = argv[1:]
            calls.append(command)
            if command == ["inspect", container.NAME]:
                return self.result(argv, [running] if started else None, code=0 if started else 1)
            if command == ["volume", "inspect", container.VOLUME]:
                return self.result(argv, code=1)
            if command[:2] == ["volume", "create"] or command[0] == "build":
                return self.result(argv)
            if command[0] == "run":
                started = True
                return self.result(argv)
            if command == ["exec", container.NAME, "cat", "/data/connection.json"]:
                return self.result(argv, connection)
            self.fail("Unexpected Docker call: " + repr(command))

        opener = mock.Mock()
        opener.open.return_value.__enter__ = mock.Mock(return_value=io.StringIO('{"running":false}'))
        opener.open.return_value.__exit__ = mock.Mock(return_value=False)
        with mock.patch.object(container.sys, "argv", ["container.py", "start", "--no-open"]), \
                mock.patch.object(container.subprocess, "run", side_effect=docker), \
                mock.patch.object(container, "build_opener", return_value=opener), \
                mock.patch.object(container.webbrowser, "open") as open_browser, \
                contextlib.redirect_stdout(io.StringIO()):
            container.main()

        volume_create = ["volume", "create", "--label", container.LABEL, container.VOLUME]
        build = ["build", "-t", container.IMAGE, "."]
        launch = next(command for command in calls if command[0] == "run")
        self.assertLess(calls.index(volume_create), calls.index(build))
        self.assertLess(calls.index(build), calls.index(launch))
        self.assertEqual("127.0.0.1::8765", launch[launch.index("-p") + 1])
        self.assertEqual(f"type=volume,source={container.VOLUME},target=/data", launch[launch.index("--mount") + 1])
        self.assertEqual("seccomp=" + str(self.policy), launch[launch.index("--security-opt") + 1])
        self.assertEqual(container.LABEL, launch[launch.index("--label") + 1])
        self.assertIn("--init", launch)
        for flag, value in (("--memory", "1024m"), ("--memory-swap", "1024m"),
                            ("--pids-limit", "256"), ("--shm-size", "128m")):
            self.assertEqual(value, launch[launch.index(flag) + 1])
        for forbidden in ("--privileged", "--cap-add", "SYS_ADMIN", "--no-sandbox", "seccomp=unconfined", "--ipc=host"):
            self.assertNotIn(forbidden, launch)
        request = opener.open.call_args.args[0]
        self.assertEqual("http://127.0.0.1:49123/api/status", request.full_url)
        saved = json.loads((self.root / ".state/container-connection.json").read_text())
        self.assertEqual("http://127.0.0.1:49123", saved["url"])
        # The host bridge runs as the agent, so it never receives the owner token.
        self.assertEqual({"url": "http://127.0.0.1:49123", "agentToken": connection["agentToken"]}, saved)
        open_browser.assert_not_called()


if __name__ == "__main__":
    unittest.main()
