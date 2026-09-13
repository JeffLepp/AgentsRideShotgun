#!/bin/sh
set -eu
KIT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec sh "$KIT_DIR/start.sh" --backend browser "$@"
