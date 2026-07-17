using System.Text.Json;
using Klaxon.Api.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Klaxon.Api.Ingestion;

// The contract Klaxon defines for callers written against it, where the body already is the normal
// form. Deserializing with the application's own options rather than options of its own is what
// keeps RespectRequiredConstructorParameters live, so a body missing a member gets the same 400 it
// got when the endpoint bound this record directly.
public sealed class GenericAlertSourceAdapter(IOptions<JsonOptions> jsonOptions) : IAlertSourceAdapter
{
    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;

    public IngestAlertRequest Parse(JsonElement body) =>
        // A JSON null deserializes to null rather than throwing, and would reach the endpoint as a
        // 500 for a body that is merely wrong.
        body.Deserialize<IngestAlertRequest>(_json)
        ?? throw new JsonException("The request body must be a JSON object.");
}
