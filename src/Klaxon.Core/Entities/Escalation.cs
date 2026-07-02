using NodaTime;

namespace Klaxon.Core.Entities;

public enum EscalationState { Triggered, Notified, Acked, Resolved, Exhausted }

public sealed class Escalation
{
    public Guid Id { get; private set; }
    public Guid AlertId { get; private set; }
    public Guid PolicyId { get; private set; }
    public EscalationState State { get; private set; }

    // Zero-based index of the level currently being paged.
    public int CurrentLevel { get; private set; }

    // When the current level's ack window expires and the engine should advance. Null once the
    // escalation reaches a terminal state. This column drives the durable due-scan (see ADR-001).
    public Instant? NextTimeoutAt { get; private set; }

    // Lease held by the engine tick currently processing this row (FOR UPDATE SKIP LOCKED + lease),
    // so concurrent ticks and the boot scan never page the same escalation twice.
    public Instant? LeaseUntil { get; private set; }

    public Guid? AckedBy { get; private set; }
    public Instant? AckedAt { get; private set; }
    public Instant? ResolvedAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    public Alert Alert { get; private set; } = default!;
    public EscalationPolicy Policy { get; private set; } = default!;

    private Escalation() { }

    public Escalation(Guid alertId, Guid policyId, Instant firstTimeoutAt)
    {
        if (alertId == Guid.Empty)
            throw new ArgumentException("AlertId cannot be empty.", nameof(alertId));
        if (policyId == Guid.Empty)
            throw new ArgumentException("PolicyId cannot be empty.", nameof(policyId));

        Id = Guid.NewGuid();
        AlertId = alertId;
        PolicyId = policyId;
        State = EscalationState.Triggered;
        CurrentLevel = 0;
        NextTimeoutAt = firstTimeoutAt;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
