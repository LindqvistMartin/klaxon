using NodaTime;

namespace Klaxon.Core.Entities;

public sealed class Organization
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    public ICollection<Team> Teams { get; private set; } = [];

    private Organization() { }

    public Organization(string name, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        Id = Guid.NewGuid();
        Name = name;
        Slug = slug;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
