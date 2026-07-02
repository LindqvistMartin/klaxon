namespace Klaxon.Core.Entities;

public sealed class EscalationLevel
{
    private readonly List<EscalationTarget> _targets = [];

    public Guid Id { get; private set; }
    public Guid PolicyId { get; private set; }

    // Zero-based position within the policy's ordered levels.
    public int Position { get; private set; }

    // Seconds to wait for an ack at this level before the engine advances to the next.
    public int TimeoutSeconds { get; private set; }

    public EscalationPolicy Policy { get; private set; } = default!;
    public IReadOnlyList<EscalationTarget> Targets => _targets;

    private EscalationLevel() { }

    public EscalationLevel(Guid policyId, int position, int timeoutSeconds, IReadOnlyList<EscalationTarget> targets)
    {
        if (policyId == Guid.Empty)
            throw new ArgumentException("PolicyId cannot be empty.", nameof(policyId));
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("An escalation level needs at least one target.", nameof(targets));

        Id = Guid.NewGuid();
        PolicyId = policyId;
        Position = position;
        TimeoutSeconds = timeoutSeconds;
        _targets.AddRange(targets);
    }
}
