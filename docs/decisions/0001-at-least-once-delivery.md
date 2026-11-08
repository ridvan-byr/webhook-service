# 0001 - At-least-once delivery with a PostgreSQL-backed queue

Status: accepted

## Context

The service must deliver events to subscriber URLs reliably. Subscribers can be
down, slow, or return errors. We also want few moving parts for the first
version.

## Decision

- Delivery guarantee is **at-least-once**. Exactly-once delivery over HTTP is not
  achievable in practice, so every event carries a stable id that receivers can
  use to detect duplicates.
- An event and its delivery tasks are written in **one database transaction**,
  so an accepted event is never lost if the API process crashes afterwards.
- Workers claim due deliveries with `SELECT ... FOR UPDATE SKIP LOCKED`, so two
  workers never pick the same task.
- Failed attempts are retried with exponential backoff plus jitter, up to a
  configurable limit, after which the delivery becomes `dead` and can be
  re-sent manually.

## Consequences

- No extra infrastructure (broker) is needed for v0.1.
- Receivers must be idempotent.
- The queue is behind an interface, so RabbitMQ or Kafka transports can be added
  later without changing the core.
