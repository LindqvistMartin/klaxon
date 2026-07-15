using Klaxon.Core.Entities;
using Klaxon.Infrastructure.Notifications;

namespace Klaxon.Tests.Integration.Infrastructure;

// Stands in for LogChannel across the whole test host so delivery is observable and can be failed
// on demand. Registered once against the shared "Api" fixture rather than per test: a second
// WebApplicationFactory<Program> building alongside it crashes on Serilog's frozen global logger.
internal sealed class RecordingChannel : INotificationChannel
{
    private readonly Lock _gate = new();
    private readonly List<SentMessage> _sent = [];

    // Set by a test to make sends fail. Volatile because the dispatcher reads it on whichever
    // thread its tick resumes on.
    public volatile bool ThrowOnSend;

    // Ids whose send should fail while ThrowOnSend is off, so a test can poison one row in a batch.
    public IReadOnlySet<Guid> PoisonIds { get; set; } = new HashSet<Guid>();

    public IReadOnlyList<SentMessage> Sent
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    public void Reset()
    {
        lock (_gate) _sent.Clear();
        ThrowOnSend = false;
        PoisonIds = new HashSet<Guid>();
    }

    public Task SendAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (ThrowOnSend || PoisonIds.Contains(message.Id))
            throw new InvalidOperationException("Channel is down.");

        // Snapshot rather than keep the entity: the caller's copy goes on to be stamped in memory,
        // and a rolled-back attempt would otherwise leave a ProcessedAt here that never committed.
        lock (_gate)
            _sent.Add(new SentMessage(message.Id, message.Type, message.Payload));
        return Task.CompletedTask;
    }
}

internal sealed record SentMessage(Guid Id, string Type, string Payload);
