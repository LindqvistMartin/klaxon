# ADR-005: Alert ingestion

## Status

Accepted and implemented by the ingest endpoint.

## Context

Two decisions shape the one endpoint that turns an alert into a page: which policy pages
for it, and what a 202 promises the sender.

Both have to hold for a source that cannot be taught anything about Klaxon. Alertmanager
posts its own payload to a URL it was configured with, treats any 2xx as delivered, and
never sends that notification again.

## Decision

### The ingest URL names the policy

`POST /api/v1/alerts/ingest/{source}/{policyId}`. The URL an operator pastes into their
monitoring system is the binding, so configuring an integration means generating a URL
rather than editing a routing table. Nothing else in the schema connects an alert to a
policy: an `Alert` carries a source, a dedup key and a payload; an `EscalationPolicy`
belongs to a team and has no notion of what it answers for.

This is the shape the category already uses: Grafana OnCall integrates at
`/integrations/v1/{type}/{token}/`, and PagerDuty's Events API carries a `routing_key` that
resolves to a service and its escalation policy. An unknown policy is a 404, because the
path names a resource that does not exist.

`{source}` is not decoration: it is half of the `(Source, DedupKey)` dedup identity
(ADR-004), so one system's keys cannot collide with another's. It is also where a payload
parser gets selected once there is more than one shape to parse.

**Rejected.** *Policy id in the request body* — works for a caller written against Klaxon
and nothing else. *Routing rules matching alert labels to policies* — where a mature product
ends up, and it needs a rule engine, a precedence order and an editor; with one payload
shape it would be a table with one row per URL we could have generated instead. *A default
policy per team* — a team holds many policies with nothing to rank them, so "the default"
would mean "whichever the database returned first."

### The 202 means committed, not queued

Ingestion writes the alert and its escalation, commits, and only then answers 202. The
endpoint is synchronous down to the transaction; nothing is buffered.

The alternative is to accept the alert into an in-memory queue, answer 202 immediately, and
persist on a worker. It is the faster endpoint and the usual shape, and here it would be
wrong: a source reads 2xx as *delivered* and drops the notification, so anything still in
memory when the process dies is gone, and nobody learns that the page was owed. An
acknowledgement is a promise about durability, and it can only be made after the commit that
makes it true.

That is affordable because the work behind the ack is two indexed statements and a commit,
not a network call. Paging is what takes unbounded time, and it already happens elsewhere:
the engine claims the escalation on its next tick and the outbox carries the page (ADR-001,
ADR-003). The endpoint's job ends at "this alert is durable and something is now responsible
for it," which is exactly what 202 says.

## Consequences

**Plus**

- No new entity, no routing table, no migration: the binding uses an id that already exists.
- Every source can carry it, because every source can be given a URL.
- An alert that has been acknowledged has been persisted, so a crash costs latency rather
  than a page.

**Minus**

- **The policy id travels in a URL held by an external system, which makes an identifier
  double as a routing address.** It is not a secret and is not treated as one. That barely
  matters today and not for a reassuring reason: ingestion is unauthenticated, so is every
  other endpoint here, and `GET /escalation-policies` hands every id to anyone who asks — the
  id is not what exposes a team, the missing auth is. What this route adds is blast radius,
  since it is the first endpoint that turns an unauthenticated request into a page. The
  follow-up is an `Integration` entity with a rotatable token: the token becomes the
  credential, ingestion authenticates against it, and the URL stops naming a domain object.
- **Dedup is scoped by `(Source, DedupKey)` and nothing else, so it is global to the
  deployment rather than per team.** Two teams whose Prometheus servers both post as
  `source=prometheus` and both key on an alert name share a namespace: the second team's
  firing deduplicates onto the first team's open incident and pages nobody. Until an
  `Integration` owns the dedup scope, `{source}` is what keeps them apart, and it has to be
  distinct per integration rather than named after the software. `Alert` has no team column
  to scope by, which is why this is the URL's problem today.
- Ingest does no admission control: no rate limit, no bounded concurrency. Under a storm the
  connection pool is the queue, and its overflow surfaces as a 5xx — which a source retries,
  so the alert survives and the latency does not.
- Re-pointing a source at a different policy means re-configuring the source. An incident
  already open keeps riding the policy it opened under, because the policy is chosen once,
  when the escalation is created.
