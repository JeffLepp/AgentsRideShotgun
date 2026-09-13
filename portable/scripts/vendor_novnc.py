"""Fetch pinned upstream static modules only; preserve every upstream license."""
import hashlib
import io
import json
from pathlib import Path
import tarfile
from urllib.request import urlopen

VERSION = "1.7.0"
URL = f"https://codeload.github.com/novnc/noVNC/tar.gz/refs/tags/v{VERSION}"
ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / "web/vendor/novnc"
MANIFEST = ROOT / "scripts/novnc-lock.json"


def main():
    with urlopen(URL, timeout=60) as response:
        payload = response.read(10 * 1024 * 1024 + 1)
    if len(payload) > 10 * 1024 * 1024:
        raise RuntimeError("Unexpected archive size")
    digest = hashlib.sha256(payload).hexdigest()
    if MANIFEST.exists() and json.loads(MANIFEST.read_text())["archiveSha256"] != digest:
        raise RuntimeError("Upstream archive changed; inspect it before updating the pin")
    files = {}
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        for member in archive.getmembers():
            relative = Path(*Path(member.name).parts[1:])
            if not relative.parts or not member.isfile():
                continue
            if relative.parts[0] not in ("core", "vendor", "LICENSE.txt", "AUTHORS", "docs"):
                continue
            if relative.parts[0] == "docs" and "license" not in relative.name.lower():
                continue
            target = (DEST / relative).resolve()
            if not target.is_relative_to(DEST.resolve()):
                raise RuntimeError("Unsafe archive path")
            value = archive.extractfile(member).read()
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(value)
            files[relative.as_posix()] = hashlib.sha256(value).hexdigest()
    MANIFEST.write_text(json.dumps({"version": VERSION, "url": URL, "archiveSha256": digest, "files": files}, indent=2) + "\n", encoding="utf-8")
    print(f"Vendored noVNC {VERSION}: {len(files)} files, archive SHA256 {digest}")


if __name__ == "__main__":
    main()
