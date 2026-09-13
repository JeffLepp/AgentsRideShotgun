"""Build an allowlisted private source kit, never including profiles or tokens."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TOP_FILES = {"README.md", "PRODUCT.md", "map.md", "RESEARCH.md", "VALIDATION.md", "THIRD_PARTY_NOTICES.md",
             "requirements.txt", "Dockerfile", ".dockerignore", ".gitignore", "start.sh", "start.ps1", "Start Deskweave.command"}
TOP_DIRS = {"deskweave", "web", "tests", "scripts", "docker", "mac"}


def main():
    manifest = json.loads((ROOT / "scripts/novnc-lock.json").read_text(encoding="utf-8"))
    for name, expected in manifest["files"].items():
        actual = hashlib.sha256((ROOT / "web/vendor/novnc" / name).read_bytes()).hexdigest()
        if actual != expected:
            raise RuntimeError("Vendored viewer changed: " + name)
    dest = ROOT / "dist"
    dest.mkdir(exist_ok=True)
    target = dest / "Deskweave-Portable-0.1.0-test-kit.zip"
    files = {}
    candidates = [ROOT / name for name in TOP_FILES]
    for name in TOP_DIRS:
        folder = ROOT / name
        if folder.is_dir() and not folder.is_symlink():
            candidates.extend(folder.rglob("*"))
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(candidates):
            rel = path.relative_to(ROOT)
            if not path.is_file() or path.is_symlink() or "__pycache__" in rel.parts or path.suffix == ".pyc":
                continue
            if rel.parts[0] not in TOP_DIRS and rel.as_posix() not in TOP_FILES:
                continue
            content = path.read_bytes()
            info = zipfile.ZipInfo("Deskweave-Portable/" + rel.as_posix())
            info.create_system = 3
            executable = path.suffix in (".sh", ".command")
            info.external_attr = ((stat.S_IFREG | (0o755 if executable else 0o644)) << 16)
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, content)
            files[rel.as_posix()] = hashlib.sha256(content).hexdigest()
    record = {"createdUtc": datetime.now(timezone.utc).isoformat(), "archive": target.name,
              "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "bytes": target.stat().st_size, "files": files}
    target.with_suffix(".manifest.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in record.items() if k != "files"}, indent=2))


if __name__ == "__main__":
    main()
