#!/bin/sh
# Build dist/mac/Deskweave.app on a Mac; PyInstaller cannot build it from Windows or Linux.
# Unsigned: other Macs block the first launch until Privacy & Security > Open Anyway. Signing is a separate release step.
set -eu
KIT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$KIT_DIR"
PYTHON_BIN=${DESKWEAVE_PYTHON:-python3}
"$PYTHON_BIN" -c 'import sys; assert sys.platform == "darwin", "Build Deskweave.app on a Mac"; assert sys.version_info >= (3, 10), "Python 3.10 or newer is required"'
"$PYTHON_BIN" -m venv .build-venv
.build-venv/bin/python -m pip install --disable-pip-version-check -r requirements.txt pywebview==6.2.1 pyinstaller==6.22.2
.build-venv/bin/pyinstaller --noconfirm --clean --distpath dist/mac --workpath .build-venv/work mac/Deskweave.spec
printf '%s\n' "Built $KIT_DIR/dist/mac/Deskweave.app"
