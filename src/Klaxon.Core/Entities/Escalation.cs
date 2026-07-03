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

    // Called by the engine once the current level has been paged. The engine computes the next
    // ack deadline from the policy and passes it in; a null deadline means there is no further
    // level, so the escalation exhausts. Advance never reads Policy/Levels itself, which keeps the
    // domain a pure, database-free state machine (ADR-004).
    public void Advance(Instant? nextTimeoutAt)
    {
        if (State is not (EscalationState.Triggered or EscalationState.Notified))
            throw new InvalidOperationException($"Cannot advance an escalation in state {State}.");

        if (nextTimeoutAt is null)
        {
            Transition(EscalationState.Exhausted);
            NextTimeoutAt = null;
            return;
        }

        if (State == EscalationState.Triggered)
            Transition(EscalationState.Notified); // first dispatch; level 0 stays current
        else
            CurrentLevel += 1; // next level armed; still waiting for an ack, so state is unchanged

        NextTimeoutAt = nextTimeoutAt;
    }

    // Idempotent: the first ack on an open escalation stops the clock; a repeated ack, or a late
    // ack that arrives after the escalation already resolved or exhausted, is a no-op. This is what
    // makes at-least-once notification (ADR-003) safe.
    public void Ack(Guid actor, Instant now)
    {
        if (actor == Guid.Empty)
            throw new ArgumentException("Actor cannot be empty.", nameof(actor));

        if (State is EscalationState.Acked or EscalationState.Resolved or EscalationState.Exhausted)
            return;

        Transition(EscalationState.Acked);
        AckedBy = actor;
        AckedAt = now;
        NextTimeoutAt = null;
    }

    // Idempotent. Resolving an already-resolved or exhausted escalation is a no-op; Exhausted stays
    // terminal so the "paged everyone, nobody acked" history is preserved.
    public void Resolve(Instant now)
    {
        if (State is EscalationState.Resolved or EscalationState.Exhausted)
            return;

        Transition(EscalationState.Resolved);
        ResolvedAt = now;
        NextTimeoutAt = null;
    }

    private void Transition(EscalationState target)
    {
        if (!EscalationStateMachine.IsAllowed(State, target))
            throw new InvalidOperationException($"Illegal escalation transition {State} -> {target}.");
        State = target;
    }
}
