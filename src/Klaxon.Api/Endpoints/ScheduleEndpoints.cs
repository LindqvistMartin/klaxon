using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klaxon.Api.Endpoints;

public static class ScheduleEndpoints
{
    public static RouteGroupBuilder MapScheduleEndpoints(this RouteGroupBuilder group)
    {
        var schedules = group.MapGroup("/schedules").WithTags("Schedules");

        schedules.MapPost("/", async (CreateScheduleRequest request, KlaxonDbContext db, CancellationToken ct) =>
        {
            var schedule = new Schedule(
                request.TeamId,
                request.Name,
                request.RotationType,
                request.HandoffTime,
                request.TimeZoneId,
                request.ParticipantOrder);

            db.Schedules.Add(schedule);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/schedules/{schedule.Id}", ScheduleResponse.FromEntity(schedule));
        })
        .WithName("CreateSchedule")
        .Produces<ScheduleResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        schedules.MapGet("/", async (KlaxonDbContext db, Guid? teamId, CancellationToken ct) =>
        {
            IQueryable<Schedule> query = db.Schedules.AsNoTracking();
            if (teamId is { } id)
                query = query.Where(schedule => schedule.TeamId == id);

            var result = await query.OrderBy(schedule => schedule.Name).ToListAsync(ct);
            return Results.Ok(result.Select(ScheduleResponse.FromEntity));
        })
        .WithName("ListSchedules")
        .Produces<IEnumerable<ScheduleResponse>>();

        schedules.MapGet("/{id:guid}", async (Guid id, KlaxonDbContext db, CancellationToken ct) =>
        {
            var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(schedule => schedule.Id == id, ct);
            return schedule is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(ScheduleResponse.FromEntity(schedule));
        })
        .WithName("GetSchedule")
        .Produces<ScheduleResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        schedules.MapDelete("/{id:guid}", async (Guid id, KlaxonDbContext db, CancellationToken ct) =>
        {
            var schedule = await db.Schedules.FirstOrDefaultAsync(schedule => schedule.Id == id, ct);
            if (schedule is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            db.Schedules.Remove(schedule); // overrides cascade
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("DeleteSchedule")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }
}
