using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using NodaTime;

namespace Klaxon.Core.Ack;

// The verdict of verifying a token, kept coarse on purpose. The endpoint folds Malformed and
// BadSignature onto one 401 so a caller cannot tell a corrupt token from a forged one, while Expired
// earns its own answer because a link that has merely aged out is worth saying so.
public enum AckTokenStatus { Valid, Malformed, BadSignature, Expired }

// Mints and verifies an ack link's token as a pure function of (claims, key, clock) — the same purity
// contract OnCallResolver keeps (ADR-007). Nothing is stored: the HMAC is the proof and expiry is the
// only revocation, which is safe because Ack is idempotent and a no-op once the escalation is
// terminal, so a replayed token can never do harm.
public static class AckTokenCodec
{
    private const byte Version = 1;
    private const int PayloadLength = 1 + 16 + 8; // version, escalation id, expiry (unix seconds)
    private const int SignatureLength = 32;       // HMACSHA256

    public static string Encode(AckToken token, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        token.EscalationId.TryWriteBytes(payload[1..17]);
        BinaryPrimitives.WriteInt64BigEndian(payload[17..], token.ExpiresAt.ToUnixTimeSeconds());

        Span<byte> signature = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, payload, signature);

        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(signature)}";
    }

    public static AckTokenStatus Verify(string token, byte[] key, Instant now, out AckToken decoded)
    {
        ArgumentNullException.ThrowIfNull(key);
        decoded = default;

        if (string.IsNullOrEmpty(token))
            return AckTokenStatus.Malformed;

        // Exactly one dot, with a non-empty half on each side.
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1 || token.IndexOf('.', dot + 1) >= 0)
            return AckTokenStatus.Malformed;

        byte[] payload, signature;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return AckTokenStatus.Malformed;
        }

        if (payload.Length != PayloadLength || payload[0] != Version || signature.Length != SignatureLength)
            return AckTokenStatus.Malformed;

        // Recompute over the received bytes and compare in constant time, so the check does not leak
        // where a forged signature first diverges.
        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, payload, expected);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            return AckTokenStatus.BadSignature;

        var expiresAt = Instant.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(17)));
        if (now >= expiresAt)
            return AckTokenStatus.Expired;

        decoded = new AckToken(new Guid(payload.AsSpan(1, 16)), expiresAt);
        return AckTokenStatus.Valid;
    }
}
