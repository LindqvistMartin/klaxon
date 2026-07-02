using NodaTime;

namespace Klaxon.Core.Entities;

public enum AlertStatus { Open, Resolved }

public sealed class Alert
{
    public Guid Id { get; private set; }
    public string Source { get; private set; } = default!;

    // Repeated arrivals of the same alert (same Source + DedupKey) collapse onto one open
    // escalation rather than starting a new page storm (see ADR-004, flap suppression).
    public string DedupKey { get; private set; } = default!;

    // Raw alert payload, persisted as jsonb.
    public string Payload { get; private set; } = default!;
    public AlertStatus Status { get; private set; }
    public Instant ReceivedAt { get; private set; }
    public Instant? ResolvedAt { get; private set; }

    private Alert() { }

    public Alert(string source, string dedupKey, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        Id = Guid.NewGuid();
        Source = source;
        DedupKey = dedupKey;
        Payload = payload;
        Status = AlertStatus.Open;
        ReceivedAt = SystemClock.Instance.GetCurrentInstant();
    }
}
