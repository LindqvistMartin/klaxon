# ADR-004: Alert dedup and escalation state

## Status

Accepted and implemented. The dedup/open-invariant indexes and the jsonb storage shipped in
the initial migration; the state-machine methods (`Advance`/`Ack`/`Resolve`) landed with the
engine.

## Context

Two related decisions shape how alerts turn into pages: how a repeated alert collapses
so a flapping source does not page over and over, and what the escalation lifecycle
looks like once one is open.

## Decision

### Dedup and flap suppression

An alert carries `(Source, DedupKey)`. Two invariants, both enforced in the database
as **filtered unique indexes** rather than plain constraints:

- `IX_Alerts_OpenDedup` — unique on `(Source, DedupKey)` `WHERE "Status" = 'Open'`.
  Ingestion upserts onto the open row (`ON CONFLICT`), so a re-firing alert reuses the
  existing row instead of creating a second one.
- `IX_Escalations_Open` — unique on `(AlertId)` `WHERE "State" NOT IN ('Resolved',
  'Exhausted')`. At most one live escalation per alert.

Together these mean a flapping alert cements onto one open escalation instead of
launching a fresh page storm. The filter is the load-bearing part: a plain unique
index on `(Source, DedupKey)` would be wrong, because it would forbid a *new* alert
after the previous one resolved. The invariant is "one *open* per key", not "one ever",
and only a partial index expresses that.

### Escalation states

`EscalationState { Triggered, Notified, Acked, Resolved, Exhausted }`. The legal
transitions live as a matrix constant in the domain, and the entity validates them:
`Advance`, `Ack`, and `Resolve` throw on an illegal move, so the rules sit in one place
in `Klaxon.Core` instead of a `switch` in a controller.

- **Ack is idempotent.** The first ack moves `Triggered`/`Notified` to `Acked` and
  stops the clock. A repeated ack, or a late ack that arrives after `Resolve`, is a
  no-op. This is what makes at-least-once notification (ADR-003) safe: a duplicate page
  acks to the same state.
- **Exhaustion is loud, never silent.** All levels passed with no ack moves the
  escalation to `Exhausted` — a terminal state with an ERROR log — and writes an
  `EscalationExhausted` row through the outbox (ADR-003) for delivery like any other
  page. It is never a quiet drop. A metric belongs here too, once there is a meter to
  raise it on (see ADR-003's consequences).
- `Acked` is deliberately still "open" for the `IX_Escalations_Open` filter: an
  acknowledged page still holds the slot for its alert until it resolves.

### jsonb targets and participants

`EscalationLevel.Targets` and `Schedule.ParticipantOrder` are stored as jsonb, not as
child tables. The tradeoff, accepted with eyes open: this gives up foreign-key
integrity for those references — a target can name a user id that was later deleted,
and nothing at the database level stops it. That is acceptable because both are small
ordered config blobs edited as a unit (a PagerDuty-style level definition), and child
tables would add joins and an ordering column for a shape we only ever read whole.
References are validated at the application layer instead. Enums inside the jsonb are
written by name (see `JsonbConverters`), matching the `HasConversion<string>()` columns,
so reordering an enum cannot silently re-map stored rows.

## Consequences

**Plus**

- Flap suppression is real and enforced by the database, not by hopeful application
  code.
- The state machine is a pure domain concern, testable without a database, with the
  transition matrix in one auditable place.
- Level and participant config is stored and read as a single value, matching how it is
  edited.

**Minus**

- jsonb references are not FK-checked; a dangling target id is possible and must be
  validated in the application and tolerated at read time.
