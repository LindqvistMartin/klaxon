using Klaxon.Api.Contracts;
using Klaxon.Core.Ack;
using Klaxon.Infrastructure.Ack;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Klaxon.Api.Endpoints;

public static class AckEndpoints
{
    public static RouteGroupBuilder MapAckEndpoints(this RouteGroupBuilder group)
    {
        var ack = group.MapGroup("/ack").WithTags("Ack");

        // No authentication, on purpose: the signed token in the path is the credential, so a
        // responder acks straight from the link in their page without an account (ADR-007). POST and
        // not GET, because a GET link is fired by mail-client prefetch and link scanners, which would
        // ack the incident before a human read it.
        ack.MapPost("/{token}", async (
            string token,
            IAckTokenService tokens,
            KlaxonDbContext db,
            CancellationToken ct) =>
        {
            switch (tokens.Verify(token, out var escalationId))
            {
                // A forged token and a corrupt one collapse to one answer: telling them apart tells
                // an attacker which half of the token they got wrong.
                case AckTokenStatus.Malformed or AckTokenStatus.BadSignature:
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
                // A link that merely aged out says so, so a responder opens the app instead of
                // wondering why the click did nothing.
                case AckTokenStatus.Expired:
                    return Results.Problem(statusCode: StatusCodes.Status410Gone);
            }

            var escalation = await db.Escalations.FirstOrDefaultAsync(e => e.Id == escalationId, ct);
            if (escalation is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);

            // Ack is idempotent and a no-op once the escalation is terminal, so a second click or a
            // click after resolution returns the current state rather than failing. AckedViaLink is a
            // placeholder actor until the resolver increment names the person the link was sent to.
            escalation.Ack(KnownActors.AckedViaLink, SystemClock.Instance.GetCurrentInstant());
            await db.SaveChangesAsync(ct);

            return Results.Ok(new AckResponse(escalation.Id, escalation.State, escalation.AckedAt));
        })
        .WithName("AcknowledgeEscalation")
        .Produces<AckResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status410Gone);

        return group;
    }
}
