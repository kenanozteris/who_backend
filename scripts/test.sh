#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
# Native Docker Desktop user sockets may be absent from /var/run.
if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.docker/run/docker.sock" ]]; then
  export DOCKER_HOST="unix://$HOME/.docker/run/docker.sock"
  export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-/var/run/docker.sock}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec dotnet test "$@"
