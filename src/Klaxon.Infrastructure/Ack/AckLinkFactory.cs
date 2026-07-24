using Microsoft.Extensions.Options;

namespace Klaxon.Infrastructure.Ack;

// The base URL is configuration because only the deployment knows the name it is reached by; the path
// is fixed because it is this service's own ack endpoint. A trailing slash on the configured base is
// trimmed so the joined URL never doubles it.
public sealed class AckLinkFactory(IAckTokenService tokens, IOptions<AckOptions> options) : IAckLinkFactory
{
    private readonly string _baseUrl = options.Value.LinkBaseUrl.TrimEnd('/');

    public string CreateLink(Guid escalationId) => $"{_baseUrl}/api/v1/ack/{tokens.Mint(escalationId)}";
}
