using NodaTime;

namespace Klaxon.Core.Ack;

// The claims carried by an ack link: which escalation, and until when. There is deliberately no actor
// here yet — nothing resolves the on-call person until the resolver increment lands, so the endpoint
// records a reserved identity and this stays a two-field token until there is a real name to sign in.
public readonly record struct AckToken(Guid EscalationId, Instant ExpiresAt);
