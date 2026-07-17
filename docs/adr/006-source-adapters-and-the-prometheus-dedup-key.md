# ADR-006: Source adapters and the Prometheus dedup key

## Status

Accepted and implemented by the ingest endpoint's two parsers.

## Context

Until now every caller spoke the shape Klaxon defines: `dedupKey`, `status`, `payload`. That works
for a caller written against Klaxon and for nothing else, and it is the opposite of what ingestion
is for. Alertmanager posts its own envelope to a URL it was configured with and cannot be taught
anything about us.

It is also the second shape, which is what makes this decision possible to take honestly. With one
shape, a parser abstraction would have been a guess about the second; with two, the shape of the
seam is a fact about code that exists.

Three things had to be settled: how a parser is chosen, what its interface looks like, and what
Alertmanager's identity for an alert group turns into once it reaches a 200-character column.

## Decision

### `{format}` chooses the parser, `{source}` stays the integration's name

ADR-005 said `{source}` would select the parser once there was a second shape. That was wrong, and
its own Minus is the argument against it: dedup is `(Source, DedupKey)` and global to the
deployment, so `{source}` "has to be distinct per integration rather than named after the
software." A parser is chosen from a **closed** set of names the code knows; an identity has to be
an **open** one the operator picks. One segment cannot be both. Asking `{source}` to name the
parser would force every Alertmanager on the deployment to call itself `prometheus`, which is
precisely the collision ADR-005 warns about — two teams sharing a dedup namespace, the second
never paged.

So the route splits: `POST /api/v1/alerts/ingest/{format}/{source}/{policyId}`. `{format}` is
`generic` or `prometheus`; `{source}` is whatever names the integration, `team-a-prom` included.
This is also the model ADR-005 cited and did not follow — Grafana OnCall's
`/integrations/v1/{type}/{token}/` splits shape from identity across two segments.

An unregistered format is a **404**, for the same reason an unknown policy is: the path names
something that does not exist. It is never a fallback to the generic parser. A fallback would turn
a mistyped URL into an Alertmanager envelope handed to a parser that cannot read it — a 400, which
Alertmanager drops permanently, so a typo would become a silent non-page that looks healthy from
both ends.

`{format}` is lowercased before the lookup. That is safe only because it is a vocabulary and not an
identity: normalising `{source}` would rewrite what existing rows dedup against, while normalising
this costs nothing and spares an operator a debugging session over `/Prometheus/`.

**Rejected.** *Exact-match on `{source}`* — see above; it makes ADR-005's collision mandatory
instead of merely tempting. *Prefix matching (`prometheus-*`)* — invents URL syntax nobody asked
for, and hands the Alertmanager parser to `prometheus-clone-custom` by accident. *A config-driven
map from source to format* — reintroduces the routing table ADR-005 rejected, and "edit a config to
add an integration" is what the URL-is-the-binding decision exists to avoid.

### The parser interface is one synchronous method

```csharp
public interface IAlertSourceAdapter
{
    IngestAlertRequest Parse(JsonElement body);
}
```

`IngestAlertRequest` is the normal form, so the generic parser is the identity parse and the
Prometheus one maps onto it. There is no `Task`, because a parse is a pure function of a body
already in memory; no `CancellationToken`, because there is nothing to cancel; no header bag,
because nothing reads one; and no `Source` property, because the name is a fact about the
registration site rather than the instance. Each of those is a member that would have been written
once and read never, which is what got `INotificationChannel.Name` and `Escalation.LeaseUntil`
deleted.

The seam earns itself on more than parsing: the two implementations disagree about *policy*, not
just format. See the dedup key below.

### The Prometheus dedup key is the SHA-256 of `groupKey`, always

Alertmanager posts one notification per alert **group**, so the group is the incident: `groupKey`
identifies it, the top-level `status` says whether the whole group has cleared, and the envelope is
the payload. Nothing reads `alerts[]`, which is why nothing can drop the second and later alerts of
a group.

`groupKey` cannot be the dedup key as it stands. It is `routeKey:groupLabels`, the route key
recurses up the routing tree, and nothing truncates it — a routine `group_by` on a handful of
Kubernetes labels passes 200 characters, which is what `Alert.MaxDedupKeyLength` allows. Unhashed,
that is a 400; Alertmanager retries 5xx only, so a 400 is permanent. The group would never open an
incident, nobody would be paged, and Klaxon would hold no record that it ever fired — the failure
ADR-001 exists to prevent, arriving on day one of a stock deployment.

Hashing is unconditional rather than a fallback for long keys: one rule is easier to trust than
two, and the boundary between them would be invisible in the column. The guard lives in the parser
and not in `Alert`, because the 200-character limit is right for a generic caller and wrong here: a
generic caller *chooses* its key and can shorten it, while an operator cannot shorten `groupKey`
without changing `group_by`, which changes their alerting. That is a per-source policy difference,
and it is the strongest thing the seam buys.

**Rejected.** *Truncation* — the discriminating labels sit at the **end** of a `groupKey`, and the
shared prefix is longest exactly in the deep-route configurations that would trigger truncation, so
two groups would collapse onto one incident and the second would page nobody. It trades a loud
failure for a silent one. *Widening the column* — `groupKey` is unbounded, so every width has the
same cliff, and a btree index row caps out around 2704 bytes, which relocates the failure to a
`54000` at `SaveChanges` — a 500, retried forever. It also needs a migration to defer a problem.

### A body that cannot be parsed is refused, never ignored

202 means committed (ADR-005), and Alertmanager reads any 2xx as delivered and never resends. So an
envelope Klaxon does not understand cannot be answered 202 — that would be a page owed to nobody
with an INFO log for a headstone. It is a 400. `Ignored` stays what it already meant: a valid
notification with genuinely nothing to do, like a resolve for a key with nothing open.

## Consequences

**Plus**

- An operator can run `prometheus/team-a-prom` and `prometheus/team-b-prom` against one parser, so
  ADR-005's dedup collision stays avoidable rather than becoming mandatory.
- A new payload shape is one class and one keyed registration; the endpoint does not change.
- No migration, and no new entity: the split is a route segment and a lookup.
- Alertmanager's own identity for a group survives arbitrary `group_by` configurations.

**Minus**

- **The hash is a persisted contract.** Change the algorithm or the encoding later and every open
  Prometheus alert's key changes with it: resolves stop matching, those rows stay `Open` holding
  their slot in `IX_Alerts_OpenDedup`, and every later firing of the group deduplicates onto a
  zombie and pages nobody. Migrating the key means rewriting the column, not just the code.
- **`DedupKey` is not human-readable for Prometheus rows.** Recoverable rather than lost — the
  verbatim `groupKey` is in the payload, so `"Payload"->>'groupKey'` finds the row — but it makes
  storing the envelope unmodified a requirement rather than a preference.
- **Incident granularity is the operator's `group_by`, silently.** Both ends of that dial are
  sharp. Set richly, the key is long and the hash absorbs it. Left unset — the Alertmanager default
  — every alert on a route shares one constant `groupKey`, so one incident swallows the deployment
  and everything after the first deduplicates onto it and pages nobody until the whole group
  clears. Klaxon cannot detect the difference; integration docs have to say `group_by` is a
  deliberate choice.
- **Every 400 Klaxon returns to Alertmanager is a page nobody gets.** Alertmanager retries 5xx and
  drops 4xx, so validation on this endpoint is not the usual trade between strictness and
  convenience: refusing a body is refusing a page. This is why the parser is strict about the two
  fields it needs and indifferent to every other, and why "cannot parse" is loud rather than tidy.
- **A group that grows keeps the payload it opened with.** Alerts joining an open group after the
  first notification are never stored, because a deduplicated firing writes nothing (ADR-004). That
  decision was taken for single alerts, where the payload is a snapshot of a condition that is still
  true; for a group it also means the alert list ages. Changing it means reversing ADR-004.
- **`truncatedAlerts` is preserved and never acted on.** Alertmanager sets it when it drops alerts
  from a large group before sending. It rides in the payload for a responder to read, and nothing
  branches on it.
- Ingest still does no admission control (ADR-005), and a group is a larger body than a single
  alert — bounded in practice only by Kestrel's 30 MB default.
