using Klaxon.Core.Entities;
using NodaTime;

namespace Klaxon.Api.Contracts;

public sealed record CreateScheduleOverrideRequest(Guid UserId, Instant StartsAt, Instant EndsAt);

public sealed record ScheduleOverrideResponse(
    Guid Id,
    Guid ScheduleId,
    Guid UserId,
    Instant StartsAt,
    Instant EndsAt)
{
    public static ScheduleOverrideResponse FromEntity(ScheduleOverride scheduleOverride) => new(
        scheduleOverride.Id,
        scheduleOverride.ScheduleId,
        scheduleOverride.UserId,
        scheduleOverride.StartsAt,
        scheduleOverride.EndsAt);
}
