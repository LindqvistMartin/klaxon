using NodaTime;

namespace Klaxon.Core.Entities;

public enum RotationType { Daily, Weekly }

public sealed class Schedule
{
    private readonly List<Guid> _participantOrder = [];

    public Guid Id { get; private set; }
    public Guid TeamId { get; private set; }
    public string Name { get; private set; } = default!;
    public RotationType RotationType { get; private set; }

    // Local wall-clock time of day the rotation hands off, interpreted in TimeZoneId (see ADR-002).
    public LocalTime HandoffTime { get; private set; }

    // IANA timezone id of the schedule — the single source of truth for rotation boundaries.
    // A participant's own timezone is display-only; boundaries are always computed here.
    public string TimeZoneId { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    public Team Team { get; private set; } = default!;
    public IReadOnlyList<Guid> ParticipantOrder => _participantOrder;
    public ICollection<ScheduleOverride> Overrides { get; private set; } = [];

    private Schedule() { }

    public Schedule(
        Guid teamId,
        string name,
        RotationType rotationType,
        LocalTime handoffTime,
        string timeZoneId,
        IReadOnlyList<Guid> participantOrder)
    {
        if (teamId == Guid.Empty)
            throw new ArgumentException("TeamId cannot be empty.", nameof(teamId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is null)
            throw new ArgumentException($"'{timeZoneId}' is not a valid IANA time zone id.", nameof(timeZoneId));
        ArgumentNullException.ThrowIfNull(participantOrder);
        if (participantOrder.Count == 0)
            throw new ArgumentException("A schedule needs at least one participant.", nameof(participantOrder));
        if (participantOrder.Any(id => id == Guid.Empty))
            throw new ArgumentException("Participant ids cannot be empty.", nameof(participantOrder));

        Id = Guid.NewGuid();
        TeamId = teamId;
        Name = name;
        RotationType = rotationType;
        HandoffTime = handoffTime;
        TimeZoneId = timeZoneId;
        _participantOrder.AddRange(participantOrder);
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
