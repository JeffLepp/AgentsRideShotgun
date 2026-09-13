"""Validate and execute the extracted source kit using installed Linux dependencies."""
import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys
import zipfile

bundle = Path("/bundle/Deskweave-Portable-0.1.0-test-kit.zip")
manifest = json.loads(bundle.with_suffix(".manifest.json").read_text(encoding="utf-8"))
assert hashlib.sha256(bundle.read_bytes()).hexdigest() == manifest["sha256"]
target = Path("/data/package-check")
target.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(bundle) as archive:
    for member in archive.infolist():
        path = (target / member.filename).resolve()
        assert path.is_relative_to(target) and not stat.S_ISLNK(member.external_attr >> 16)
        relative = Path(member.filename).relative_to("Deskweave-Portable").as_posix()
        payload = archive.read(member)
        assert hashlib.sha256(payload).hexdigest() == manifest["files"][relative]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        path.chmod((member.external_attr >> 16) & 0o777)
kit = target / "Deskweave-Portable"
results = []
for argv in ([sys.executable, "-m", "deskweave", "doctor", "--backend", "desktop"],
             ["sh", "-n", "start.sh"], ["sh", "-n", "scripts/setup.sh"],
             ["sh", "-n", "Start Deskweave.command"],
             [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py", "-q"]):
    run = subprocess.run(argv, cwd=kit, text=True, capture_output=True, timeout=120)
    results.append({"argv": argv, "exitCode": run.returncode, "stdout": run.stdout, "stderr": run.stderr})
report = {"archiveSha256": manifest["sha256"], "fileCount": len(manifest["files"]),
          "allFileHashesMatched": True, "results": results, "passed": all(r["exitCode"] == 0 for r in results),
          "scope": "Extracted source kit on Linux using image-installed dependencies, not a clean-host dependency installation"}
Path("/evidence/report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
raise SystemExit(int(not report["passed"]))
