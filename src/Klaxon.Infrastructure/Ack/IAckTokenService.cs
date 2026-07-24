using Klaxon.Core.Ack;

namespace Klaxon.Infrastructure.Ack;

// The application-side view of the codec: the configured key and the clock are already applied, so a
// caller mints for an escalation and verifies a string without handling key material.
public interface IAckTokenService
{
    string Mint(Guid escalationId);

    AckTokenStatus Verify(string token, out Guid escalationId);
}
