using System.Net;
using System.Net.Http.Json;
using System.Text;
using Klaxon.Api.Contracts;
using Klaxon.Api.Endpoints;
using Klaxon.Core.Ack;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Ack;
using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Integration.Endpoints;

// The ack path has no login, so these drive it over real HTTP with only a signed token: a valid link
// acks and stops the engine, and a forged, expired, or unknown one is refused. The engine is stripped
// from the hosted pipeline (ApiFactory), so "the engine stops paging" is a deterministic hand-tick
// rather than a race with the 1s loop.
[Collection("Api")]
public sealed class AckTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ack_ValidToken_AcksTheEscalationAndStopsTheClock()
    {
        var escalationId = await SeedDueEscalationAsync(await SeedPolicyAsync(60, 120));

        var response = await AckAsync(Mint(escalationId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<AckResponse>(TestJson.Options))!;
        body.State.Should().Be(EscalationState.Acked);

        var escalation = await LoadEscalationAsync(escalationId);
        escalation.State.Should().Be(EscalationState.Acked);
        escalation.AckedAt.Should().NotBeNull();
        escalation.AckedBy.Should().Be(KnownActors.AckedViaLink);
        // The clock is off: a stopped escalation carries no next deadline for the engine to scan.
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public async Task Ack_AfterAcking_TheEngineNeverPagesTheEscalationAgain()
    {
        var escalationId = await SeedDueEscalationAsync(await SeedPolicyAsync(60, 120));
        await AckAsync(Mint(escalationId));

        // Re-arm the timer in the past: the claim's State filter, not just the null deadline, is what
        // has to keep an acked escalation out of the scan, so force it due and prove the engine skips it.
        await MakeDueAsync(escalationId);
        (await Engine().ProcessDueOnceAsync(CancellationToken.None)).Should().Be(0);
        (await LoadOutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Ack_Twice_IsIdempotentAndKeepsTheFirstAck()
    {
        var escalationId = await SeedDueEscalationAsync(await SeedPolicyAsync(60));
        var token = Mint(escalationId);

        await AckAsync(token);
        var afterFirst = await LoadEscalationAsync(escalationId);

        var second = await AckAsync(token);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<AckResponse>(TestJson.Options))!.State.Should().Be(EscalationState.Acked);
        // The second click is a no-op: the persisted ack is exactly the first one's, not re-stamped.
        // Compared through the database both times so the check is at one precision, not the in-memory
        // instant against its microsecond-truncated reload.
        var afterSecond = await LoadEscalationAsync(escalationId);
        afterSecond.AckedAt.Should().Be(afterFirst.AckedAt);
        afterSecond.AckedBy.Should().Be(afterFirst.AckedBy);
    }

    [Fact]
    public async Task Ack_AnExpiredToken_Returns410()
    {
        var escalationId = await SeedDueEscalationAsync(await SeedPolicyAsync(60));
        // Sign through the codec with a past expiry under the app's own key, so only the deadline is
        // wrong — not the signature.
        var expired = AckTokenCodec.Encode(
            new AckToken(escalationId, SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(1)),
            Encoding.UTF8.GetBytes(SigningKey()));

        (await AckAsync(expired)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await LoadEscalationAsync(escalationId)).State.Should().Be(EscalationState.Triggered);
    }

    [Fact]
    public async Task Ack_ATamperedToken_Returns401()
    {
        var escalationId = await SeedDueEscalationAsync(await SeedPolicyAsync(60));
        var parts = Mint(escalationId).Split('.');
        var tampered = $"{parts[0]}.{(parts[1][0] == 'A' ? 'B' : 'A')}{parts[1][1..]}";

        (await AckAsync(tampered)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoadEscalationAsync(escalationId)).State.Should().Be(EscalationState.Triggered);
    }

    [Fact]
    public async Task Ack_AMalformedToken_Returns401()
    {
        (await AckAsync("not-a-real-token")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ack_AValidTokenForAnUnknownEscalation_Returns404()
    {
        // Signed correctly, but names an escalation that was never created.
        (await AckAsync(Mint(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> AckAsync(string token) =>
        factory.CreateClient().PostAsync($"/api/v1/ack/{token}", content: null);

    private string Mint(Guid escalationId) =>
        factory.Services.GetRequiredService<IAckTokenService>().Mint(escalationId);

    private string SigningKey() =>
        factory.Services.GetRequiredService<IOptions<AckOptions>>().Value.SigningKey;

    private EscalationEngine Engine() => factory.Services.GetRequiredService<EscalationEngine>();

    private async Task<Guid> SeedPolicyAsync(params int[] levelTimeoutsSeconds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var org = new Organization($"Ack Org {suffix}", $"ack-org-{suffix}");
        var team = new Team(org.Id, $"Ack Team {suffix}", $"ack-team-{suffix}");
        var policy = new EscalationPolicy(team.Id, "Ack policy");
        db.Organizations.Add(org);
        db.Teams.Add(team);
        db.EscalationPolicies.Add(policy);
        for (var position = 0; position < levelTimeoutsSeconds.Length; position++)
        {
            db.EscalationLevels.Add(new EscalationLevel(policy.Id, position, levelTimeoutsSeconds[position],
                [new EscalationTarget(EscalationTargetKind.User, $"user-{position}")]));
        }
        await db.SaveChangesAsync();
        return policy.Id;
    }

    private async Task<Guid> SeedDueEscalationAsync(Guid policyId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var alert = new Alert("prometheus", $"dedup-{Guid.NewGuid()}", "{}");
        var escalation = new Escalation(alert.Id, policyId,
            SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(1));
        db.Alerts.Add(alert);
        db.Escalations.Add(escalation);
        await db.SaveChangesAsync();
        return escalation.Id;
    }

    private async Task MakeDueAsync(Guid escalationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Escalations" SET "NextTimeoutAt" = now() - interval '1 minute' WHERE "Id" = {escalationId}
            """);
    }

    private async Task<Escalation> LoadEscalationAsync(Guid escalationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.Escalations.AsNoTracking().SingleAsync(e => e.Id == escalationId);
    }

    private async Task<List<OutboxMessage>> LoadOutboxAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.OutboxMessages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }
}
