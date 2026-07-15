using System.Text.Json;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Klaxon.Infrastructure.BackgroundServices;

// Durable, resumable escalation engine (ADR-001). Every escalation whose ack window has elapsed is
// claimed under FOR UPDATE SKIP LOCKED, advanced one level — or exhausted once the policy runs out
// of levels — and its new state committed to Postgres. All timer state lives in the Escalations
// table, so a restart resumes by re-running the same scan; there is no in-process timer to lose.
// The page itself is not sent from here: the same transaction writes an OutboxMessage that the
// NotificationDispatcher drains, so the state change and the notification intent land together
// (ADR-003).
public sealed class EscalationEngine(
    IServiceScopeFactory scopeFactory,
    ILogger<EscalationEngine> logger) : BackgroundService
{
    // The trigger is time elapsing, not an event, so a cheap indexed poll is the natural fit
    // (ADR-001). One second is invisible against minute-scale ack windows.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessDueOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "EscalationEngine encountered an error");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One tick: claim the due batch under FOR UPDATE SKIP LOCKED, advance each escalation from its
    // policy, and commit — all in one transaction. Exposed as internal so integration tests can
    // drive a single deterministic tick instead of racing the poll loop. Returns the number of
    // escalations advanced.
    internal async Task<int> ProcessDueOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();

        // EnableRetryOnFailure wraps DB work in a retrying execution strategy that rejects a
        // user-initiated transaction unless it is opened inside the strategy's own scope, so the
        // claim transaction runs through ExecuteAsync.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Start each attempt from a clean tracker. On a transient fault the strategy re-invokes
            // this delegate against the same context; without the reset, an escalation advanced in
            // memory on the failed attempt would stay tracked as Modified, EF identity resolution
            // would reuse it on the re-query, and Advance would run a second time — skipping a
            // paging level. Advance is a forward step, not idempotent, so the tracker must be reset.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // ADR-001's due scan. FOR UPDATE SKIP LOCKED holds each claimed row for the life of the
            // transaction, so a second tick — or a second instance mid-deploy — skips a locked row
            // rather than blocking or paging it twice; advancing NextTimeoutAt then drops the row
            // out of the next scan. The partial IX_Escalations_Due keeps the poll cheap. The lock
            // is the whole claim: it dies with the backend that held it, so a crashed worker's rows
            // are due again on the very next tick, with no lease to wait out (ADR-001).
            var due = await db.Escalations
                .FromSqlRaw("""
                    SELECT * FROM "Escalations"
                    WHERE "State" IN ('Triggered', 'Notified')
                      AND "NextTimeoutAt" <= now()
                    ORDER BY "NextTimeoutAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 100
                    """)
                .ToListAsync(ct);

            if (due.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            // One round-trip for all policies in the batch. The engine reads TimeoutSeconds to
            // compute the next ack deadline; Advance never reads the policy itself (ADR-004).
            var policyIds = due.Select(e => e.PolicyId).Distinct().ToList();
            var policies = await db.EscalationPolicies
                .Include(p => p.Levels)
                .Where(p => policyIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            // now() in the claim and this app clock are the same wall clock on the single-host
            // deployment (ADR-001); the next ack deadline is measured from here.
            var now = SystemClock.Instance.GetCurrentInstant();
            foreach (var escalation in due)
            {
                // Levels page in Position order; CurrentLevel is the ordinal into that ordered
                // sequence (Advance keeps it on the first Triggered -> Notified dispatch, bumps it
                // afterwards). Treating Position as a sort key rather than an index means a policy
                // with non-contiguous or 1-based positions still pages every level in order instead
                // of exhausting early. No next level -> null -> Exhausted; a policy with no levels
                // exhausts on its first advance.
                var levels = policies[escalation.PolicyId].Levels.OrderBy(l => l.Position).ToList();
                var nextOrdinal = escalation.State == EscalationState.Triggered
                    ? escalation.CurrentLevel
                    : escalation.CurrentLevel + 1;
                Instant? nextTimeoutAt = nextOrdinal < levels.Count
                    ? now + Duration.FromSeconds(levels[nextOrdinal].TimeoutSeconds)
                    : null;

                escalation.Advance(nextTimeoutAt);

                // The page rides in the same transaction as the state change, so the two land
                // together or not at all and a page is never lost between "decided" and "sent"
                // (ADR-003).
                if (nextTimeoutAt is null)
                {
                    // Exhaustion goes through the outbox like every other page rather than a direct
                    // call, which keeps it exactly as durable, and it is loud (ADR-004).
                    db.OutboxMessages.Add(new OutboxMessage(
                        OutboxMessageTypes.EscalationExhausted,
                        JsonSerializer.Serialize(new { EscalationId = escalation.Id })));
                    logger.LogError(
                        "Escalation {EscalationId} exhausted its policy with no ack", escalation.Id);
                }
                else
                {
                    db.OutboxMessages.Add(new OutboxMessage(
                        OutboxMessageTypes.EscalationLevelPaged,
                        JsonSerializer.Serialize(new { EscalationId = escalation.Id, Level = nextOrdinal })));
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return due.Count;
        });
    }
}
