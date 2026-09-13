"""Optional Linux desktop through an already-running Docker or Podman runtime."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time
import webbrowser
from urllib.request import Request, build_opener, ProxyHandler

ROOT = Path(__file__).resolve().parents[1]
IMAGE = "deskweave-portable:0.1.0"
NAME = "deskweave-portable-pilot"
VOLUME = "deskweave-portable-data"
LABEL = "ai.deskweave.pilot=0.1.0"
SECCOMP = ROOT / "docker/seccomp/playwright-v1.58.2.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("start", "stop", "open", "status"))
    parser.add_argument("--runtime", choices=("docker", "podman"), default="docker")
    parser.add_argument("--memory", type=int, default=1024, help="Container MiB cap, excluding VM/viewer")
    parser.add_argument("--no-open", action="store_true", help="Prepare the private connection without opening a viewer")
    args = parser.parse_args()
    runtime = shutil.which(args.runtime)
    if not runtime:
        raise RuntimeError(f"Install and start {args.runtime} first, or use native browser mode without a VM.")

    def run(*argv, check=True):
        return subprocess.run([runtime, *argv], cwd=ROOT, text=True, capture_output=True, check=check)

    def inspect():
        result = run("inspect", NAME, check=False)
        if result.returncode:
            return None
        value = json.loads(result.stdout)[0]
        if value.get("Config", {}).get("Labels", {}).get("ai.deskweave.pilot") != "0.1.0":
            raise RuntimeError("A container with this name already exists and is not owned by this pilot.")
        return value

    state = inspect()
    if args.action == "stop":
        if state:
            run("stop", "--time", "70", NAME)
        print("Deskweave container stopped; its private volume remains.")
        return
    if args.action == "status":
        print(json.dumps({"running": bool(state and state["State"]["Running"]),
                          "memoryCapMiB": None if not state else state["HostConfig"]["Memory"] // 1048576}, indent=2))
        return
    if args.action == "start" and not state:
        if not 512 <= args.memory <= 8192:
            raise ValueError("Memory must be 512–8192 MiB")
        if hashlib.sha256(SECCOMP.read_bytes()).hexdigest() != "cc3e61cabda6bbc1e53e54d27ba4d55a9d3be829b6dd1a596f4a7b31b1cc7849":
            raise RuntimeError("The pinned namespace policy changed; inspect it before running a container")
        existing_volume = run("volume", "inspect", VOLUME, check=False)
        if existing_volume.returncode == 0:
            volume = json.loads(existing_volume.stdout)[0]
            if (volume.get("Labels") or {}).get("ai.deskweave.pilot") != "0.1.0":
                raise RuntimeError("The data volume name already exists without Deskweave ownership. Choose or preserve that volume explicitly before continuing.")
        else:
            run("volume", "create", "--label", LABEL, VOLUME)
        subprocess.run([runtime, "build", "-t", IMAGE, "."], cwd=ROOT, check=True)
        run("run", "-d", "--name", NAME, "--label", LABEL, "--init",
            "--memory", f"{args.memory}m", "--memory-swap", f"{args.memory}m", "--pids-limit", "256",
            "--security-opt", "seccomp=" + str(SECCOMP),
            "--shm-size", "128m", "-p", "127.0.0.1::8765",
            "--mount", f"type=volume,source={VOLUME},target=/data",
            IMAGE, "serve", "--backend", "desktop", "--port", "8765", "--container-bind",
            "--data-dir", "/data", "--host-platform", sys.platform)
    elif args.action == "start" and not state["State"]["Running"]:
        run("start", NAME)
    state = inspect()
    if not state or not state["State"]["Running"]:
        raise RuntimeError("The Deskweave container is not running")
    ports = state["NetworkSettings"]["Ports"]["8765/tcp"]
    if len(ports) != 1 or ports[0]["HostIp"] != "127.0.0.1":
        raise RuntimeError("Refusing a container that is not published exclusively to loopback")
    local_url = "http://127.0.0.1:" + ports[0]["HostPort"]
    opener = build_opener(ProxyHandler({}))
    deadline = time.monotonic() + 20
    while True:
        result = run("exec", NAME, "cat", "/data/connection.json", check=False)
        if result.returncode == 0:
            try:
                connection = json.loads(result.stdout)
                # A stale file from an unclean stop must not authenticate a new viewer.
                request = Request(local_url + "/api/status", headers={"Authorization": "Bearer " + connection["ownerToken"]})
                with opener.open(request, timeout=2) as response:
                    json.load(response)
                break
            except (OSError, ValueError, KeyError):
                pass
        if time.monotonic() >= deadline:
            raise RuntimeError("Broker did not become ready; inspect this container's logs")
        time.sleep(0.25)
    connection["url"] = local_url
    # This file is for a host-side MCP bridge; the volume remains container-private.
    state_dir = ROOT / ".state"
    state_dir.mkdir(exist_ok=True, mode=0o700)
    if os.name != "nt":
        state_dir.chmod(0o700)
    target = state_dir / "container-connection.json"
    fd = os.open(target, os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as out:
        json.dump(connection, out, indent=2)
    if os.name != "nt":
        target.chmod(0o600)
    if not args.no_open:
        webbrowser.open(f"{connection['url']}/#token={connection['ownerToken']}")
    print(f"Local Linux desktop broker ready. MCP connection: {target}")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, OSError, subprocess.CalledProcessError) as exc:
        print(f"Deskweave: {exc}", file=sys.stderr)
        raise SystemExit(1)
