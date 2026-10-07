#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
who_load_env
"$WHO_DOCKER_BIN" compose config --quiet
"$WHO_DOCKER_BIN" compose up -d --wait --wait-timeout 120
"$WHO_DOCKER_BIN" compose ps
