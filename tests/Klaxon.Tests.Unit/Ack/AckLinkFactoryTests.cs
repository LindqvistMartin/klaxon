using Klaxon.Core.Ack;
using Klaxon.Infrastructure.Ack;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Klaxon.Tests.Unit.Ack;

public sealed class AckLinkFactoryTests
{
    [Fact]
    public void CreateLink_buildsTheAckUrlWithAVerifiableToken()
    {
        var tokens = new AckTokenService(Opts());
        var factory = new AckLinkFactory(tokens, Opts());
        var escalationId = Guid.NewGuid();

        var link = factory.CreateLink(escalationId);

        const string prefix = "https://klaxon.example/api/v1/ack/";
        link.Should().StartWith(prefix);
        tokens.Verify(link[prefix.Length..], out var verified).Should().Be(AckTokenStatus.Valid);
        verified.Should().Be(escalationId);
    }

    [Fact]
    public void CreateLink_doesNotDoubleTheSlashWhenTheBaseUrlEndsInOne()
    {
        var options = Opts("https://klaxon.example/");
        var factory = new AckLinkFactory(new AckTokenService(options), options);

        factory.CreateLink(Guid.NewGuid()).Should().StartWith("https://klaxon.example/api/v1/ack/");
    }

    [Fact]
    public void Mint_thenVerify_roundTripsTheEscalationId()
    {
        var tokens = new AckTokenService(Opts());
        var escalationId = Guid.NewGuid();

        tokens.Verify(tokens.Mint(escalationId), out var verified).Should().Be(AckTokenStatus.Valid);
        verified.Should().Be(escalationId);
    }

    private static IOptions<AckOptions> Opts(string baseUrl = "https://klaxon.example") =>
        Options.Create(new AckOptions { SigningKey = "unit-test-ack-signing-key-0123456789", LinkBaseUrl = baseUrl });
}
