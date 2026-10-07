#!/usr/bin/env bash
set -euo pipefail
WHO_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$WHO_REPO_ROOT"
if command -v docker >/dev/null 2>&1; then
  WHO_DOCKER_BIN="$(command -v docker)"
elif [[ -x /Applications/Docker.app/Contents/Resources/bin/docker ]]; then
  WHO_DOCKER_BIN=/Applications/Docker.app/Contents/Resources/bin/docker
else
  echo "Docker CLI is required. Install/start Docker Desktop and expose docker on PATH." >&2
  exit 1
fi
who_load_env() {
  if [[ ! -f .env ]]; then
    echo "Missing .env. Copy .env.example and supply WHO_POSTGRES_PASSWORD." >&2
    exit 1
  fi
  set -a
  source .env
  set +a
}
