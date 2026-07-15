# ADR-001: Durable, resumable escalations

## Status

Accepted. Shipped in phases: the `EscalationEngine` claim/advance loop landed first, and the
`OutboxMessage` written inside the claim transaction landed with the notification milestone
(ADR-003).

The `LeaseUntil` lease this ADR originally prescribed is **superseded, and the column is gone**. Its
premise was that delivery would eventually hold a claimed escalation across the send, leaving a
crashed worker's in-flight row to be reclaimed only once its lease lapsed. ADR-003 did separate
delivery from the claim — but by moving it to a *different table*, not by holding the row longer.
The dispatcher locks `OutboxMessages` and never touches `Escalations`, so the escalation row is
never held across a network call and the row lock alone is the entire claim. A lease does belong to
the dispatcher's outbox claim once a channel makes a network call — a different column on a
different table (see ADR-003).

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
`CurrentLevel`, and `NextTimeoutAt`. A single
`EscalationEngine : BackgroundService` runs a ~1s poll loop that atomically
claims due rows and advances them — pages the current level, or marks the
escalation `exhausted` when the levels run out. Advancing a level writes, in one
transaction, the level change plus an `OutboxMessage` (see ADR-003).

The claim is atomic under concurrency:

```sql
SELECT * FROM "Escalations"
WHERE "State" IN ('Triggered', 'Notified')
  AND "NextTimeoutAt" <= now()
ORDER BY "NextTimeoutAt"
FOR UPDATE SKIP LOCKED
LIMIT 100;
```

`FOR UPDATE SKIP LOCKED` means two concurrent ticks — or a boot scan that overlaps
a still-running tick, or a second instance during a rolling deploy — never page the
same escalation twice: the lock is held for the life of the claim transaction, and a
second tick skips a locked row rather than blocking on it. Claim and advance share
that transaction, so a worker that crashes mid-batch rolls back and its rows are due
again on the very next tick. Postgres drops the lock with the backend that held it,
which makes recovery cost one poll interval and need no reclaim machinery at all.

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
- **A lease on the claim.** An earlier draft of this ADR claimed rows with a committing
  `UPDATE ... SET "LeaseUntil" = now() + interval '30 seconds' ... RETURNING *`. A claim that
  commits outlives its transaction, which is what creates the need for a lease to expire in the
  first place; keeping the claim inside the transaction makes the row lock the claim, and Postgres
  releases it on crash for free. The lease was also worse than no lease here: it delayed reclaim
  from one poll interval to the lease duration, and nothing cleared it on advance, so its
  `("LeaseUntil" IS NULL OR "LeaseUntil" <= now())` predicate would have silently stretched every
  ack window shorter than 30s out to 30s.

## Consequences

**Plus**

- Survives restarts by construction. The engine holds no in-process state, so a restart is
  just a fresh instance re-running the scan. The contract for this ADR is a restart-recovery
  integration test: create an escalation with a due `NextTimeoutAt`, run a fresh engine that
  shares nothing in-process against the *same* database, and assert it advances to the next
  level. It lands with the engine, and if it ever fails the product's core promise is broken.
- No external scheduler. One process, one Postgres, one `docker compose up`.
- The claim query is the same at steady state and at boot, so there is a single code
  path to reason about.

**Minus**

- A latency floor of roughly one poll interval (~1s) between "timeout elapsed" and
  "page sent." For human paging this is irrelevant; nobody notices a one-second
  difference on a five-minute escalation timer.
- A batch is all-or-nothing: one failed write rolls back every escalation the tick
  claimed, and they are all re-claimed on the next one. At a hundred rows a tick and a
  one-second retry that is a fair trade for never having to reason about a half-applied
  batch.
