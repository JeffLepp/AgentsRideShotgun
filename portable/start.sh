#!/bin/sh
# Run from any working directory. Explicit setup is separate from starting a viewer.
set -eu
KIT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$KIT_DIR"
if [ ! -x .venv/bin/python ]; then
    printf '%s\n' 'First run: sh scripts/setup.sh' >&2
    exit 1
fi
exec .venv/bin/python -m deskweave serve --open "$@"
