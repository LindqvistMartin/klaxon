using NodaTime;

namespace Klaxon.Core.Entities;

public sealed class User
{
    public Guid Id { get; private set; }
    public Guid TeamId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Email { get; private set; } = default!;

    // IANA timezone id (e.g. "Europe/Berlin"), stored as text. Display-only: used to render
    // "on call until 09:00 their time", never to decide a handoff instant (see ADR-002).
    public string TimeZoneId { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    public Team Team { get; private set; } = default!;

    private User() { }

    public User(Guid teamId, string name, string email, string timeZoneId)
    {
        if (teamId == Guid.Empty)
            throw new ArgumentException("TeamId cannot be empty.", nameof(teamId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is null)
            throw new ArgumentException($"'{timeZoneId}' is not a valid IANA time zone id.", nameof(timeZoneId));
        Id = Guid.NewGuid();
        TeamId = teamId;
        Name = name;
        Email = email;
        TimeZoneId = timeZoneId;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
