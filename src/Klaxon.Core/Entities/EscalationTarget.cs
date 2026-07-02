namespace Klaxon.Core.Entities;

public enum EscalationTargetKind { Schedule, User, Channel }

// A single target of an escalation level, stored as jsonb inside EscalationLevel.Targets.
// Reference is the schedule id, user id, or channel name, interpreted per Kind. A record for
// value semantics — two targets with the same kind and reference are equal.
public sealed record EscalationTarget
{
    public EscalationTargetKind Kind { get; }
    public string Reference { get; }

    public EscalationTarget(EscalationTargetKind kind, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Kind = kind;
        Reference = reference;
    }
}
