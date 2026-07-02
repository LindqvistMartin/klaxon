using NodaTime;

namespace Klaxon.Core.Entities;

// Named ScheduleOverride rather than Override because `override` is a C# keyword. Represents a
// one-off substitution that takes precedence over the computed rotation (see ADR-002).
public sealed class ScheduleOverride
{
    public Guid Id { get; private set; }
    public Guid ScheduleId { get; private set; }
    public Guid UserId { get; private set; }
    public Instant StartsAt { get; private set; }
    public Instant EndsAt { get; private set; }

    public Schedule Schedule { get; private set; } = default!;

    private ScheduleOverride() { }

    public ScheduleOverride(Guid scheduleId, Guid userId, Instant startsAt, Instant endsAt)
    {
        if (scheduleId == Guid.Empty)
            throw new ArgumentException("ScheduleId cannot be empty.", nameof(scheduleId));
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId cannot be empty.", nameof(userId));
        if (endsAt <= startsAt)
            throw new ArgumentException("Override end must be after its start.", nameof(endsAt));

        Id = Guid.NewGuid();
        ScheduleId = scheduleId;
        UserId = userId;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }
}
