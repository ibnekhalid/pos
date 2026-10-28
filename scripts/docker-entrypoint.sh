#!/usr/bin/env bash
set -euo pipefail

cd /app

wait_for_postgres() {
  local host="${POSTGRES_HOST:-rp-postgres}"
  local port="${POSTGRES_PORT:-5432}"

  echo "Waiting for postgres at ${host}:${port}..."
  for _ in $(seq 1 30); do
    if (echo > /dev/tcp/"${host}"/"${port}") >/dev/null 2>&1; then
      echo "Postgres is reachable."
      return 0
    fi
    sleep 2
  done

  echo "ERROR: Postgres not reachable at ${host}:${port}"
  return 1
}

wait_for_postgres

exec dotnet Restaurant.Api.dll "$@"
