namespace Klaxon.Api.Endpoints;

// Reserved actor ids for acks with no real user attached yet. AckedViaLink stands in until the
// resolver increment can record the on-call person the signed link was sent to; the domain only
// requires a non-empty actor, so a fixed sentinel satisfies Ack without inventing a fake user row.
public static class KnownActors
{
    public static readonly Guid AckedViaLink = new("ac000000-0000-0000-0000-000000000000");
}
