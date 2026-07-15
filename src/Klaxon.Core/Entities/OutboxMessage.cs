using System.Text.Json;
using NodaTime;

namespace Klaxon.Core.Entities;

// A notification intent, written in the same transaction as the state change that caused it, so a
// page can never be lost between "decided" and "sent" (ADR-003). The NotificationDispatcher drains
// unprocessed rows and stamps ProcessedAt once the send returns.
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }

    // One of OutboxMessageTypes.
    public string Type { get; private set; } = default!;

    // Message body, persisted as jsonb.
    public string Payload { get; private set; } = default!;
    public Instant CreatedAt { get; private set; }

    // Null until a dispatcher tick has delivered the message. The claim scan filters on this, so a
    // processed row never reappears.
    public Instant? ProcessedAt { get; private set; }

    private OutboxMessage() { }

    public OutboxMessage(string type, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        // Payload lands in a jsonb column; reject malformed JSON here so it fails with a clear
        // domain error at construction rather than a Postgres 22P02 at SaveChanges.
        try
        {
            using var _ = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Payload must be well-formed JSON.", nameof(payload), ex);
        }
        Id = Guid.NewGuid();
        Type = type;
        Payload = payload;
        CreatedAt = SystemClock.Instance.GetCurrentInstant();
    }

    // Takes the instant rather than reading the clock so a dispatcher batch stamps every row with
    // the one clock read its tick started from, the way the engine measures its deadlines.
    public void MarkProcessed(Instant now) => ProcessedAt = now;
}
