using Klaxon.Core.Entities;
using NodaTime;

namespace Klaxon.Api.Contracts;

public sealed record CreateScheduleRequest(
    Guid TeamId,
    string Name,
    RotationType RotationType,
    LocalTime HandoffTime,
    string TimeZoneId,
    IReadOnlyList<Guid> ParticipantOrder);

public sealed record ScheduleResponse(
    Guid Id,
    Guid TeamId,
    string Name,
    RotationType RotationType,
    LocalTime HandoffTime,
    string TimeZoneId,
    IReadOnlyList<Guid> ParticipantOrder,
    Instant CreatedAt)
{
    public static ScheduleResponse FromEntity(Schedule schedule) => new(
        schedule.Id,
        schedule.TeamId,
        schedule.Name,
        schedule.RotationType,
        schedule.HandoffTime,
        schedule.TimeZoneId,
        schedule.ParticipantOrder,
        schedule.CreatedAt);
}
