using System.Text;
using Klaxon.Core.Ack;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Klaxon.Infrastructure.Ack;

// Wraps the pure AckTokenCodec with the configured key and the system clock, so the endpoint and the
// link factory share one place that knows how a token is signed and how long it lives. The key is
// read once: it is a singleton, and the options are fixed for the process lifetime.
public sealed class AckTokenService(IOptions<AckOptions> options) : IAckTokenService
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.SigningKey);
    private readonly Duration _lifetime = Duration.FromTimeSpan(options.Value.LinkLifetime);

    public string Mint(Guid escalationId)
    {
        var expiresAt = SystemClock.Instance.GetCurrentInstant() + _lifetime;
        return AckTokenCodec.Encode(new AckToken(escalationId, expiresAt), _key);
    }

    public AckTokenStatus Verify(string token, out Guid escalationId)
    {
        var status = AckTokenCodec.Verify(token, _key, SystemClock.Instance.GetCurrentInstant(), out var decoded);
        escalationId = decoded.EscalationId;
        return status;
    }
}
