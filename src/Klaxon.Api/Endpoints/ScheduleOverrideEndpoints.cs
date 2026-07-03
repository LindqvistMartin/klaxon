using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klaxon.Api.Endpoints;

public static class ScheduleOverrideEndpoints
{
    public static RouteGroupBuilder MapScheduleOverrideEndpoints(this RouteGroupBuilder group)
    {
        var overrides = group.MapGroup("/schedules/{scheduleId:guid}/overrides").WithTags("Schedule overrides");

        overrides.MapPost("/", async (Guid scheduleId, CreateScheduleOverrideRequest request, KlaxonDbContext db, CancellationToken ct) =>
        {
            if (!await db.Schedules.AnyAsync(schedule => schedule.Id == scheduleId, ct))
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            var scheduleOverride = new ScheduleOverride(scheduleId, request.UserId, request.StartsAt, request.EndsAt);
            db.ScheduleOverrides.Add(scheduleOverride);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/schedules/{scheduleId}/overrides/{scheduleOverride.Id}",
                ScheduleOverrideResponse.FromEntity(scheduleOverride));
        })
        .WithName("CreateScheduleOverride")
        .Produces<ScheduleOverrideResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        overrides.MapGet("/", async (Guid scheduleId, KlaxonDbContext db, CancellationToken ct) =>
        {
            if (!await db.Schedules.AnyAsync(schedule => schedule.Id == scheduleId, ct))
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            var result = await db.ScheduleOverrides.AsNoTracking()
                .Where(scheduleOverride => scheduleOverride.ScheduleId == scheduleId)
                .OrderBy(scheduleOverride => scheduleOverride.StartsAt)
                .ToListAsync(ct);

            return Results.Ok(result.Select(ScheduleOverrideResponse.FromEntity));
        })
        .WithName("ListScheduleOverrides")
        .Produces<IEnumerable<ScheduleOverrideResponse>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        overrides.MapDelete("/{overrideId:guid}", async (Guid scheduleId, Guid overrideId, KlaxonDbContext db, CancellationToken ct) =>
        {
            var scheduleOverride = await db.ScheduleOverrides
                .FirstOrDefaultAsync(o => o.Id == overrideId && o.ScheduleId == scheduleId, ct);
            if (scheduleOverride is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            db.ScheduleOverrides.Remove(scheduleOverride);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("DeleteScheduleOverride")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }
}
