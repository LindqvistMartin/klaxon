using System.Text.Json;
using Klaxon.Api.Contracts;

namespace Klaxon.Api.Ingestion;

// Turns one monitoring system's webhook body into the shape the ingest endpoint has always taken,
// selected by the {format} segment of the ingest URL (ADR-006). Synchronous and tokenless: a parse
// is a pure function of a body already in memory, so there is nothing to await and nothing to
// cancel.
public interface IAlertSourceAdapter
{
    IngestAlertRequest Parse(JsonElement body);
}
