#!/usr/bin/env bash
# Emergency: restore public traffic when a deploy left *_temp containers running without
# production names. Run on the VM from repo root: ./scripts/restore-production.sh
set -euo pipefail

restore_pair() {
  local api_src="$1"
  local web_src="$2"

  echo "=== Restoring production from ${api_src} + ${web_src} ==="
  docker rm -f restaurant_api restaurant_web 2>/dev/null || true
  docker rename "$api_src" restaurant_api
  docker rename "$web_src" restaurant_web
  echo "Production containers restored."
  docker ps --filter name='restaurant' --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
}

for color in green blue; do
  api_temp="restaurant_api_${color}_temp"
  web_temp="restaurant_web_${color}_temp"
  if docker container inspect "$api_temp" >/dev/null 2>&1 \
    && docker container inspect "$web_temp" >/dev/null 2>&1; then
    restore_pair "$api_temp" "$web_temp"
    exit 0
  fi
done

if docker container inspect restaurant_api >/dev/null 2>&1 \
  && docker container inspect restaurant_web >/dev/null 2>&1; then
  echo "Production containers restaurant_api + restaurant_web already exist."
  docker ps --filter name='restaurant' --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
  exit 0
fi

echo "No standby *_temp pair found. Starting production via docker compose..."
docker compose --project-directory . -f devops/docker-compose.yml up -d --no-deps rp-api rp-web
docker ps --filter name='restaurant' --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
