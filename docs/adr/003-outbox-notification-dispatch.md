# ADR-003: Outbox-driven notification dispatch

## Status

Accepted. The producer side (the escalation engine writing an `OutboxMessage` in its
claim transaction) is designed in ADR-001; the dispatcher and channels land with the
notification milestone.

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
change. Advancing a level persists, in one `SaveChangesAsync`: the escalation update,
a `Notification` row, and an `OutboxMessage`.

A `NotificationDispatcher : BackgroundService` polls unprocessed outbox rows with
`FOR UPDATE SKIP LOCKED`, hands each to the matching `INotificationChannel`
(`EmailChannel`, `SlackChannel`, `LogChannel`, `WebhookChannel`), and stamps
`ProcessedAt` once the send returns. Delivery is decoupled from the transaction, so
there is no window where state is committed but the page is lost, and no window where
a page precedes a rolled-back commit.

Exhaustion takes the same path deliberately. When a policy runs out of levels with no
ack, the engine does not call Flare (or a fallback target) directly over HTTP — it
writes a `WebhookChannel` outbox row. A direct call would strand the "never a silent
drop" guarantee on exactly the crash between committing the exhausted state and making
the call. Routing exhaustion through the outbox keeps it as durable as every other
page.

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
- Not yet implemented. The producer contract is fixed (ADR-001's claim transaction
  will carry the `OutboxMessage`); the dispatcher, channels, and the
  `Notification`/`OutboxMessage` tables arrive with the notification milestone.
