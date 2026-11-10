#!/usr/bin/env bash
# Starts the whole service with Docker Compose, sends a burst of events and prints how fast they were delivered.
#
#   scripts/loadtest.sh [postgres|rabbitmq]      EVENTS=5000 CONCURRENCY=32 scripts/loadtest.sh rabbitmq
#
# It uses made-up secrets, its own project name and volume, and removes everything when it is done. Like
# scripts/smoke.sh it needs ports 3000 and 5005 free, and Docker, curl and Node.
#
# The numbers say how this stack behaves on the computer it runs on, with a receiver that answers at once. They are
# for comparing the two transports on one machine, not for promising a rate anywhere else.
set -euo pipefail

cd "$(dirname "$0")/.."

TRANSPORT="${1:-postgres}"
case "$TRANSPORT" in
  postgres|rabbitmq) ;;
  *) echo "usage: $0 [postgres|rabbitmq]" >&2; exit 2 ;;
esac

export COMPOSE_PROJECT_NAME=webhook-load
export ADMIN_API_KEY="$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")"
export SESSION_SECRET="$(node -e "console.log(require('crypto').randomBytes(32).toString('base64'))")"
export ALLOW_PRIVATE_NETWORKS=true
export REQUIRE_HTTPS=false
# The receiver would print every request, which at this rate is the slowest part of the whole run.
export DEMO_LOG_REQUESTS=false
export POSTGRES_PASSWORD="$(node -e "console.log(require('crypto').randomBytes(12).toString('hex'))")"
if [ "$TRANSPORT" = rabbitmq ]; then
  export DELIVERY_TRANSPORT=RabbitMq
  export RABBITMQ_PASSWORD="$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")"
fi

COMPOSE=(docker compose --profile demo)
[ "$TRANSPORT" = rabbitmq ] && COMPOSE+=(--profile rabbitmq)

cleanup() { "${COMPOSE[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "== Transport: $TRANSPORT"
"${COMPOSE[@]}" up -d --build >/dev/null

for _ in $(seq 1 60); do
  curl -fsS http://127.0.0.1:5005/health >/dev/null 2>&1 && break
  sleep 2
done
curl -fsS http://127.0.0.1:5005/health >/dev/null || { echo "the API did not become healthy" >&2; exit 1; }

COMPOSE="${COMPOSE[*]}" node scripts/loadtest.mjs
