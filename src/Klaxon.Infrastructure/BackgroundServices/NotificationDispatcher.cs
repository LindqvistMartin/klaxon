using Klaxon.Infrastructure.Notifications;
using Klaxon.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Klaxon.Infrastructure.BackgroundServices;

// Drains the outbox the escalation engine fills (ADR-003). Unprocessed rows are claimed under
// FOR UPDATE SKIP LOCKED, delivered to every registered channel, and stamped ProcessedAt — all in
// one transaction, so nothing is committed until delivery has returned. A failure anywhere rolls
// the stamp back and the next tick retries. That makes delivery at-least-once: a duplicate page is
// harmless because Ack is idempotent (ADR-004), whereas a row committed as sent whose delivery
// never happened is the silent non-page the product exists to prevent.
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    // Matches the engine's cadence; a second either side of a minute-scale ack window is invisible.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "NotificationDispatcher encountered an error");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One tick: claim the unprocessed batch, deliver it, stamp it, commit. Exposed as internal so
    // integration tests can drive a single deterministic tick instead of racing the poll loop.
    // Returns the number of messages delivered.
    internal async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();

        // EnableRetryOnFailure rejects a user-initiated transaction opened outside the strategy's
        // own scope, so the claim transaction runs through ExecuteAsync.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retried attempt re-reads the batch rather than reusing rows this context already
            // stamped in memory on the attempt that failed.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // SKIP LOCKED lets a second dispatcher — or a second instance mid-deploy — take the
            // next batch instead of blocking on this one, and the partial index keeps the scan off
            // delivered history.
            var pending = await db.OutboxMessages
                .FromSqlRaw("""
                    SELECT * FROM "OutboxMessages"
                    WHERE "ProcessedAt" IS NULL
                    ORDER BY "CreatedAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 50
                    """)
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            // One clock read stamps the whole batch, the way the engine measures one tick's
            // deadlines from a single read.
            var now = SystemClock.Instance.GetCurrentInstant();
            var delivered = 0;
            foreach (var message in pending)
            {
                try
                {
                    // Sending inside the claim transaction holds the row lock, and the connection,
                    // for the length of the send. That is free while channels are local; the first
                    // channel that makes a network call needs the claim and the stamp split into
                    // separate transactions.
                    foreach (var channel in channels)
                        await channel.SendAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave this row unstamped so the next tick retries it, and let the rest of the
                    // batch commit. Failing the whole transaction instead would re-send every
                    // message ahead of this one on every tick, and never reach the ones behind it.
                    logger.LogWarning(ex, "Delivery failed for outbox message {MessageId}", message.Id);
                    continue;
                }

                message.MarkProcessed(now);
                delivered++;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return delivered;
        });
    }
}
