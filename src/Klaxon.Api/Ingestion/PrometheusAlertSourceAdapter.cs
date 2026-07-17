using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klaxon.Api.Contracts;

namespace Klaxon.Api.Ingestion;

// Alertmanager's v4 webhook. It posts one notification per alert group, so the group is the
// incident: groupKey identifies it and the whole envelope is the payload. Keying on the group is
// also what keeps a group's second and later alerts from being dropped — nothing here selects among
// them, so nothing can discard them.
public sealed class PrometheusAlertSourceAdapter : IAlertSourceAdapter
{
    public IngestAlertRequest Parse(JsonElement body)
    {
        if (body.ValueKind is not JsonValueKind.Object)
            throw new JsonException("The request body must be a JSON object.");

        var groupKey = ReadString(body, "groupKey");

        // Guarded before hashing, not after: every string hashes to a valid-looking key, blank ones
        // included, so hashing first would sail past the Alert constructor's own guard and open an
        // incident keyed on nothing.
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);

        // The top-level status is resolved only once every alert in the group has cleared, which is
        // when the incident is over. A third value means this is not Alertmanager, and saying so is
        // better than opening an incident from a body nobody can read.
        var status = ReadString(body, "status") switch
        {
            "firing" => AlertIngestStatus.Firing,
            "resolved" => AlertIngestStatus.Resolved,
            var other => throw new JsonException($"Unknown Alertmanager status '{other}'."),
        };

        // groupKey grows with the group-by labels and has no bound, so it cannot be the dedup key as
        // it stands (Alert.MaxDedupKeyLength). Hashing is unconditional rather than a fallback for
        // long keys: one rule instead of two, and truncating would collapse two groups sharing a
        // prefix onto one incident and page for neither, since the discriminating labels come last.
        // The verbatim groupKey stays readable in the payload.
        var dedupKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey)));

        return new IngestAlertRequest(dedupKey, status, body);
    }

    private static string ReadString(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"Alertmanager payload is missing a string '{name}'.");
}
