using System.Text.Json;
using Klaxon.Core.Entities;
using Klaxon.Infrastructure.BackgroundServices;
using Klaxon.Infrastructure.Notifications;
using Klaxon.Infrastructure.Persistence;
using Klaxon.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Klaxon.Tests.Integration.BackgroundServices;

// The dispatcher is stripped from the hosted pipeline and registered as a singleton in test config,
// so every test drives ProcessOnceAsync by hand — deterministic, no racing the 1s poll loop. Shares
// the one "Api" container; CleanAsync empties the outbox and Reset the recording channel.
[Collection("Api")]
public sealed class NotificationDispatcherTests(ApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.CleanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tick_UnprocessedRow_SendsToChannelAndMarksProcessed()
    {
        var id = await SeedOutboxAsync(OutboxMessageTypes.EscalationLevelPaged);

        var delivered = await Dispatcher().ProcessOnceAsync(CancellationToken.None);

        delivered.Should().Be(1);
        Channel().Sent.Should().ContainSingle();
        Channel().Sent[0].Id.Should().Be(id);
        Channel().Sent[0].Type.Should().Be(OutboxMessageTypes.EscalationLevelPaged);
        (await LoadAsync(id)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Tick_ProcessedRow_IsNotSentAgain()
    {
        await SeedOutboxAsync(OutboxMessageTypes.EscalationLevelPaged);

        await Dispatcher().ProcessOnceAsync(CancellationToken.None);
        var second = await Dispatcher().ProcessOnceAsync(CancellationToken.None);

        second.Should().Be(0);
        Channel().Sent.Should().ContainSingle("a stamped row drops out of the claim scan for good");
    }

    [Fact]
    public async Task Tick_ChannelThrows_LeavesRowUnprocessed()
    {
        var id = await SeedOutboxAsync(OutboxMessageTypes.EscalationExhausted);
        Channel().ThrowOnSend = true;

        var delivered = await Dispatcher().ProcessOnceAsync(CancellationToken.None);

        // Delivery is at-least-once (ADR-003): a row is stamped only once its send has returned, so
        // a failed send leaves it for the next tick. Committing the stamp and delivering afterwards
        // — affordable for a dashboard blip, not for a page — would lose it here.
        delivered.Should().Be(0);
        (await LoadAsync(id)).ProcessedAt.Should().BeNull();

        Channel().ThrowOnSend = false;
        await Dispatcher().ProcessOnceAsync(CancellationToken.None);
        (await LoadAsync(id)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Tick_OneFailingMessage_DoesNotStrandTheRest()
    {
        var first = await SeedOutboxAsync(OutboxMessageTypes.EscalationLevelPaged);
        var poison = await SeedOutboxAsync(OutboxMessageTypes.EscalationExhausted);
        var last = await SeedOutboxAsync(OutboxMessageTypes.EscalationLevelPaged);
        Channel().PoisonIds = new HashSet<Guid> { poison };

        var delivered = await Dispatcher().ProcessOnceAsync(CancellationToken.None);

        // One unreachable target must not take the batch down with it. Rolling back instead would
        // re-send everything ahead of the failure on every tick, and never reach what is behind it.
        delivered.Should().Be(2);
        (await LoadAsync(first)).ProcessedAt.Should().NotBeNull();
        (await LoadAsync(last)).ProcessedAt.Should().NotBeNull("a failure ahead of it must not stall the queue");
        (await LoadAsync(poison)).ProcessedAt.Should().BeNull();

        // The delivered rows are done, so the retry re-sends only the failure — no page storm.
        Channel().PoisonIds = new HashSet<Guid>();
        await Dispatcher().ProcessOnceAsync(CancellationToken.None);
        Channel().Sent.Count(m => m.Id == first).Should().Be(1);
        (await LoadAsync(poison)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentTicks_SendExactlyOnce()
    {
        await SeedOutboxAsync(OutboxMessageTypes.EscalationLevelPaged);

        // Two dispatchers claim the same row at once. FOR UPDATE SKIP LOCKED means one takes it and
        // the other skips rather than paging a second time.
        var delivered = await Task.WhenAll(
            Dispatcher().ProcessOnceAsync(CancellationToken.None),
            FreshDispatcher().ProcessOnceAsync(CancellationToken.None));

        delivered.Sum().Should().Be(1);
        Channel().Sent.Should().ContainSingle();
    }

    private NotificationDispatcher Dispatcher() =>
        factory.Services.GetRequiredService<NotificationDispatcher>();

    private NotificationDispatcher FreshDispatcher() => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        factory.Services.GetServices<INotificationChannel>(),
        NullLogger<NotificationDispatcher>.Instance);

    private RecordingChannel Channel() => factory.Services.GetRequiredService<RecordingChannel>();

    private async Task<Guid> SeedOutboxAsync(string type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        var message = new OutboxMessage(type, JsonSerializer.Serialize(new { EscalationId = Guid.NewGuid() }));
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private async Task<OutboxMessage> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KlaxonDbContext>();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }
}
