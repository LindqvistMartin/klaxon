using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klaxon.Api.Endpoints;

public static class EscalationPolicyEndpoints
{
    public static RouteGroupBuilder MapEscalationPolicyEndpoints(this RouteGroupBuilder group)
    {
        var policies = group.MapGroup("/escalation-policies").WithTags("Escalation policies");

        policies.MapPost("/", async (CreateEscalationPolicyRequest request, KlaxonDbContext db, CancellationToken ct) =>
        {
            var policy = new EscalationPolicy(request.TeamId, request.Name);
            db.EscalationPolicies.Add(policy);

            // Levels have their own DbSet and FK, so they are added alongside the policy and
            // committed in one SaveChanges (a single transaction). The guarded EscalationTarget /
            // EscalationLevel constructors validate the request; a bad level throws before commit.
            foreach (var levelDto in request.Levels)
            {
                var targets = levelDto.Targets
                    .Select(target => new EscalationTarget(target.Kind, target.Reference))
                    .ToList();
                db.EscalationLevels.Add(new EscalationLevel(policy.Id, levelDto.Position, levelDto.TimeoutSeconds, targets));
            }

            await db.SaveChangesAsync(ct);

            var created = await LoadPolicy(db, policy.Id, ct);
            return Results.Created($"/api/v1/escalation-policies/{policy.Id}", EscalationPolicyResponse.FromEntity(created!));
        })
        .WithName("CreateEscalationPolicy")
        .Produces<EscalationPolicyResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        policies.MapGet("/", async (KlaxonDbContext db, Guid? teamId, CancellationToken ct) =>
        {
            IQueryable<EscalationPolicy> query = db.EscalationPolicies.AsNoTracking().Include(policy => policy.Levels);
            if (teamId is { } id)
                query = query.Where(policy => policy.TeamId == id);

            var result = await query.OrderBy(policy => policy.Name).ToListAsync(ct);
            return Results.Ok(result.Select(EscalationPolicyResponse.FromEntity));
        })
        .WithName("ListEscalationPolicies")
        .Produces<IEnumerable<EscalationPolicyResponse>>();

        policies.MapGet("/{id:guid}", async (Guid id, KlaxonDbContext db, CancellationToken ct) =>
        {
            var policy = await LoadPolicy(db, id, ct);
            return policy is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(EscalationPolicyResponse.FromEntity(policy));
        })
        .WithName("GetEscalationPolicy")
        .Produces<EscalationPolicyResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        policies.MapDelete("/{id:guid}", async (Guid id, KlaxonDbContext db, CancellationToken ct) =>
        {
            var policy = await db.EscalationPolicies.FirstOrDefaultAsync(policy => policy.Id == id, ct);
            if (policy is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            db.EscalationPolicies.Remove(policy); // levels cascade; a policy with live escalations is FK-restricted -> 409
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("DeleteEscalationPolicy")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static Task<EscalationPolicy?> LoadPolicy(KlaxonDbContext db, Guid id, CancellationToken ct) =>
        db.EscalationPolicies.AsNoTracking().Include(policy => policy.Levels).FirstOrDefaultAsync(policy => policy.Id == id, ct);
}
