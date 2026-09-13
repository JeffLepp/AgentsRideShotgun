#!/bin/sh
set -eu
KIT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$KIT_DIR"
PYTHON_BIN=${DESKWEAVE_PYTHON:-python3}
"$PYTHON_BIN" -c 'import sys; assert sys.version_info >= (3, 10), "Python 3.10 or newer is required"'
"$PYTHON_BIN" -m venv .venv
.venv/bin/python -m pip install --disable-pip-version-check -r requirements.txt
.venv/bin/python -m deskweave doctor
printf '%s\n' 'Setup complete. Run: sh start.sh'
