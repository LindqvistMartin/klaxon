# ADR-001: Durable, resumable escalations

## Status

Accepted.

## Context

An escalation is a timer with teeth: "page level 1, wait 5 minutes for an ack,
and if none comes, page level 2." The obvious implementation holds that timer in
the process — a `Task.Delay`, a `System.Threading.Timer`, or a job handed to a
background scheduler.

Every in-process variant shares one fatal property: **a restart loses the timer.**
Deploys, crashes, OOM-kills, and host migrations all restart the process, and they
happen most often precisely when something is on fire — which is when an escalation
is most likely to be mid-flight. A paging system that forgets who it was trying to
wake after a mid-incident deploy is worse than useless: it is silently useless. The
on-call engineer assumes they will be paged, and they are not.

This is the failure mode the whole product exists to prevent, so it drives the
central design decision rather than being an afterthought.

## Decision

**Escalation state lives in Postgres, and the engine is stateless.**

The `Escalations` table carries everything needed to resume: `State`,
`CurrentLevel`, `NextTimeoutAt`, and `LeaseUntil`. A single
`EscalationEngine : BackgroundService` runs a `PeriodicTimer` (~1s) that atomically
claims due rows and advances them — pages the current level, or marks the
escalation `exhausted` when the levels run out. Advancing a level writes, in one
transaction, the level change plus a `Notification` and an `OutboxMessage` (see
ADR-003).

The claim is atomic under concurrency:

```sql
UPDATE "Escalations" SET "LeaseUntil" = now() + interval '30 seconds'
WHERE "Id" IN (
  SELECT "Id" FROM "Escalations"
  WHERE "State" IN ('Triggered', 'Notified')
    AND "NextTimeoutAt" <= now()
    AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= now())
  ORDER BY "NextTimeoutAt"
  FOR UPDATE SKIP LOCKED
  LIMIT 100)
RETURNING *;
```

`FOR UPDATE SKIP LOCKED` plus the `LeaseUntil` lease means two concurrent ticks — or
a boot scan that overlaps a still-running tick, or a second instance during a
rolling deploy — never page the same escalation twice. A worker that crashes
mid-batch simply lets its lease lapse, and the row is reclaimed on a later tick.

**Resumption needs no special code.** On startup the engine runs the exact same
scan. Any escalation whose `NextTimeoutAt` has passed is due, gets claimed, and is
advanced — whether it became due one second ago or during the ninety seconds the
process was being redeployed. Resumability is an ordinary query, not a recovery
routine, and that is the point: there is no separate "recover in-flight timers"
path to get wrong.

A partial index keeps the poll cheap as the table accumulates closed rows:

```sql
CREATE INDEX "IX_Escalations_Due" ON "Escalations" ("NextTimeoutAt")
  WHERE "State" IN ('Triggered', 'Notified');
```

Resolved and exhausted escalations fall out of the index entirely, so the once-a-second
scan touches only live work.

Deliberately **not** chosen:

- **A dedicated job scheduler (Quartz and the like).** It reintroduces a component
  to operate and back up, and its durability guarantees are weaker and fuzzier than
  "the row is still in the table." A `BackgroundService` over a Postgres table is
  less code and stronger.
- **`LISTEN`/`NOTIFY`.** The trigger here is *time elapsing*, not an event. There is
  nothing to notify on. A cheap indexed poll is the natural fit.

## Consequences

**Plus**

- Survives restarts by construction. The contract for this ADR is a restart-recovery
  integration test: create an escalation with a due `NextTimeoutAt`, dispose and
  recreate the application over the *same* database, and assert level 2 fires. It
  lands with the engine, and if it ever fails the product's core promise is broken.
- No external scheduler. One process, one Postgres, one `docker compose up`.
- The claim query is the same at steady state and at boot, so there is a single code
  path to reason about.

**Minus**

- A latency floor of roughly one poll interval (~1s) between "timeout elapsed" and
  "page sent." For human paging this is irrelevant; nobody notices a one-second
  difference on a five-minute escalation timer.
- The lease duration (30s) bounds how long a crashed worker's claimed rows sit
  before another tick can reclaim them. Short enough to be invisible to humans, long
  enough to comfortably exceed one tick.
