# ADR-003: Outbox-driven notification dispatch

## Status

Accepted and implemented, minus the remote channels. The engine writes an `OutboxMessage` in its
claim transaction and a `NotificationDispatcher` drains it through `INotificationChannel`. One
channel exists, `LogChannel`, so the loop is closed end to end; email, Slack, and outbound webhooks
are still ahead, and the note under Decision about what a network channel changes applies the moment
the first one lands.

The `Notification` row this ADR originally paired with every outbox message is **deferred**. It was
there to record *who was paged*, and the engine cannot answer that yet: resolving an
`EscalationTarget` of kind `Schedule` to a person needs `OnCallResolver`, which has no production
caller and no snapshot loader behind it. Everything else a `Notification` could carry today —
escalation, level, instant — is already on the outbox row, so it would be a duplicate with an FK.
It lands with on-call resolution, holding the resolved person at the resolved instant.

## Context

When the escalation engine decides to page — advancing to a level, or exhausting a
policy — it must do two things: record the state change, and deliver a notification
(email, Slack, an outbound webhook). The obvious approach sends the notification
straight from the engine tick, right after the state change commits.

That approach has two failure modes, both observable and both bad for a pager:

- **Commit-then-send.** The transaction commits, then the process dies (or the SMTP
  call hangs and the tick is killed) before the notification goes out. The database
  says level 1 was paged; nobody was. This is the same silent non-page that ADR-001
  exists to prevent, reintroduced one layer up.
- **Send-then-commit.** The notification fires, then the transaction rolls back on a
  constraint violation. Someone is paged for an escalation that does not exist.

## Decision

The engine writes the notification intent into the **same transaction** as the state
change. Advancing a level persists, in one `SaveChangesAsync`, the escalation update
and an `OutboxMessage`.

A `NotificationDispatcher : BackgroundService` polls unprocessed outbox rows with
`FOR UPDATE SKIP LOCKED`, hands each to every registered `INotificationChannel`, and
stamps `ProcessedAt` once the send returns. Delivery is decoupled from the *engine's*
transaction, so there is no window where state is committed but the page is lost, and
no window where a page precedes a rolled-back commit.

A row is stamped only once its send has returned, which is what makes delivery
at-least-once: a send that throws leaves the row for the next tick. Stamping first and
delivering afterwards is what a dashboard fan-out can afford and a pager cannot — a
channel failure there is a page lost with the database claiming it was sent. The cost is
that the send sits inside the claim transaction, holding a row lock and a pooled
connection for its duration. That is free for a local channel; the first channel that
makes a network call needs the claim and the stamp split into two transactions, and that
claim — outbox rows held across a send — is where a lease column earns its keep (ADR-001
removed the one it had put on `Escalations`, for the same reason in reverse).

Exhaustion takes the same path deliberately. When a policy runs out of levels with no
ack, the engine does not call Flare (or a fallback target) directly over HTTP — it
writes an `EscalationExhausted` outbox row. A direct call would strand the "never a
silent drop" guarantee on exactly the crash between committing the exhausted state and
making the call. The type names *what happened*, not how to deliver it; picking targets
belongs downstream, and a producer naming a channel would be deciding from the wrong end.

This is the same outbox pattern used in PulseWatch and Flare. Reusing it across the
suite is a consistency choice, not a coincidence — the same reasoning about
commit/send ordering applies to every one of them.

## Consequences

**Plus**

- Transactional consistency: the state change and the notification intent land
  together or not at all.
- A page is never lost between "decided" and "sent", and never sent for a change that
  did not persist.
- A new channel is one `INotificationChannel` implementation plus a DI registration.

**Minus**

- At-least-once, not exactly-once: a page can arrive twice (a send that succeeds but
  whose `ProcessedAt` write then fails is retried). Acceptable, and harmless because
  ack is idempotent (ADR-004) — a duplicate page acks to the same state.
- A latency floor of roughly one poll interval between "decided" and "sent". Fine for
  human paging.
- A send that fails is retried on every tick forever: there is no attempt counter and nothing
  parks a poison row, so a permanently unreachable target is an endless 1/second retry rather
  than a dead letter. It is contained — a failed send skips only its own row, and the rest of
  the batch still commits — but it is unbounded. Unreachable while the only channel is local
  and cannot fail; the counter lands with the two-transaction split the network channels force
  anyway.
- Exhaustion is loud in the log and in the outbox, but not yet in metrics — there is no
  meter anywhere in the service to raise. ADR-004 asks for one; it waits on instrumentation.
