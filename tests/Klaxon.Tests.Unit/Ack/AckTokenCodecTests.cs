using System.Buffers.Text;
using System.Text;
using Klaxon.Core.Ack;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Klaxon.Tests.Unit.Ack;

public sealed class AckTokenCodecTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-ack-signing-key-0123456789");
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("a-different-unit-test-key-9876543210");
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 24, 12, 0, 0);

    [Fact]
    public void Encode_thenVerify_roundTripsTheClaims()
    {
        var escalationId = Guid.NewGuid();
        var expiresAt = Now + Duration.FromHours(1);

        var status = AckTokenCodec.Verify(
            AckTokenCodec.Encode(new AckToken(escalationId, expiresAt), Key), Key, Now, out var decoded);

        status.Should().Be(AckTokenStatus.Valid);
        decoded.EscalationId.Should().Be(escalationId);
        decoded.ExpiresAt.Should().Be(expiresAt);
    }

    [Fact]
    public void Verify_atTheExactExpiryInstant_isExpired()
    {
        // Expiry is exclusive: the link is dead at its stated instant, not a second past it.
        var expiresAt = Now + Duration.FromHours(1);
        var token = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), expiresAt), Key);

        AckTokenCodec.Verify(token, Key, expiresAt, out _).Should().Be(AckTokenStatus.Expired);
    }

    [Fact]
    public void Verify_anExpiredToken_isExpired()
    {
        var token = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now - Duration.FromSeconds(1)), Key);

        AckTokenCodec.Verify(token, Key, Now, out _).Should().Be(AckTokenStatus.Expired);
    }

    [Fact]
    public void Verify_withTheWrongKey_isBadSignature()
    {
        var token = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key);

        AckTokenCodec.Verify(token, OtherKey, Now, out _).Should().Be(AckTokenStatus.BadSignature);
    }

    [Fact]
    public void Verify_aTamperedSignature_isBadSignature()
    {
        var parts = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key).Split('.');

        AckTokenCodec.Verify($"{parts[0]}.{Flip(parts[1])}", Key, Now, out _).Should().Be(AckTokenStatus.BadSignature);
    }

    [Fact]
    public void Verify_claimsCarriedUnderAnotherTokensSignature_isBadSignature()
    {
        // Valid shapes, but the HMAC covers the payload: one token's claims under another's signature
        // do not verify.
        var mine = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key);
        var theirs = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key);
        var forged = $"{theirs.Split('.')[0]}.{mine.Split('.')[1]}";

        AckTokenCodec.Verify(forged, Key, Now, out _).Should().Be(AckTokenStatus.BadSignature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData(".onlyRight")]
    [InlineData("onlyLeft.")]
    [InlineData("too.many.dots")]
    [InlineData("not*base64url.also*not")]
    public void Verify_malformedInput_isMalformed(string token)
    {
        AckTokenCodec.Verify(token, Key, Now, out _).Should().Be(AckTokenStatus.Malformed);
    }

    [Fact]
    public void Verify_anUnknownVersionByte_isMalformed()
    {
        // A future version is not a signature failure — it is a token this codec does not speak, so it
        // is rejected at the shape check before the HMAC.
        var parts = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key).Split('.');
        var payload = Base64Url.DecodeFromChars(parts[0]);
        payload[0] = 2;

        AckTokenCodec.Verify($"{Base64Url.EncodeToString(payload)}.{parts[1]}", Key, Now, out _)
            .Should().Be(AckTokenStatus.Malformed);
    }

    [Fact]
    public void Encode_producesAUrlSafeToken()
    {
        var token = AckTokenCodec.Encode(new AckToken(Guid.NewGuid(), Now + Duration.FromHours(1)), Key);

        // base64url on each side of a dot: safe in a path segment, so the link needs no escaping.
        token.Should().MatchRegex("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$");
    }

    // Flips the first character of a base64url segment to a different one, keeping it decodable while
    // changing the first byte underneath — the top bits, which are never the ignored trailing ones.
    private static string Flip(string segment) =>
        (segment[0] == 'A' ? 'B' : 'A') + segment[1..];
}
