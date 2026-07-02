using NodaTime;

namespace Klaxon.Core.Entities;

public sealed class Team
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    public Organization Organization { get; private set; } = default!;
    public ICollection<User> Members { get; private set; } = [];
    public ICollection<Schedule> Schedules { get; private set; } = [];
    public ICollection<EscalationPolicy> Policies { get; private set; } = [];

    private Team() { }

    public Team(Guid organizationId, string name, string slug)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("OrganizationId cannot be empty.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Name = name;
        Slug = slug;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
