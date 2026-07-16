using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Klaxon.Api.Contracts;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Klaxon.Tests.Integration.Endpoints;

// Ingestion is the product's front door, so these drive it over real HTTP and then hand-tick the
// engine and dispatcher: a page is followed from the POST all the way to a channel. Both pollers are
// stripped from the hosted pipeline in the test host, so the ticks are deterministic rather than
// racing a 1s loop.
[Collection("Api")]
public sealed class AlertIngestTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Key = "disk-full:web-1";

    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ingest_FiringAlert_PagesLevelZeroThroughOutboxToChannel()
    {
        var policyId = await SeedPolicyAsync(60, 120);

        var ingested = await IngestAsync(policyId, Firing(Key));

        ingested.Outcome.Should().Be(AlertIngestOutcome.Created);
        ingested.AlertId.Should().NotBeNull();
        ingested.EscalationId.Should().NotBeNull();

        // Ingestion arms the escalation due now, so the next tick claims it and pages level 0 —
        // a Triggered escalation advances to its own CurrentLevel rather than past it.
        (await Engine().ProcessDueOnceAsync(CancellationToken.None)).Should().Be(1);
        (await Dispatcher().ProcessOnceAsync(CancellationToken.None)).Should().Be(1);

        Channel().Sent.Should().ContainSingle();
        Channel().Sent[0].Type.Should().Be(OutboxMessageTypes.EscalationLevelPaged);
        using var paged = JsonDocument.Parse(Channel().Sent[0].Payload);
        paged.RootElement.GetProperty("EscalationId").GetGuid().Should().Be(ingested.EscalationId!.Value);
        paged.RootElement.GetProperty("Level").GetInt32().Should().Be(0);

        // The body the source sent is what a responder reads, so it has to survive the round trip
        // into jsonb rather than being dropped on the way.
        using var stored = JsonDocument.Parse((await LoadAlertAsync(ingested.AlertId!.Value)).Payload);
        stored.RootElement.GetProperty("severity").GetString().Should().Be("critical");
    }

    [Fact]
    public async Task Ingest_SameKeyFromDifferentSources_AreSeparateIncidents()
    {
        var policyId = await SeedPolicyAsync(60);
        var fromNagios = await IngestAsync(policyId, Firing(Key));

        var fromPrometheus = await IngestAsync(policyId, Firing(Key), source: "prometheus");

        // Dedup identity is (Source, DedupKey), not the key alone. Two monitoring systems that
        // happen to name the same condition the same way describe two incidents, and collapsing
        // them would page for the first and stay silent for the second.
        fromPrometheus.Outcome.Should().Be(AlertIngestOutcome.Created);
        fromPrometheus.AlertId.Should().NotBe(fromNagios.AlertId!.Value);
        (await CountAsync<Alert>()).Should().Be(2);
        (await Engine().ProcessDueOnceAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Ingest_SameKeyTwiceWhileOpen_DedupsOntoOneEscalation()
    {
        var policyId = await SeedPolicyAsync(60);
        var first = await IngestAsync(policyId, Firing(Key));

        var second = await IngestAsync(policyId, Firing(Key));

        // Flap suppression (ADR-004): the second firing rides the escalation already running.
        second.Outcome.Should().Be(AlertIngestOutcome.Deduplicated);
        second.AlertId.Should().Be(first.AlertId);
        second.EscalationId.Should().Be(first.EscalationId);
        (await CountAsync<Alert>()).Should().Be(1);
        (await CountAsync<Escalation>()).Should().Be(1);

        // The point of the dedup: one page, not two.
        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        await Dispatcher().ProcessOnceAsync(CancellationToken.None);
        Channel().Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Ingest_ResolvedThenFiringSameKey_CreatesFreshAlertAndPagesAgain()
    {
        var policyId = await SeedPolicyAsync(60);
        var first = await IngestAsync(policyId, Firing(Key));
        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        await IngestAsync(policyId, Resolved(Key));

        var second = await IngestAsync(policyId, Firing(Key));

        // The same key fires again after its incident closed. IX_Alerts_OpenDedup constrains only
        // open rows, so this opens a fresh alert rather than deduplicating onto the finished one.
        // While nothing could move an alert off Open, the filter could never be false and this
        // firing would have deduplicated onto a week-old row and paged nobody, silently.
        second.Outcome.Should().Be(AlertIngestOutcome.Created);
        second.AlertId.Should().NotBe(first.AlertId!.Value);
        second.EscalationId.Should().NotBe(first.EscalationId!.Value);

        (await Engine().ProcessDueOnceAsync(CancellationToken.None)).Should().Be(1);
        await Dispatcher().ProcessOnceAsync(CancellationToken.None);
        Channel().Sent.Select(EscalationIdOf).Should()
            .Equal(first.EscalationId!.Value, second.EscalationId!.Value);
    }

    [Fact]
    public async Task Ingest_ResolvedAlert_ResolvesLiveEscalationSoEngineStopsPaging()
    {
        var policyId = await SeedPolicyAsync(60, 120);
        var created = await IngestAsync(policyId, Firing(Key));
        await Engine().ProcessDueOnceAsync(CancellationToken.None);

        var resolved = await IngestAsync(policyId, Resolved(Key));

        resolved.Outcome.Should().Be(AlertIngestOutcome.Resolved);
        resolved.EscalationId.Should().Be(created.EscalationId);
        var escalation = await LoadEscalationAsync(created.EscalationId!.Value);
        escalation.State.Should().Be(EscalationState.Resolved);
        escalation.NextTimeoutAt.Should().BeNull();

        // Re-arm the timer in the past. Resolving the alert without cascading to its escalation
        // would leave this claimable, and the engine would page level 1 for a closed incident and
        // then log an ERROR that nobody acked it.
        await MakeDueAsync(created.EscalationId!.Value);
        (await Engine().ProcessDueOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Ingest_ResolvedUnknownKey_Returns202Ignored()
    {
        var policyId = await SeedPolicyAsync(60);

        var response = await IngestAsync(policyId, Resolved("never-fired"));

        // Sources repeat their resolved notifications; a 404 here would make Alertmanager retry a
        // settled incident forever.
        response.Outcome.Should().Be(AlertIngestOutcome.Ignored);
        response.AlertId.Should().BeNull();
        response.EscalationId.Should().BeNull();
        (await CountAsync<Alert>()).Should().Be(0);
    }

    [Fact]
    public async Task Ingest_UnknownPolicy_Returns404()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync(Url("nagios", Guid.NewGuid()), Firing(Key), TestJson.Options);

        // The policy id names a resource in the path, so an unknown one is a 404 like every other
        // missing path resource here — and never a 409, which a source treats as permanent.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountAsync<Alert>()).Should().Be(0);
    }

    [Fact]
    public async Task Ingest_MissingPayload_Returns400()
    {
        var policyId = await SeedPolicyAsync(60);

        var response = await factory.CreateClient().PostAsJsonAsync(Url("nagios", policyId),
            new { dedupKey = Key, status = "Firing" }, TestJson.Options);

        // Payload is a required, non-nullable record parameter, so omitting it throws during
        // binding — the framework's 400, distinct from the domain guard below.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        problem!.Title.Should().Be("The request body is invalid.");
    }

    [Fact]
    public async Task Ingest_BlankDedupKey_Returns400()
    {
        var policyId = await SeedPolicyAsync(60);

        var response = await factory.CreateClient()
            .PostAsJsonAsync(Url("nagios", policyId), Firing("  "), TestJson.Options);

        // A blank key trips the Alert constructor — the domain-is-the-validation-layer path.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        problem!.Title.Should().Be("One or more validation errors occurred.");
    }

    [Fact]
    public async Task OpenDedupIndex_RejectsSecondOpenAlertForSameKey()
    {
        await InsertAlertAsync();

        var act = () => InsertAlertAsync();

        // The unique half of IX_Alerts_OpenDedup. Ingestion looks the open row up before inserting,
        // but the index is what makes "at most one open per key" a rule instead of a hope — two
        // ingests racing each other both miss the lookup, and this is what stops the second.
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.And.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task OpenDedupIndex_AllowsNewOpenAlertAfterPreviousResolved()
    {
        var first = await InsertAlertAsync();
        await ResolveAlertAsync(first);

        var act = () => InsertAlertAsync();

        // The partial half, and the reason the filter carries its weight: a resolved alert leaves
        // the index, so the same key can open a new incident later. A plain unique index would
        // reject this and the key would never page again.
        await act.Should().NotThrowAsync();
    }

    private static string Url(string source, Guid policyId) => $"/api/v1/alerts/ingest/{source}/{policyId}";

    private static IngestAlertRequest Firing(string dedupKey) => new(
        dedupKey, AlertIngestStatus.Firing,
        JsonSerializer.SerializeToElement(new { severity = "critical", instance = "web-1" }));

    private static IngestAlertRequest Resolved(string dedupKey) => new(
        dedupKey, AlertIngestStatus.Resolved,
        JsonSerializer.SerializeToElement(new { severity = "critical", instance = "web-1" }));

    private static Guid EscalationIdOf(SentMessage message)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        return payload.RootElement.GetProperty("EscalationId").GetGuid();
    }

    private async Task<AlertIngestResponse> IngestAsync(Guid policyId, IngestAlertRequest request, string source = "nagios")
    {
        var response = await factory.CreateClient().PostAsJsonAsync(Url(source, policyId), request, TestJson.Options);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<AlertIngestResponse>(TestJson.Options))!;
    }

    private async Task<Alert> LoadAlertAsync(Guid alertId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alertId);
    }

    private EscalationEngine Engine() => factory.Services.GetRequiredService<EscalationEngine>();

    private NotificationDispatcher Dispatcher() => factory.Services.GetRequiredService<NotificationDispatcher>();

    private RecordingChannel Channel() => factory.Services.GetRequiredService<RecordingChannel>();

    private async Task<Guid> SeedPolicyAsync(params int[] levelTimeoutsSeconds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        // Unique slug per policy so a test can seed several (Organization.Slug is globally unique).
        var suffix = Guid.NewGuid().ToString("N");
        var org = new Organization($"Ingest Org {suffix}", $"ingest-org-{suffix}");
        var team = new Team(org.Id, $"Ingest Team {suffix}", $"ingest-team-{suffix}");
        var policy = new EscalationPolicy(team.Id, "Ingest policy");
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

    private async Task<Guid> InsertAlertAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var alert = new Alert("nagios", Key, "{}");
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert.Id;
    }

    private async Task ResolveAlertAsync(Guid alertId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.Id == alertId);
        // Through the domain, not raw SQL: a hand-written UPDATE would pin a state the product
        // cannot actually produce.
        alert.Resolve(NodaTime.SystemClock.Instance.GetCurrentInstant());
        await db.SaveChangesAsync();
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

    private async Task<int> CountAsync<TEntity>() where TEntity : class
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.Set<TEntity>().CountAsync();
    }
}
