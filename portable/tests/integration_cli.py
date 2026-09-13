"""Exercise the real launcher, stdio bridge and graceful broker shutdown."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from deskweave.mcp import http_json

ROOT = Path(__file__).resolve().parents[1]


def main(output):
    output.mkdir(parents=True, exist_ok=True)
    claims = []
    process = None
    failure = None
    with tempfile.TemporaryDirectory(prefix="deskweave cli ") as directory:
        data = Path(directory)
        connection_file = data / "connection.json"
        options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
        with (output / "broker.log").open("w", encoding="utf-8") as log:
            try:
                process = subprocess.Popen([sys.executable, "-m", "deskweave", "serve", "--backend", "browser", "--data-dir", str(data)],
                                           cwd=ROOT, stdout=log, stderr=log, **options)
                deadline = time.monotonic() + 15
                while not connection_file.exists():
                    if process.poll() is not None or time.monotonic() >= deadline:
                        raise AssertionError("The launcher did not publish its connection")
                    time.sleep(0.05)
                connection = json.loads(connection_file.read_text(encoding="utf-8"))
                state = http_json(connection["url"] + "/api/status", connection["ownerToken"])
                assert not state["running"] and not state["agentEnabled"] and not state["commandsSupported"]
                claims.append("Real CLI publishes authenticated stopped web-only broker without starting a browser")
                duplicate = subprocess.run([sys.executable, "-m", "deskweave", "serve", "--backend", "browser", "--data-dir", str(data)],
                                           cwd=ROOT, capture_output=True, text=True, timeout=10, **options)
                assert duplicate.returncode == 1 and "already has a running" in duplicate.stderr
                assert connection_file.exists()
                claims.append("Duplicate launcher refuses without altering the existing instance")

                messages = [{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25"}},
                            {"jsonrpc": "2.0", "method": "notifications/initialized"},
                            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
                            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "workspace_status"}}]
                bridge = subprocess.run([sys.executable, "-m", "deskweave", "mcp", "--connection", str(connection_file)],
                                        input="\n".join(json.dumps(m) for m in messages) + "\n", cwd=ROOT,
                                        capture_output=True, text=True, timeout=15, **options)
                assert bridge.returncode == 0
                replies = [json.loads(line) for line in bridge.stdout.splitlines()]
                assert len(replies) == 3 and replies[0]["result"]["protocolVersion"] == "2025-11-25"
                assert "workspace_run" not in {tool["name"] for tool in replies[1]["result"]["tools"]}
                assert replies[2]["result"]["isError"]
                assert connection["ownerToken"] not in bridge.stdout and connection["agentToken"] not in bridge.stdout
                claims.append("Real stdio MCP negotiates, filters unsupported commands and refuses access-off calls without leaking tokens")
                http_json(connection["url"] + "/api/agent", connection["ownerToken"], {"enabled": True})
                status_call = json.dumps(messages[-1]) + "\n"
                bridge = subprocess.run([sys.executable, "-m", "deskweave", "mcp", "--connection", str(connection_file)],
                                        input=status_call, cwd=ROOT, capture_output=True, text=True, timeout=15, **options)
                reply = json.loads(bridge.stdout)
                assert not reply["result"].get("isError")
                assert json.loads(reply["result"]["content"][0]["text"])["agentEnabled"]
                claims.append("Owner-enabled access reaches the real local broker through the stdio bridge")
                shutdown = subprocess.run([sys.executable, "-m", "deskweave", "shutdown", "--connection", str(connection_file)],
                                          cwd=ROOT, capture_output=True, text=True, timeout=15, **options)
                assert shutdown.returncode == 0 and process.wait(timeout=15) == 0
                assert not connection_file.exists()
                log.flush()
                content = (output / "broker.log").read_text(encoding="utf-8")
                assert connection["ownerToken"] not in content and connection["agentToken"] not in content
                claims.append("Authenticated shutdown exits cleanly, removes credentials and keeps tokens out of launcher logs")
            except Exception as exc:
                failure = repr(exc)
            finally:
                if process is not None and process.poll() is None:
                    try:
                        if connection_file.exists():
                            connection = json.loads(connection_file.read_text(encoding="utf-8"))
                            http_json(connection["url"] + "/api/shutdown", connection["ownerToken"], {})
                            process.wait(timeout=15)
                    finally:
                        if process.poll() is None:
                            process.terminate()
                            process.wait(timeout=5)
    report = {"passed": failure is None, "claims": claims, "failure": failure, "platform": sys.platform,
              "scope": "Actual CLI and stdio MCP; no workspace process or model started by this gate"}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return int(failure is not None)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(main(parser.parse_args().output.resolve()))
