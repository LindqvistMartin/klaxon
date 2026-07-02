# ADR-002: On-call resolution on NodaTime

## Status

Accepted.

## Context

The question "who is on call at instant T?" has to be correct across daylight-saving
transitions and rotation handoffs, in every timezone a team operates in. This is
where naive on-call tools quietly page the wrong person.

The BCL time types make the naive version easy to reach for and wrong:

- `DateTime` carries no timezone at all. Arithmetic on it silently assumes the
  machine's local zone or UTC, and a handoff computed one way on the server and
  another way in the client drifts apart.
- `DateTimeOffset` fixes an *offset*, not a *zone*. It knows "+01:00", but it does
  not know that `Europe/Berlin` is +01:00 in January and +02:00 in July. A rotation
  that hands off at 09:00 Berlin time must resolve to a different absolute instant in
  summer than in winter; an offset-based implementation pages the previous engineer
  for an extra hour twice a year, or double-pages during the fall-back overlap.

These are the worst kind of bug: rare, seasonal, and invisible until someone is not
woken up.

## Decision

**Use NodaTime, and make resolution a pure function.**

```
OnCallResolver.WhoIsOnCall(ScheduleSnapshot snapshot, Instant at) -> User?
```

The resolver is pure: the `ScheduleSnapshot` carries the participant order plus any
overrides, so the function performs no I/O and can be exhaustively table-tested. It
returns nullable — nobody on call is a real state (empty rotation, gap between
overrides) that the caller handles with a fallback, not an exception.

Rules:

- **Rotation boundaries are computed in the schedule's timezone**, which is the
  single source of truth. A participant's own timezone is display-only — used to
  render "on call until 09:00 their time," never to decide the handoff instant.
- **DST is explicit.** A handoff time that lands in a spring-forward gap (that local
  time never occurs) or a fall-back overlap (it occurs twice) is resolved through an
  explicit `ZoneLocalMappingResolver`, and the choice is recorded here rather than
  inherited by accident.
- **Overrides beat the computed rotation.** When an explicit override covers the
  instant, it wins; precedence is unambiguous and tested.

Storage: `Instant` maps to `timestamptz` through Npgsql's NodaTime plugin
(`o.UseNodaTime()`); IANA zone ids are stored as text (e.g. `"Europe/Berlin"`), which
is stable across tzdb updates in a way that fixed offsets are not.

Deliberately out of scope for v0.1: **per-zone follow-the-sun handoffs** (a rotation
that hands between regions as the day moves around the globe). That is a genuinely
harder model, and conflating it with the base case is how the base case gets bugs.
Single-timezone-per-schedule with correct DST handling covers the overwhelmingly
common case correctly first.

## Consequences

**Plus**

- Determinism. Because the resolver is pure and takes an `Instant`, its tests are
  table-driven and cover the cases that actually break: spring-forward gap,
  fall-back overlap, handoff across a DST boundary, and override precedence — with
  no clock, database, or HTTP to mock. This resolver is the correctness crown of the
  product, and it is the easiest part to test rigorously.
- The domain speaks the right language. `Instant`, `ZonedDateTime`, and
  `DateTimeZone` in the model make the timezone-correctness intent legible to anyone
  reading it, instead of hiding it behind ambiguous `DateTime`s.

**Minus**

- NodaTime is an additional dependency and a small learning curve for contributors
  used to the BCL types. Accepted deliberately: the bugs it removes are the silent,
  seasonal kind, and no amount of care makes hand-rolled `DateTimeOffset` math safe
  against DST.
