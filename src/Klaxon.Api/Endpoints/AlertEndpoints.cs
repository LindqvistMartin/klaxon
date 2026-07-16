using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Klaxon.Api.Endpoints;

public static class AlertEndpoints
{
    public static RouteGroupBuilder MapAlertEndpoints(this RouteGroupBuilder group)
    {
        var alerts = group.MapGroup("/alerts").WithTags("Alerts");

        // The policy is named by the URL rather than the body, because a URL is the one thing every
        // monitoring system can be configured with (ADR-005). The 202 is returned only after the
        // commit: a source treats 2xx as delivered and will not send the alert again, so acking
        // before the write would drop it on a crash.
        alerts.MapPost("/ingest/{source}/{policyId:guid}", async (
            string source,
            Guid policyId,
            IngestAlertRequest request,
            KlaxonDbContext db,
            CancellationToken ct) =>
        {
            // A 409 here would say "conflict" about a URL that is simply wrong.
            if (!await db.EscalationPolicies.AnyAsync(policy => policy.Id == policyId, ct))
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            var now = SystemClock.Instance.GetCurrentInstant();

            var open = await db.Alerts.FirstOrDefaultAsync(
                alert => alert.Source == source
                    && alert.DedupKey == request.DedupKey
                    && alert.Status == AlertStatus.Open,
                ct);

            if (request.Status == AlertIngestStatus.Resolved)
            {
                // Sources repeat their resolved notifications, so one for a key with nothing open is
                // a no-op rather than an error: a 404 would make Alertmanager retry a settled
                // incident forever.
                if (open is null)
                    return Results.Accepted(null, new AlertIngestResponse(null, null, AlertIngestOutcome.Ignored));

                // Resolving the alert alone would leave its escalation armed, and the engine would
                // page the next level for an incident that is over and then log an ERROR that nobody
                // acked it. Resolve is a no-op on an escalation that already finished, so this needs
                // no state filter.
                var escalation = await db.Escalations.FirstOrDefaultAsync(e => e.AlertId == open.Id, ct);

                open.Resolve(now);
                escalation?.Resolve(now);
                await db.SaveChangesAsync(ct);

                return Results.Accepted(null, new AlertIngestResponse(open.Id, escalation?.Id, AlertIngestOutcome.Resolved));
            }

            if (open is not null)
            {
                // Flap suppression (ADR-004). Nothing is written, so the stored payload stays the one
                // that opened the incident.
                var escalation = await db.Escalations.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.AlertId == open.Id, ct);
                return Results.Accepted(null, new AlertIngestResponse(open.Id, escalation?.Id, AlertIngestOutcome.Deduplicated));
            }

            var newAlert = new Alert(source, request.DedupKey, request.Payload.GetRawText());

            // Due now, so the engine claims it on the next tick and pages level 0 — a Triggered
            // escalation advances to its own CurrentLevel rather than past it. Paging from here
            // instead would page level 0 twice, since only Advance both moves Triggered to Notified
            // and writes the outbox row carrying the page.
            var newEscalation = new Escalation(newAlert.Id, policyId, now);

            // One SaveChanges: an alert that landed without its escalation would hold the dedup key
            // forever and page nobody.
            db.Alerts.Add(newAlert);
            db.Escalations.Add(newEscalation);
            await db.SaveChangesAsync(ct);

            return Results.Accepted(null, new AlertIngestResponse(newAlert.Id, newEscalation.Id, AlertIngestOutcome.Created));
        })
        .WithName("IngestAlert")
        .Produces<AlertIngestResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }
}
