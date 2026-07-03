using Klaxon.Core.Entities;
using NodaTime;

namespace Klaxon.Core.OnCall;

// A single override slot, half-open over [StartsAt, EndsAt). It carries the resolved on-call user
// rather than an id so the resolver can return a User directly and stay free of any lookup.
public sealed record OverrideWindow
{
    public User User { get; }
    public Instant StartsAt { get; }
    public Instant EndsAt { get; }

    public OverrideWindow(User user, Instant startsAt, Instant endsAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (endsAt <= startsAt)
            throw new ArgumentException("Override end must be after its start.", nameof(endsAt));

        User = user;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }
}

// The finished projection an OnCallResolver call needs: the resolved rotation participants, the
// override windows, and everything required to compute handoff boundaries in the schedule's own
// timezone (ADR-002). It is built by the query layer from a Schedule plus its participants and
// overrides; the resolver itself performs no I/O.
public sealed record ScheduleSnapshot
{
    public RotationType Rotation { get; }
    public LocalTime HandoffTime { get; }
    public DateTimeZone Zone { get; }

    // The local date participant 0's first shift begins. Seeded from the schedule's creation
    // instant; kept on the snapshot so the resolver stays agnostic to its origin.
    public LocalDate RotationAnchor { get; }

    // Rotation order. May be empty — an empty rotation is a real "no one on call" state, not an error.
    public IReadOnlyList<User> Participants { get; }
    public IReadOnlyList<OverrideWindow> Overrides { get; }

    public ScheduleSnapshot(
        RotationType rotation,
        LocalTime handoffTime,
        DateTimeZone zone,
        LocalDate rotationAnchor,
        IReadOnlyList<User> participants,
        IReadOnlyList<OverrideWindow> overrides)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(overrides);

        Rotation = rotation;
        HandoffTime = handoffTime;
        Zone = zone;
        RotationAnchor = rotationAnchor;
        Participants = participants;
        Overrides = overrides;
    }
}
