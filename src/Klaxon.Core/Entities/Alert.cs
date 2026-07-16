using System.Text.Json;
using NodaTime;

namespace Klaxon.Core.Entities;

public enum AlertStatus { Open, Resolved }

public sealed class Alert
{
    // AlertConfiguration sizes the columns from these, so a guard here and a column width there
    // cannot drift apart.
    public const int MaxSourceLength = 100;
    public const int MaxDedupKeyLength = 200;

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
        // Both arrive from caller input — the ingest URL and an untrusted webhook body — so an
        // oversized value is a bad request, not a bug. Guarding here makes it the same 400 as any
        // other malformed field instead of a Postgres 22001 at SaveChanges.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(source.Length, MaxSourceLength, nameof(source));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dedupKey.Length, MaxDedupKeyLength, nameof(dedupKey));
        // Payload lands in a jsonb column; reject malformed JSON here so it fails with a clear
        // domain error at construction rather than a Postgres 22P02 at SaveChanges.
        JsonValueKind payloadKind;
        try
        {
            using var document = JsonDocument.Parse(payload);
            payloadKind = document.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Payload must be well-formed JSON.", nameof(payload), ex);
        }

        // jsonb takes a bare scalar happily, so "null" and "42" are well-formed JSON that would page
        // someone with nothing to read. A payload describes one alert, which makes it an object.
        if (payloadKind is not JsonValueKind.Object)
            throw new ArgumentException("Payload must be a JSON object.", nameof(payload));
        Id = Guid.NewGuid();
        Source = source;
        DedupKey = dedupKey;
        Payload = payload;
        Status = AlertStatus.Open;
        ReceivedAt = SystemClock.Instance.GetCurrentInstant();
    }

    // Idempotent, like Escalation.Resolve: a monitoring source repeats its resolved notification,
    // and the second arrival must not rewrite when the incident ended. Leaving Open is what frees
    // the (Source, DedupKey) slot in IX_Alerts_OpenDedup, so the next firing of the same key opens
    // a fresh alert rather than deduplicating onto a finished incident (ADR-004). The caller is
    // responsible for resolving any live escalation in the same unit of work; the domain holds no
    // navigation from an alert to its escalation.
    public void Resolve(Instant now)
    {
        if (Status == AlertStatus.Resolved)
            return;

        Status = AlertStatus.Resolved;
        ResolvedAt = now;
    }
}
