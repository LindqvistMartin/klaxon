using Klaxon.Core.Entities;
using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Integration.BackgroundServices;

// The engine is stripped from the hosted pipeline and registered as a singleton in test config, so
// every test drives ProcessDueOnceAsync by hand — deterministic, no racing the 1s poll loop.
// Shares the one "Api" container; CleanAsync gives each test an empty database.
[Collection("Api")]
public sealed class EscalationEngineTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tick_AdvancesTriggeredToNotified()
    {
        var policyId = await SeedPolicyAsync(60, 60);
        var escalationId = await SeedDueEscalationAsync(policyId);

        var advanced = await Engine().ProcessDueOnceAsync(CancellationToken.None);

        advanced.Should().Be(1);
        var escalation = await LoadEscalationAsync(escalationId);
        // First dispatch pages level 0 and keeps CurrentLevel there; only later ticks bump it.
        escalation.State.Should().Be(EscalationState.Notified);
        escalation.CurrentLevel.Should().Be(0);
        escalation.NextTimeoutAt.Should().NotBeNull();
        escalation.NextTimeoutAt!.Value.Should().BeGreaterThan(SystemClock.Instance.GetCurrentInstant());
    }

    [Fact]
    public async Task Tick_ExhaustsWhenLevelsRunOut()
    {
        var policyId = await SeedPolicyAsync(1);
        var escalationId = await SeedDueEscalationAsync(policyId);

        await Engine().ProcessDueOnceAsync(CancellationToken.None); // pages the only level
        await MakeDueAsync(escalationId);
        await Engine().ProcessDueOnceAsync(CancellationToken.None); // no next level -> exhaust

        var escalation = await LoadEscalationAsync(escalationId);
        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public async Task Tick_ExhaustsZeroLevelPolicyOnFirstAdvance()
    {
        var policyId = await SeedPolicyAsync(); // zero levels
        var escalationId = await SeedDueEscalationAsync(policyId);

        var advanced = await Engine().ProcessDueOnceAsync(CancellationToken.None);

        advanced.Should().Be(1);
        var escalation = await LoadEscalationAsync(escalationId);
        // Triggered -> Exhausted: a policy with nobody to page exhausts loudly rather than hanging.
        escalation.State.Should().Be(EscalationState.Exhausted);
        escalation.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public async Task Tick_PagesEveryLevelWhenPositionsAreNotContiguous()
    {
        // Positions 1 and 2 (1-based, no level 0). The engine must page both in order, not exhaust
        // immediately because it can't find Position 0.
        var policyId = await SeedPolicyWithPositionsAsync((1, 1), (2, 1));
        var escalationId = await SeedDueEscalationAsync(policyId);

        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        (await LoadEscalationAsync(escalationId)).State.Should().Be(EscalationState.Notified);

        await MakeDueAsync(escalationId);
        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        (await LoadEscalationAsync(escalationId)).CurrentLevel.Should().Be(1);

        await MakeDueAsync(escalationId);
        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        (await LoadEscalationAsync(escalationId)).State.Should().Be(EscalationState.Exhausted);
    }

    [Fact]
    public async Task Tick_SkipsNotYetDueEscalation()
    {
        var policyId = await SeedPolicyAsync(60);
        var future = SystemClock.Instance.GetCurrentInstant() + Duration.FromMinutes(5);
        var escalationId = await SeedEscalationAsync(policyId, future);

        var advanced = await Engine().ProcessDueOnceAsync(CancellationToken.None);

        advanced.Should().Be(0);
        var escalation = await LoadEscalationAsync(escalationId);
        escalation.State.Should().Be(EscalationState.Triggered);
        escalation.CurrentLevel.Should().Be(0);
    }

    [Fact]
    public async Task Tick_SkipsTerminalEscalation()
    {
        var policyId = await SeedPolicyAsync(60);
        var escalationId = await SeedDueEscalationAsync(policyId);
        // Force a terminal-but-due row (Resolve/Ack normally null out NextTimeoutAt, so this can't
        // arise naturally) to prove the claim's State filter, not just the timeout, guards it.
        await ForceResolvedButDueAsync(escalationId);

        var advanced = await Engine().ProcessDueOnceAsync(CancellationToken.None);

        advanced.Should().Be(0);
        (await LoadEscalationAsync(escalationId)).State.Should().Be(EscalationState.Resolved);
    }

    [Fact]
    public async Task Tick_AdvancesEveryDueEscalationAcrossPolicies()
    {
        var fastPolicy = await SeedPolicyAsync(30);
        var slowPolicy = await SeedPolicyAsync(600);
        var fast1 = await SeedDueEscalationAsync(fastPolicy);
        var fast2 = await SeedDueEscalationAsync(fastPolicy);
        var slow = await SeedDueEscalationAsync(slowPolicy);

        var advanced = await Engine().ProcessDueOnceAsync(CancellationToken.None);

        advanced.Should().Be(3);
        var slowEscalation = await LoadEscalationAsync(slow);
        var fastEscalation = await LoadEscalationAsync(fast1);
        (await LoadEscalationAsync(fast2)).State.Should().Be(EscalationState.Notified);
        // Each escalation took its own policy's timeout, proving the batch maps rows to policies
        // correctly: the slow policy's next deadline is further out than the fast one's.
        slowEscalation.State.Should().Be(EscalationState.Notified);
        fastEscalation.State.Should().Be(EscalationState.Notified);
        slowEscalation.NextTimeoutAt!.Value.Should().BeGreaterThan(fastEscalation.NextTimeoutAt!.Value);
    }

    [Fact]
    public async Task Restart_ResumesFromPostgresState()
    {
        var policyId = await SeedPolicyAsync(1, 1);
        var escalationId = await SeedDueEscalationAsync(policyId);

        // First tick pages level 0.
        await Engine().ProcessDueOnceAsync(CancellationToken.None);
        (await LoadEscalationAsync(escalationId)).CurrentLevel.Should().Be(0);

        // Simulate a restart: a brand-new engine that holds no in-process state. It resumes purely
        // from the row in Postgres — ADR-001's promise that resumption is an ordinary query, not a
        // recovery routine.
        await MakeDueAsync(escalationId);
        var restarted = FreshEngine();

        await restarted.ProcessDueOnceAsync(CancellationToken.None);
        var afterLevel2 = await LoadEscalationAsync(escalationId);
        afterLevel2.State.Should().Be(EscalationState.Notified);
        afterLevel2.CurrentLevel.Should().Be(1);

        // One more tick past the last level exhausts it.
        await MakeDueAsync(escalationId);
        await restarted.ProcessDueOnceAsync(CancellationToken.None);
        var exhausted = await LoadEscalationAsync(escalationId);
        exhausted.State.Should().Be(EscalationState.Exhausted);
        exhausted.NextTimeoutAt.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentTicks_AdvanceExactlyOnce()
    {
        var policyId = await SeedPolicyAsync(60, 60);
        var escalationId = await SeedDueEscalationAsync(policyId);

        // Two engines tick the same due row at once. FOR UPDATE SKIP LOCKED means one claims it and
        // the other skips the locked row rather than paging it a second time.
        var advanced = await Task.WhenAll(
            Engine().ProcessDueOnceAsync(CancellationToken.None),
            FreshEngine().ProcessDueOnceAsync(CancellationToken.None));

        advanced.Sum().Should().Be(1);
        var escalation = await LoadEscalationAsync(escalationId);
        escalation.State.Should().Be(EscalationState.Notified);
        escalation.CurrentLevel.Should().Be(0); // advanced once, not twice
    }

    private EscalationEngine Engine() => factory.Services.GetRequiredService<EscalationEngine>();

    private EscalationEngine FreshEngine() => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<EscalationEngine>.Instance);

    private Task<Guid> SeedPolicyAsync(params int[] levelTimeoutsSeconds) =>
        SeedPolicyWithPositionsAsync(levelTimeoutsSeconds.Select((timeout, index) => (index, timeout)).ToArray());

    private async Task<Guid> SeedPolicyWithPositionsAsync(params (int Position, int TimeoutSeconds)[] levels)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        // Unique slug per policy so a test can seed several (Organization.Slug is globally unique).
        var suffix = Guid.NewGuid().ToString("N");
        var org = new Organization($"Engine Org {suffix}", $"engine-org-{suffix}");
        var team = new Team(org.Id, $"Engine Team {suffix}", $"engine-team-{suffix}");
        var policy = new EscalationPolicy(team.Id, "Engine policy");
        db.Organizations.Add(org);
        db.Teams.Add(team);
        db.EscalationPolicies.Add(policy);
        foreach (var (position, timeout) in levels)
        {
            db.EscalationLevels.Add(new EscalationLevel(policy.Id, position, timeout,
                [new EscalationTarget(EscalationTargetKind.User, $"user-{position}")]));
        }
        await db.SaveChangesAsync();
        return policy.Id;
    }

    private Task<Guid> SeedDueEscalationAsync(Guid policyId) =>
        SeedEscalationAsync(policyId, SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(1));

    private async Task<Guid> SeedEscalationAsync(Guid policyId, Instant firstTimeoutAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var alert = new Alert("prometheus", $"dedup-{Guid.NewGuid()}", "{}");
        var escalation = new Escalation(alert.Id, policyId, firstTimeoutAt);
        db.Alerts.Add(alert);
        db.Escalations.Add(escalation);
        await db.SaveChangesAsync();
        return escalation.Id;
    }

    private async Task MakeDueAsync(Guid escalationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        // Re-arm the timer in the past so the next tick re-claims it without waiting out the real
        // ack window.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Escalations" SET "NextTimeoutAt" = now() - interval '1 minute' WHERE "Id" = {escalationId}
            """);
    }

    private async Task ForceResolvedButDueAsync(Guid escalationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Escalations" SET "State" = 'Resolved', "NextTimeoutAt" = now() - interval '1 minute'
            WHERE "Id" = {escalationId}
            """);
    }

    private async Task<Escalation> LoadEscalationAsync(Guid escalationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.Escalations.AsNoTracking().SingleAsync(e => e.Id == escalationId);
    }
}
