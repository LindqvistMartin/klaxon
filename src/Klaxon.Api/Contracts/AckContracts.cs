using Klaxon.Core.Entities;
using NodaTime;

namespace Klaxon.Api.Contracts;

// The escalation's state after the ack. Idempotent by design: acking an already-acked or terminal
// escalation returns its current state with a 200 rather than an error, so a responder who clicks the
// link twice — or after the incident resolved — sees the truth instead of a failure.
public sealed record AckResponse(Guid EscalationId, EscalationState State, Instant? AckedAt);
