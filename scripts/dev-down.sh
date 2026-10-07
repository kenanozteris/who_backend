#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
# No --volumes/-v: the PostgreSQL named volume survives.
"$WHO_DOCKER_BIN" compose down
