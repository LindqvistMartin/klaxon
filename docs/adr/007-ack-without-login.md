# ADR-007: Ack without login via signed links

## Status

Accepted and implemented by the `POST /api/v1/ack/{token}` endpoint and the token it verifies.

## Context

An escalation climbs until someone acks it, and the state machine has modelled that ack from the
start: `Acked`, `AckedBy`, `AckedAt`, and an idempotent `Escalation.Ack` that stops the clock. What
was missing is the one thing that makes it usable — a way for the person being paged to trigger the
ack. Every on-call product this replaces answers that with an app and a login, which is exactly the
friction a page cannot afford at 3am: the responder has a link in a notification, not a browser
already signed in.

So the ack has to work from a link alone, with no account behind it. That turns three things into
decisions: what the link proves, how the link is spent, and who the ack is recorded as when nobody has
logged in to claim it.

## Decision

### The token is a stateless HMAC, and expiry is the only revocation

The link carries a signed token — `version ‖ escalationId ‖ expiry`, HMAC-SHA256 over those bytes,
each half base64url so the whole thing is one path segment. Verifying it is recomputing the HMAC with
the deployment's key and checking the deadline; there is no row to look up. Nothing is stored, so there
is nothing to revoke, and that is safe here where it usually would not be: `Ack` is idempotent and a
no-op once the escalation is `Acked`, `Resolved`, or `Exhausted`, so a token replayed inside its window
re-acks to the same state and one replayed after the incident closed does nothing. The failure a
stored-token scheme guards against — a captured link acking something it should not — collapses to
"acks an incident that is already acked or over," which is not a failure.

Expiry exists anyway, because a bearer credential with no lifetime is one leak away from permanent. The
default is generous — a week — because the link outlives the ack window: a responder may act on a page
well after the escalation stopped climbing, and a token that died with the timeout would refuse an ack
the product still wants to take.

The signing key is configuration with no default, and the app refuses to start without one of at least
32 bytes. A missing key is not a weak key to warn about; it is signing links anyone can forge, and the
right time to fail is boot, not the first forged ack.

### The link is spent with POST, not GET

A GET link is the obvious shape and the wrong one. Mail clients prefetch links and security scanners
follow them, so an ack link that acted on GET would be acked by a link preview before a human saw the
page — the incident silenced by a robot reading the mail. So the mutation is a POST. Until there is a
web UI, that means a responder acks with a one-line `curl` or the eventual page's button, and a GET a
scanner might hit does nothing because it is not a route. A one-click confirmation page that GETs a form
and POSTs the ack is a UI concern, deferred with the rest of the UI.

### The ack is recorded against a reserved actor, for now

`Ack` demands a non-empty actor, and there is no real one to give it yet: resolving the paged
`Schedule` target to a person needs `OnCallResolver`, which still has no production caller (ADR-002).
Rather than invent a fake user row or weaken the domain, the endpoint records a reserved `AckedViaLink`
id. The token is versioned precisely so this is not permanent: when on-call resolution lands, the link
is minted carrying the resolved person, the endpoint records them through a `v2` token with an actor
claim, and `AckedViaLink` retires. The link works today; whose finger it was waits for the increment
that can actually answer.

### Unauthenticated is the design, not a gap

The endpoint has no authentication because the signed token *is* the credential — the same posture
ADR-005 takes for ingestion, for the same reason: the URL carries the proof. This is orthogonal to the
`Integration`-token work ADR-005 foresees; that authenticates a *sender*, while this authorises a
*single act on a single escalation*, and a short-lived per-escalation HMAC is the tighter grant.

**Rejected.** *A stored, single-use token* — buys revocation and one-time semantics the idempotent
domain already makes moot, at the cost of a table, a write on every mint, and cleanup. *A JWT* — a
library and a header's worth of claims to carry two fields and one signature this endpoint reads
itself. *Acting on GET* — see above; it hands the ack to the first prefetcher.

## Consequences

**Plus**

- A page is actionable from the notification alone, with no account, which is the whole point.
- No storage, no cleanup, no revocation machinery: the signature and the clock are the entire
  mechanism, and `Ack`'s idempotency makes replay a non-event.
- The one concrete channel, `LogChannel`, already carries the link, so the loop is demonstrable end to
  end before any network channel exists.

**Minus**

- **The signing key is a rotation cliff.** Rotate it and every outstanding link stops verifying at
  once. Acceptable while links live a week and rotation is rare, but a real rotation story — accept the
  previous key during an overlap — is deferred until it is needed.
- **The ack is not yet attributable to a person.** `AckedBy` reads `AckedViaLink` for every
  link-driven ack until on-call resolution lands, so "who acked" is "someone with the link" and no
  finer. The column is real and set; only its value is a placeholder.
- **No rate limiting on verification.** A flood of bad tokens is cheap to reject — an HMAC and a
  compare — but it is unbounded work, and admission control here is deferred along with ingestion's
  (ADR-005).
- **Expiry is a fixed window, not the incident's.** A link's life is the configured default, so a very
  long incident could outlive its own ack link. Re-minting on later pages is the answer, and it arrives
  with the channels that would re-page.
