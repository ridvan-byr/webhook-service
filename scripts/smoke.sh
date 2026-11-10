#!/usr/bin/env bash
# Builds and starts the whole service with Docker Compose and checks that it works end to end:
# the API answers, a tenant can be created, an event reaches a receiver, and the panel is up and protected.
#
#   scripts/smoke.sh              the worker polls the database (the default)
#   scripts/smoke.sh rabbitmq     deliveries go through RabbitMQ, which is started too
#
# It uses made-up secrets, its own project name and its own volume, and removes everything when it is done.
# Ports 3000 and 5005 must be free. Needs Docker, curl and Node.
set -euo pipefail

cd "$(dirname "$0")/.."

TRANSPORT="${1:-postgres}"
case "$TRANSPORT" in
  postgres|rabbitmq) ;;
  *) echo "usage: $0 [postgres|rabbitmq]" >&2; exit 2 ;;
esac

export COMPOSE_PROJECT_NAME=webhook-smoke
export ADMIN_API_KEY="$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")"
export SESSION_SECRET="$(node -e "console.log(require('crypto').randomBytes(32).toString('base64'))")"
# The demo receiver is on a private network and speaks plain http.
export ALLOW_PRIVATE_NETWORKS=true
export REQUIRE_HTTPS=false

if [ "$TRANSPORT" = rabbitmq ]; then
  export DELIVERY_TRANSPORT=RabbitMq
  export RABBITMQ_PASSWORD="$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")"
fi

API=http://127.0.0.1:5005
WEB=http://127.0.0.1:3000
COMPOSE=(docker compose --profile demo)
[ "$TRANSPORT" = rabbitmq ] && COMPOSE+=(--profile rabbitmq)

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAILED: %s\n' "$*" >&2; "${COMPOSE[@]}" logs --tail=40 >&2 || true; exit 1; }
json() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const v=JSON.parse(s);console.log($1)})"; }

cleanup() { "${COMPOSE[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

step "Build and start"
"${COMPOSE[@]}" up -d --build

step "Wait for the API"
for _ in $(seq 1 60); do
  curl -fsS "$API/health" >/dev/null 2>&1 && break
  sleep 2
done
curl -fsS "$API/health" >/dev/null || fail "the API did not become healthy"

step "The API refuses a caller without a key"
code=$(curl -s -o /dev/null -w '%{http_code}' "$API/deliveries")
[ "$code" = 401 ] || fail "GET /deliveries without a key gave $code, expected 401"

step "Create a tenant"
tenant=$(curl -fsS -X POST "$API/admin/tenants" -H "X-Admin-Key: $ADMIN_API_KEY" -H 'Content-Type: application/json' -d '{"name":"Smoke"}')
KEY=$(echo "$tenant" | json 'v.apiKey')
[ -n "$KEY" ] || fail "no API key in the tenant response"
AUTH=(-H "Authorization: Bearer $KEY")

step "Register the demo receiver and send an event"
curl -fsS -X POST "$API/endpoints" "${AUTH[@]}" -H 'Content-Type: application/json' -d '{"url":"http://receiver:9000/ok"}' >/dev/null
curl -fsS -X POST "$API/events" "${AUTH[@]}" -H 'Content-Type: application/json' -d '{"type":"smoke.test","payload":{"hello":"world"}}' >/dev/null

step "Wait for the delivery to succeed"
status=""
for _ in $(seq 1 30); do
  status=$(curl -fsS "$API/deliveries" "${AUTH[@]}" | json 'v[0]?.status ?? "none"')
  [ "$status" = Succeeded ] && break
  sleep 2
done
[ "$status" = Succeeded ] || fail "the delivery is $status, expected Succeeded"

step "The receiver got a signed request"
"${COMPOSE[@]}" logs receiver | grep -q 'webhookSignature":"v1,' || fail "the receiver did not log a v1 signature"

if [ "$TRANSPORT" = rabbitmq ]; then
  step "The delivery really went through RabbitMQ"
  "${COMPOSE[@]}" logs worker | grep -q 'Delivery relay started' || fail "the worker did not start the relay"
  # The broker counts what was published to the exchange. It updates its statistics every few seconds.
  published=0
  for _ in $(seq 1 15); do
    published=$(curl -fsS -u "webhooks:$RABBITMQ_PASSWORD" 'http://127.0.0.1:15672/api/exchanges/%2F/webhooks.deliveries'       | json 'v.message_stats?.publish_in ?? 0' || echo 0)
    [ "$published" -ge 1 ] && break
    sleep 2
  done
  [ "$published" -ge 1 ] || fail "the broker saw no message published to the exchange"
  queue=$(curl -fsS -u "webhooks:$RABBITMQ_PASSWORD" 'http://127.0.0.1:15672/api/queues/%2F/webhooks.deliveries.due'     | json 'v.messages')
  [ "$queue" = 0 ] || fail "the queue still holds $queue message(s) after the delivery succeeded"
  poison=$(curl -fsS -u "webhooks:$RABBITMQ_PASSWORD" 'http://127.0.0.1:15672/api/queues/%2F/webhooks.deliveries.poison'     | json 'v.messages')
  [ "$poison" = 0 ] || fail "the poison queue holds $poison message(s)"
fi

step "The panel is up and protected"
code=$(curl -s -o /dev/null -w '%{http_code}' "$WEB/login")
[ "$code" = 200 ] || fail "GET /login gave $code, expected 200"
location=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "$WEB/deliveries")
[ "$location" = "307 $WEB/login" ] || fail "GET /deliveries without a session gave '$location', expected a redirect to /login"
headers=$(curl -sI "$WEB/login")
echo "$headers" | grep -qi '^content-security-policy:.*nonce-' || fail "the panel sent no Content-Security-Policy with a nonce"
echo "$headers" | grep -qi '^x-frame-options: DENY' || fail "the panel sent no X-Frame-Options"

step "The database is not published to the host"
published=$(docker port "$("${COMPOSE[@]}" ps -q postgres)" 2>/dev/null || true)
[ -z "$published" ] || fail "PostgreSQL is published to the host: $published"
if [ "$TRANSPORT" = rabbitmq ]; then
  # Only the management page, and only on this computer. The broker's own port must not be reachable.
  broker=$(docker port "$("${COMPOSE[@]}" ps -q rabbitmq)" 2>/dev/null | grep -v '^15672/tcp -> 127.0.0.1:15672$' || true)
  [ -z "$broker" ] || fail "RabbitMQ publishes more than its management page on 127.0.0.1: $broker"
fi

step "The containers do not run as root"
for service in api worker web; do
  user=$("${COMPOSE[@]}" exec -T "$service" id -u 2>/dev/null || echo unknown)
  [ "$user" != 0 ] || fail "$service runs as root"
done

# Last, because a client that has been refused gets no answer about keys for a minute, valid ones included.
step "Too many failed attempts are refused"
wrong='whk_000000000000_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
refused_at=""
for i in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $wrong" "$API/deliveries")
  if [ "$code" = 429 ]; then refused_at=$i; break; fi
  [ "$code" = 401 ] || fail "a wrong key gave $code on attempt $i, expected 401 or 429"
done
# The default is 20 failures a minute, so the 21st attempt is the first one refused.
[ "$refused_at" = 21 ] || fail "wrong keys were first refused with 429 at attempt '${refused_at:-never}', expected 21"
retry=$(curl -s -D - -o /dev/null -H "Authorization: Bearer $wrong" "$API/deliveries" | grep -i '^retry-after:' | tr -d '\r' | awk '{print $2}')
{ [ -n "$retry" ] && [ "$retry" -ge 1 ] && [ "$retry" -le 60 ]; } || fail "no sensible Retry-After header (got '${retry:-none}')"
curl -fsS "$API/health" >/dev/null || fail "the health check was refused for a client over the limit"

printf '\nAll checks passed.\n'
