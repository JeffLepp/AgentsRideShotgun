from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

import psutil

from deskweave import native


class NativeContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.workspace = native.NativeWorkspace(Path(self.temporary.name) / "workspace")

    def test_dimensions_and_rate_reject_invalid_values(self):
        for kwargs in ({"width": True}, {"width": 0}, {"height": 99999}, {"fps": 0}, {"fps": 31}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                native.NativeWorkspace(Path(self.temporary.name), **kwargs)

    def test_environment_contains_only_owned_display_home_and_runtime(self):
        hostile = {"DISPLAY": ":0", "WAYLAND_DISPLAY": "wayland-0", "XAUTHORITY": "/owner/.Xauthority",
                   "DBUS_SESSION_BUS_ADDRESS": "unix:path=/owner/bus", "HOME": "/owner",
                   "XDG_RUNTIME_DIR": "/run/user/owner", "OPENAI_API_KEY": "fixture-secret",
                   "LD_PRELOAD": "/owner/hook.so", "PYTHONPATH": "/owner/modules",
                   "PATH": "/owner/bin", "HTTP_PROXY": "http://owner-proxy"}
        self.workspace.display = ":137"
        with mock.patch.dict(os.environ, hostile):
            environment = self.workspace._environment()
        self.assertEqual(":137", environment["DISPLAY"])
        self.assertEqual(str(self.workspace.root / ".runtime" / "Xauthority"), environment["XAUTHORITY"])
        self.assertEqual(str(self.workspace.root / "home"), environment["HOME"])
        for key in ("WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "OPENAI_API_KEY", "LD_PRELOAD", "PYTHONPATH", "HTTP_PROXY"):
            self.assertNotIn(key, environment)
        self.assertNotIn("/owner", " ".join(environment.values()))

    def test_two_workspaces_have_distinct_display_and_home_paths(self):
        other = native.NativeWorkspace(Path(self.temporary.name) / "second")
        self.workspace.display, other.display = ":201", ":202"
        one, two = self.workspace._environment(), other._environment()
        for key in ("DISPLAY", "HOME", "XAUTHORITY", "XDG_RUNTIME_DIR", "TMPDIR"):
            self.assertNotEqual(one[key], two[key])

    def test_unsupported_text_is_refused_before_any_input(self):
        with mock.patch.object(self.workspace, "_ensure_running") as ensure, mock.patch.object(self.workspace, "_checked") as execute:
            for text in ("hello 世界", "café", "line\nnext", "a\tb", "\0", "", "x" * 4097):
                with self.subTest(text=text[:20]), self.assertRaises(ValueError):
                    self.workspace.type_text(text)
            ensure.assert_not_called()
            execute.assert_not_called()

    def test_ascii_text_uses_stdin_not_command_line(self):
        self.workspace._tools = {"input": "/usr/bin/xdotool"}
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_checked") as execute:
            text = 'literal --window 0; $(no-shell) "text"'
            self.workspace.type_text(text)
        argv = execute.call_args.args[0]
        self.assertNotIn(text, argv)
        self.assertEqual(["--file", "-"], argv[-2:])
        self.assertEqual(text.encode("ascii"), execute.call_args.kwargs["data"])

    def test_keys_are_one_allowlisted_chord(self):
        for source, expected in (("Ctrl+L", "ctrl+l"), ("Control+Shift+Tab", "ctrl+shift+Tab"),
                                 ("Enter", "Return"), ("PageDown", "Next"), ("F12", "F12"),
                                 ("Meta+ArrowLeft", "super+Left"), ("ArrowRight", "Right"),
                                 ("ArrowUp", "Up"), ("ArrowDown", "Down")):
            self.assertEqual(expected, native._key_chord(source))
        for key in ("a key ctrl+c", "--window", "ctrl+ctrl+a", "Ctrl+", "Shift+Alt+exec", "F13", "é", "a;b"):
            with self.subTest(key=key), self.assertRaises(ValueError):
                native._key_chord(key)

    def test_pointer_bounds_reject_entire_operation(self):
        with mock.patch.object(self.workspace, "_checked") as execute:
            for x, y, button in ((-1, 0, 1), (1024, 0, 1), (0, 640, 1), (0, 0, 4), (True, 0, 1)):
                with self.subTest(values=(x, y, button)), self.assertRaises(ValueError):
                    self.workspace.click(x, y, button)
            execute.assert_not_called()

    def test_scroll_sign_and_amount_are_bounded(self):
        self.workspace._tools = {"input": "/usr/bin/xdotool"}
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_checked") as execute:
            self.workspace.scroll(-2, 3)
            self.assertEqual("5", execute.call_args_list[0].args[0][-1])
            self.assertEqual("6", execute.call_args_list[1].args[0][-1])
            with self.assertRaises(ValueError):
                self.workspace.scroll(0, 21)
            self.assertEqual(2, execute.call_count)

    def test_run_requires_bounded_explicit_argv(self):
        argv = ["printf", "literal $(shell) ; text"]
        validated, timeout = native._command(argv, 2)
        self.assertEqual(argv, validated)
        self.assertIsNot(argv, validated)
        self.assertEqual(2, timeout)
        for value in ("echo hello", [], [""], ["echo", "\0"], ["x"] * 65, ["x" * 32769]):
            with self.subTest(argv=value), self.assertRaises(ValueError):
                native._command(value, 1)
        for timeout in (0, 61, float("nan"), float("inf"), True, "30"):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                native._command(["true"], timeout)

    def test_input_diagnostic_is_an_error_even_with_exit_zero(self):
        receipt = {"exitCode": 0, "timedOut": False, "truncated": False,
                   "stdout": b"", "stderr": b"I don't know which key produces that, skipping."}
        with mock.patch.object(self.workspace, "_execute", return_value=receipt) as execute:
            with self.assertRaisesRegex(native.NativeError, "Input may be partial"):
                self.workspace._checked(["xdotool", "type"], input_action=True)
        execute.assert_called_once()

    def test_viewer_input_requires_confirmed_server_parameters(self):
        self.workspace._tools = {"config": "/usr/bin/tigervncconfig"}
        self.workspace.display = ":178"
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_checked", side_effect=[b"", b"1\n", b"", b"1\n"]) as execute:
            self.workspace.set_viewer_input(True)
        self.assertTrue(self.workspace._viewer_input)
        self.assertEqual(4, execute.call_count)
        self.assertTrue(all(call.args[0][1:3] == ["-display", ":178"] for call in execute.call_args_list))

    def test_debian_viewer_boolean_readback_uses_on_and_off(self):
        self.workspace._tools = {"config": "/usr/bin/tigervncconfig"}
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_checked", side_effect=[b"", b"on\n", b"", b"on\n", b"", b"off\n", b"", b"off\n"]):
            self.workspace.set_viewer_input(True)
            self.assertTrue(self.workspace._viewer_input)
            self.workspace.set_viewer_input(False)
            self.assertFalse(self.workspace._viewer_input)

    def test_rapid_restart_invalidates_old_watchdog_and_stop_ends_current_watchdog(self):
        real_thread = threading.Thread
        process = mock.Mock()
        process.poll.return_value = None
        tools = {name: "/usr/bin/" + name for name in native._TOOLS}
        def checked(argv, **kwargs):
            return b"12345678" if argv[0] == tools["password"] else b"1024 640"
        with mock.patch.object(native.sys, "platform", "linux"), mock.patch.object(native.os, "O_NOFOLLOW", 0, create=True), mock.patch.object(native, "dependency_status", return_value=tools), mock.patch.object(self.workspace, "_checked", side_effect=checked), mock.patch.object(self.workspace, "_spawn", return_value=process), mock.patch.object(self.workspace, "set_viewer_input"), mock.patch.object(native.threading, "Thread") as create_thread:
            self.workspace.start()
            first = create_thread.call_args.kwargs["args"][0]
            self.workspace.stop()
            self.workspace.start()
            second = create_thread.call_args.kwargs["args"][0]
        self.assertIsNot(first, second)
        self.assertTrue(first.is_set())
        self.assertFalse(second.is_set())
        old = real_thread(target=self.workspace._watch, args=(first,), daemon=True)
        current = real_thread(target=self.workspace._watch, args=(second,), daemon=True)
        old.start()
        current.start()
        old.join(timeout=1)
        self.assertFalse(old.is_alive())
        self.assertTrue(current.is_alive())
        self.workspace.stop()
        current.join(timeout=1)
        self.assertFalse(current.is_alive())

    def test_denied_resource_measurement_is_explicitly_incomplete(self):
        parent, process = mock.Mock(), mock.Mock()
        process.pid = 123
        process.memory_info.side_effect = psutil.AccessDenied(123)
        self.workspace._processes = [parent]
        with mock.patch.object(self.workspace, "_observe", return_value=[process]):
            status = self.workspace.status()
        self.assertEqual(1, status["processCount"])
        self.assertEqual(0, status["rssBytes"])
        self.assertFalse(status["metricsComplete"])

    def test_launch_without_owned_window_fails_and_cleans_its_tree(self):
        process = mock.Mock()
        process.poll.return_value = 1
        self.workspace._tools = {"terminal": "/usr/bin/xterm"}
        self.workspace._processes = [process]
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_spawn", return_value=process), mock.patch.object(self.workspace, "_application_window", return_value=None), mock.patch.object(native, "_terminate") as terminate:
            with self.assertRaisesRegex(native.NativeError, "owned visible window"):
                self.workspace.launch("terminal")
        terminate.assert_called_once_with(process)
        self.assertEqual([], self.workspace._processes)

    def test_launch_readiness_rejects_window_from_another_owned_tree(self):
        supervisor = mock.Mock()
        supervisor.poll.return_value = None
        child = mock.Mock(pid=123)
        self.workspace._tools = {"input": "/usr/bin/xdotool"}
        receipt = {"exitCode": 0, "timedOut": False, "truncated": False, "stdout": b"60001\n"}
        with mock.patch.object(self.workspace, "_observe", return_value=[child]), mock.patch.object(self.workspace, "_execute", return_value=receipt), mock.patch.object(self.workspace, "_checked", return_value=b"999\n"):
            self.assertIsNone(self.workspace._application_window(supervisor, "browser"))

    def test_failed_viewer_input_readback_stops_workspace(self):
        self.workspace._tools = {"config": "/usr/bin/tigervncconfig"}
        with mock.patch.object(self.workspace, "_ensure_running"), mock.patch.object(self.workspace, "_checked", side_effect=[b"", b"0\n"]), mock.patch.object(self.workspace, "_stop_locked") as stop:
            with self.assertRaises(native.NativeError):
                self.workspace.set_viewer_input(True)
        stop.assert_called_once()
        self.assertIn("unconfirmed control grant", self.workspace._error)

    def test_stop_retains_files_and_clears_connection_credentials(self):
        self.workspace.files.mkdir(parents=True)
        result = self.workspace.files / "result.txt"
        result.write_text("retained", encoding="utf-8")
        self.workspace.vnc_password = "private"
        self.workspace.rfb_port = 5920
        self.workspace.display = ":20"
        self.workspace.stop()
        self.workspace.stop()
        self.assertEqual("retained", result.read_text(encoding="utf-8"))
        self.assertIsNone(self.workspace.vnc_password)
        self.assertIsNone(self.workspace.rfb_port)
        self.assertFalse(self.workspace.status()["running"])

    def test_access_denied_is_not_treated_as_a_dead_process(self):
        process = mock.Mock()
        process.is_running.return_value = True
        process.status.side_effect = psutil.AccessDenied(123)
        self.assertTrue(native._live(process))

    def test_failed_cleanup_is_reported_and_retained_for_retry(self):
        process = mock.Mock()
        self.workspace._processes = [process]
        with mock.patch.object(native, "_terminate", side_effect=native.NativeError("unconfirmed child")):
            with self.assertRaises(native.NativeError):
                self.workspace.stop()
        self.assertEqual([process], self.workspace._processes)
        with mock.patch.object(native, "_terminate"):
            self.workspace.stop()
        self.assertEqual([], self.workspace._processes)

    def test_failed_start_stops_every_owned_launch(self):
        server, wm = mock.Mock(), mock.Mock()
        server.poll.return_value = None
        wm.poll.return_value = 1
        tools = {name: "/usr/bin/" + name for name in native._TOOLS}

        def spawn(argv, **kwargs):
            process = server if argv[0] == tools["server"] else wm
            self.workspace._processes.append(process)
            return process

        with mock.patch.object(native.sys, "platform", "linux"), mock.patch.object(native.os, "O_NOFOLLOW", 0, create=True), mock.patch.object(native, "dependency_status", return_value=tools), mock.patch.object(self.workspace, "_checked", side_effect=[b"12345678", b"", b"1024 640"]), mock.patch.object(self.workspace, "_spawn", side_effect=spawn), mock.patch.object(native, "_terminate") as terminate:
            with self.assertRaisesRegex(native.NativeError, "window manager failed"):
                self.workspace.start()
        self.assertEqual([mock.call(wm), mock.call(server)], terminate.call_args_list)
        self.assertFalse(self.workspace.status()["running"])
        self.assertIsNone(self.workspace.vnc_password)


@unittest.skipUnless(sys.platform == "linux", "Real child supervision requires Linux")
class LinuxProcessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.workspace = native.NativeWorkspace(Path(self.temporary.name) / "workspace")
        self.workspace.files.mkdir(parents=True)
        self.workspace._private.mkdir()
        self.workspace.display = ":9999"
        self.workspace._env = self.workspace._environment()
        self.addCleanup(self.workspace.stop)

    def test_stop_kills_detached_grandchild_after_intermediate_parent_exits(self):
        pidfile = self.workspace.files / "grandchild.pid"
        program = ("import pathlib, subprocess, sys; "
                   "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], start_new_session=True); "
                   "pathlib.Path(sys.argv[1]).write_text(str(p.pid))")
        supervisor = self.workspace._spawn([sys.executable, "-c", program, str(pidfile)], persistent=True)
        deadline = time.monotonic() + 5
        while not pidfile.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertTrue(pidfile.exists())
        grandchild = psutil.Process(int(pidfile.read_text()))
        time.sleep(0.2)
        self.assertIsNone(supervisor.poll())
        self.assertTrue(native._live(grandchild))
        self.workspace.stop()
        self.assertFalse(native._live(grandchild))
        self.assertIsNotNone(supervisor.poll())
        self.assertTrue(pidfile.exists())

    def test_large_command_output_is_drained_but_bounded(self):
        receipt = self.workspace._execute([sys.executable, "-c", "import sys; sys.stdout.write('x' * 2000000); sys.stderr.write('y' * 1000000)"], timeout=5)
        self.assertEqual(0, receipt["exitCode"])
        self.assertTrue(receipt["truncated"])
        self.assertEqual(native._OUTPUT_LIMIT, len(receipt["stdout"]))
        self.assertEqual(native._OUTPUT_LIMIT, len(receipt["stderr"]))

    def test_observed_descendant_is_stopped_after_supervisor_is_killed(self):
        pidfile = self.workspace.files / "child.pid"
        program = "import os,pathlib,sys,time; pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); time.sleep(60)"
        supervisor = self.workspace._spawn([sys.executable, "-c", program, str(pidfile)], persistent=True)
        deadline = time.monotonic() + 5
        while not pidfile.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertTrue(pidfile.exists())
        child = psutil.Process(int(pidfile.read_text()))
        self.assertGreaterEqual(self.workspace.status()["processCount"], 2)
        supervisor.kill()
        supervisor.wait(timeout=2)
        self.workspace.stop()
        self.assertFalse(native._live(child))

    def test_timeout_terminates_command_tree(self):
        receipt = self.workspace._execute([sys.executable, "-c", "import time; time.sleep(60)"], timeout=0.2)
        self.assertTrue(receipt["timedOut"])
        self.assertNotEqual(0, receipt["exitCode"])
        self.assertEqual([], self.workspace._processes)


if __name__ == "__main__":
    unittest.main()
