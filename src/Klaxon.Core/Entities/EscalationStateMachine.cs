using System.Collections.Frozen;

namespace Klaxon.Core.Entities;

// The single source of truth for legal escalation state changes (ADR-004). Keeping the matrix
// here, next to the entity that enforces it, means the rules sit in one auditable place rather
// than a switch buried in a controller or the engine.
public static class EscalationStateMachine
{
    // Only real state changes are listed. The Notified -> Notified level bump is not a state
    // change, so it is deliberately absent (Advance handles it without consulting the matrix).
    // Triggered -> Exhausted is included on purpose, so a policy with no levels exhausts loudly on
    // the first Advance instead of throwing.
    public static readonly FrozenSet<(EscalationState From, EscalationState To)> AllowedTransitions =
        new HashSet<(EscalationState, EscalationState)>
        {
            (EscalationState.Triggered, EscalationState.Notified),
            (EscalationState.Triggered, EscalationState.Acked),
            (EscalationState.Triggered, EscalationState.Resolved),
            (EscalationState.Triggered, EscalationState.Exhausted),
            (EscalationState.Notified, EscalationState.Acked),
            (EscalationState.Notified, EscalationState.Resolved),
            (EscalationState.Notified, EscalationState.Exhausted),
            (EscalationState.Acked, EscalationState.Resolved),
        }.ToFrozenSet();

    public static bool IsAllowed(EscalationState from, EscalationState to) =>
        AllowedTransitions.Contains((from, to));
}
