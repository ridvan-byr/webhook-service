// Sends a burst of events through the API of a running stack and reports how fast they were delivered.
// Started by scripts/loadtest.sh; run it by hand against your own stack with the same environment variables.
//
// What it measures, and from where:
//   ingest      the API, from the client: how fast it accepted the events and how long each request took.
//   throughput  deliveries finished per second, from the first event being stored to the last attempt ending.
//   pickup      how long a delivery waited between being stored and its attempt starting. This is the number that
//               tells transports apart. Both are read from timestamps the service itself wrote to the database.
//
// Needs Node 20 or later (global fetch) and docker compose, to read the database. No other dependencies.

import { execFileSync } from "node:child_process";

const API = process.env.API ?? "http://127.0.0.1:5005";
const ADMIN_API_KEY = required("ADMIN_API_KEY");
const EVENTS = Number(process.env.EVENTS ?? 2000);
const CONCURRENCY = Number(process.env.CONCURRENCY ?? 32);
// Events per second to send. 0 (the default) sends them as fast as possible, which measures how fast a backlog is
// worked off. A rate below what the service can deliver measures how long a delivery waits when nothing is queued.
const RATE = Number(process.env.RATE ?? 0);
const RECEIVER_URL = process.env.RECEIVER_URL ?? "http://receiver:9000/ok";
const TIMEOUT_SECONDS = Number(process.env.TIMEOUT_SECONDS ?? 300);
const COMPOSE = (process.env.COMPOSE ?? "docker compose --profile demo").split(" ");

function required(name) {
  const value = process.env[name];
  if (!value) {
    console.error(`Set ${name}.`);
    process.exit(2);
  }
  return value;
}

async function call(method, path, headers, body) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: { "Content-Type": "application/json", ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`${method} ${path} answered ${response.status}: ${(await response.text()).slice(0, 200)}`);
  }
  return response.json();
}

function sql(query) {
  const [command, ...args] = COMPOSE;
  return execFileSync(
    command,
    [...args, "exec", "-T", "postgres", "psql", "-U", "webhooks", "-d", "webhooks", "-At", "-F", "|", "-c", query],
    { encoding: "utf8" },
  ).trim();
}

function percentile(sorted, p) {
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1))];
}

const tenant = await call("POST", "/admin/tenants", { "X-Admin-Key": ADMIN_API_KEY }, { name: `load-${Date.now()}` });
const auth = { Authorization: `Bearer ${tenant.apiKey}` };
const endpoint = await call("POST", "/endpoints", auth, { url: RECEIVER_URL });

console.log(`Sending ${EVENTS} events with ${CONCURRENCY} at a time to ${endpoint.url ?? RECEIVER_URL} ...`);

const latencies = [];
let next = 0;
const ingestStart = performance.now();
await Promise.all(
  Array.from({ length: CONCURRENCY }, async () => {
    while (next < EVENTS) {
      const number = next++;
      if (RATE > 0) {
        const wait = ingestStart + (number / RATE) * 1000 - performance.now();
        if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));
      }
      const started = performance.now();
      await call("POST", "/events", auth, { type: "load.test", payload: { number } });
      latencies.push(performance.now() - started);
    }
  }),
);
const ingestSeconds = (performance.now() - ingestStart) / 1000;

const where = `d."EndpointId" = '${endpoint.id}'`;
const deadline = Date.now() + TIMEOUT_SECONDS * 1000;
let finished = 0;
while (Date.now() < deadline) {
  finished = Number(sql(`SELECT count(*) FROM deliveries d WHERE ${where} AND d."Status" = 'Succeeded'`));
  if (finished >= EVENTS) break;
  await new Promise((resolve) => setTimeout(resolve, 500));
}

latencies.sort((a, b) => a - b);

const [firstCreated, lastEnded, p50, p95, p99, max, attempts] = sql(`
  SELECT
    extract(epoch FROM min(d."CreatedAt")),
    extract(epoch FROM max(a."StartedAt" + a."DurationMs" * interval '1 millisecond')),
    percentile_cont(0.50) WITHIN GROUP (ORDER BY extract(epoch FROM a."StartedAt" - d."CreatedAt") * 1000),
    percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM a."StartedAt" - d."CreatedAt") * 1000),
    percentile_cont(0.99) WITHIN GROUP (ORDER BY extract(epoch FROM a."StartedAt" - d."CreatedAt") * 1000),
    max(extract(epoch FROM a."StartedAt" - d."CreatedAt") * 1000),
    count(*)
  FROM deliveries d JOIN delivery_attempts a ON a."DeliveryId" = d."Id"
  WHERE ${where}`).split("|").map(Number);

const seconds = lastEnded - firstCreated;
const result = {
  events: EVENTS,
  concurrency: CONCURRENCY,
  rate: RATE || "as fast as possible",
  delivered: finished,
  attempts,
  complete: finished >= EVENTS,
  ingest: {
    eventsPerSecond: Math.round(EVENTS / ingestSeconds),
    requestMsP50: Math.round(percentile(latencies, 50)),
    requestMsP95: Math.round(percentile(latencies, 95)),
    requestMsP99: Math.round(percentile(latencies, 99)),
  },
  throughput: { seconds: Number(seconds.toFixed(2)), deliveriesPerSecond: Math.round(finished / seconds) },
  pickupMs: { p50: Math.round(p50), p95: Math.round(p95), p99: Math.round(p99), max: Math.round(max) },
};

console.log(JSON.stringify(result, null, 2));
if (!result.complete) {
  console.error(`Only ${finished} of ${EVENTS} deliveries finished within ${TIMEOUT_SECONDS} seconds.`);
  process.exit(1);
}
