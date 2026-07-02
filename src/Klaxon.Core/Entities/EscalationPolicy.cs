using NodaTime;

namespace Klaxon.Core.Entities;

public sealed class EscalationPolicy
{
    private readonly List<EscalationLevel> _levels = [];

    public Guid Id { get; private set; }
    public Guid TeamId { get; private set; }
    public string Name { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    public Team Team { get; private set; } = default!;

    // Ordered by EscalationLevel.Position. Populated by EF; level mutation arrives with the
    // policy editor (a later milestone), not the escalation engine.
    public IReadOnlyList<EscalationLevel> Levels => _levels;

    private EscalationPolicy() { }

    public EscalationPolicy(Guid teamId, string name)
    {
        if (teamId == Guid.Empty)
            throw new ArgumentException("TeamId cannot be empty.", nameof(teamId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = Guid.NewGuid();
        TeamId = teamId;
        Name = name;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
