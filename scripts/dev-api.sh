#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
who_load_env
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec dotnet run --project src/Who.Api --no-launch-profile --urls "${WHO_API_URL:-http://127.0.0.1:5080}"
