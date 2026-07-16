using System.Text.Json;

namespace Klaxon.Api.Contracts;

public enum AlertIngestStatus { Firing, Resolved }

// Payload is a JsonElement so a caller can send its own JSON as JSON: a string would have to carry
// the whole payload escaped into a JSON literal, and a fixed record would drop every field it does
// not model. The raw text lands in the jsonb column.
public sealed record IngestAlertRequest(string DedupKey, AlertIngestStatus Status, JsonElement Payload);

public enum AlertIngestOutcome { Created, Deduplicated, Resolved, Ignored }

// Both ids are null for Ignored. EscalationId is also null when a resolved notification closes an
// alert that never had one — impossible today, since an alert and its escalation are created
// together (ADR-004).
public sealed record AlertIngestResponse(Guid? AlertId, Guid? EscalationId, AlertIngestOutcome Outcome);
