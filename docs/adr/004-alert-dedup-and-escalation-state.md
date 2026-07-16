# ADR-004: Alert dedup and escalation state

## Status

Accepted and implemented. The indexes and the jsonb storage shipped in the initial
migration; the state-machine methods (`Advance`/`Ack`/`Resolve`) landed with the engine;
dedup landed with alert ingestion, which is the only thing that creates an alert.

Until ingestion existed, the dedup half of this record described an intention rather than
behaviour: no code read the index, and nothing could move an alert off `Open`, so the
filter that makes it a *partial* index could never be false and it constrained every row
ever written. `Alert.Resolve` is what closes that.

## Context

Two related decisions shape how alerts turn into pages: how a repeated alert collapses
so a flapping source does not page over and over, and what the escalation lifecycle
looks like once one is open.

## Decision

### Dedup and flap suppression

An alert carries `(Source, DedupKey)`. Two invariants, both enforced in the database
as **filtered unique indexes** rather than plain constraints:

- `IX_Alerts_OpenDedup` — unique on `(Source, DedupKey)` `WHERE "Status" = 'Open'`.
  Ingestion looks the open row up and reuses it, so a re-firing alert rides the incident
  already running instead of opening a second one.
- `IX_Escalations_Open` — unique on `(AlertId)` `WHERE "State" NOT IN ('Resolved',
  'Exhausted')`. At most one live escalation per alert.

Together these mean a flapping alert cements onto one open escalation instead of
launching a fresh page storm. The filter is the load-bearing part: a plain unique
index on `(Source, DedupKey)` would be wrong, because it would forbid a *new* alert
after the previous one resolved. The invariant is "one *open* per key", not "one ever",
and only a partial index expresses that.

The lookup is a read followed by an insert, not an `ON CONFLICT` upsert. The index rather
than the lookup is what holds the invariant: two ingests racing on a key that is not open
yet both miss the read, and the second insert takes the unique violation instead of opening
a duplicate incident.

### One escalation per alert row

An escalation is created when an alert row is inserted, and only then. A firing that
deduplicates onto an open alert neither opens a second escalation nor restarts the first:
once a policy has paged every level and nobody answered, repeated firings attach to the
incident that is already there rather than re-running the ladder.

That leaves one state deliberately holed: an alert still `Open` whose escalation has
`Exhausted`. It holds the dedup key, so re-firings attach to it and page nobody until a
resolved notification closes it. This is not the silent non-page ADR-001 exists to prevent —
reaching `Exhausted` means every level was paged, an ERROR was logged, and an
`EscalationExhausted` row went through the outbox, which is as loud as this system gets. It
is the absence of a re-notification policy, and that is where one belongs when there is a
reason to write one.

Because only an insert creates an escalation, no code path attempts a second one for the
same alert, so `IX_Escalations_Open` cannot fire from anything the product does today. It
stays as the constraint that keeps the rule true in the database rather than only in an
endpoint.

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
- Two ingests racing on a key nobody has opened yet leave the loser holding a 409, saying
  "conflict" about an alert its twin ingested a millisecond earlier. Nothing is lost — the
  incident is open and its page went out — but the answer is wrong for what happened. An
  upsert would answer 202 to both by serialising them on the conflicting row's lock; that is
  the reason to reach for one, and a hand-written INSERT column list that drifts the first
  time `Alerts` gains a column is the reason not to yet.
- A deduplicated firing writes nothing, so the stored payload stays the one that opened the
  incident rather than the most recent. Where a payload is a snapshot of a condition that is
  still true, that is the more useful of the two; it is a choice, not an oversight.
