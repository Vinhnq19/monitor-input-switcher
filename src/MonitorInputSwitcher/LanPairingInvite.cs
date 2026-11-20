using System;
using System.Security.Cryptography;

namespace MonitorInputSwitcher;

/// <summary>
/// Single-use, five-minute pairing token. The code carries the certificate SHA-256 digest
/// followed by a random secret. Nothing here is logged or echoed in exceptions.
/// </summary>
public sealed class LanPairingInvite
{
    private const string Prefix = "MIS1-";
    private const int DigestLength = 32;
    private const int SecretLength = 16;
    private const int PayloadLength = DigestLength + SecretLength;
    private const int EncodedLength = 64;
    private const string Base64UrlAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly byte[] _digest;
    private readonly byte[] _secret;
    private readonly TimeProvider _time;
    private readonly long _issuedTimestamp;
    private bool _available = true;

    public LanPairingInvite(ReadOnlySpan<byte> certificateSha256, TimeProvider? timeProvider = null)
    {
        if (certificateSha256.Length != DigestLength)
        {
            throw new ArgumentException(
                "Certificate SHA-256 digest must be 32 bytes.",
                nameof(certificateSha256));
        }

        _digest = certificateSha256.ToArray();
        _secret = RandomNumberGenerator.GetBytes(SecretLength);
        _time = timeProvider ?? TimeProvider.System;
        _issuedTimestamp = _time.GetTimestamp();

        Span<byte> payload = stackalloc byte[PayloadLength];
        _digest.AsSpan().CopyTo(payload);
        _secret.AsSpan().CopyTo(payload.Slice(DigestLength));

        Span<char> encoded = stackalloc char[EncodedLength];
        EncodePayload(payload, encoded);
        Code = Prefix + new string(encoded);

        CryptographicOperations.ZeroMemory(payload);
    }

    public string Code { get; }

    /// <summary>
    /// Validates and consumes the invitation. <paramref name="candidate"/> is the decoded
    /// 48-byte payload produced by <see cref="TryParse"/> (digest followed by secret).
    /// Persistence runs while the invitation gate is held; only a successful persist consumes
    /// the invitation, and a failure propagates while the invitation stays usable until expiry.
    /// </summary>
    public bool TryRedeem(ReadOnlySpan<byte> candidate, Action persist)
    {
        ArgumentNullException.ThrowIfNull(persist);

        if (candidate.Length != PayloadLength)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_available)
            {
                return false;
            }

            if (HasExpiredLocked())
            {
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    candidate.Slice(0, DigestLength),
                    _digest) ||
                !CryptographicOperations.FixedTimeEquals(
                    candidate.Slice(DigestLength, SecretLength),
                    _secret))
            {
                return false;
            }

            // Mark unavailable before persisting so a re-entrant redeem cannot consume twice.
            _available = false;
            try
            {
                persist();
            }
            catch
            {
                _available = true;
                throw;
            }

            CryptographicOperations.ZeroMemory(_secret);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            CryptographicOperations.ZeroMemory(_secret);
            _available = false;
        }
    }

    public static bool TryParse(string? text, out byte[] certificateSha256, out byte[] secret)
    {
        certificateSha256 = Array.Empty<byte>();
        secret = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> encoded = trimmed.Slice(Prefix.Length);
        if (encoded.Length != EncodedLength)
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        if (!TryDecodePayload(encoded, payload))
        {
            return false;
        }

        Span<char> canonical = stackalloc char[EncodedLength];
        EncodePayload(payload, canonical);
        if (!canonical.SequenceEqual(encoded))
        {
            return false;
        }

        certificateSha256 = payload.Slice(0, DigestLength).ToArray();
        secret = payload.Slice(DigestLength, SecretLength).ToArray();
        CryptographicOperations.ZeroMemory(payload);
        return true;
    }

    private bool HasExpiredLocked()
    {
        if (_time.GetElapsedTime(_issuedTimestamp) < Lifetime)
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(_secret);
        _available = false;
        return true;
    }

    private static void EncodePayload(ReadOnlySpan<byte> payload, Span<char> destination)
    {
        var target = 0;
        for (var index = 0; index < payload.Length; index += 3)
        {
            var first = payload[index];
            var second = payload[index + 1];
            var third = payload[index + 2];

            destination[target++] = Base64UrlAlphabet[first >> 2];
            destination[target++] = Base64UrlAlphabet[((first & 0x03) << 4) | (second >> 4)];
            destination[target++] = Base64UrlAlphabet[((second & 0x0F) << 2) | (third >> 6)];
            destination[target++] = Base64UrlAlphabet[third & 0x3F];
        }
    }

    private static bool TryDecodePayload(ReadOnlySpan<char> encoded, Span<byte> payload)
    {
        if (encoded.Length != EncodedLength)
        {
            return false;
        }

        var target = 0;
        for (var index = 0; index < encoded.Length; index += 4)
        {
            var first = DecodeDigit(encoded[index]);
            var second = DecodeDigit(encoded[index + 1]);
            var third = DecodeDigit(encoded[index + 2]);
            var fourth = DecodeDigit(encoded[index + 3]);

            if (first < 0 || second < 0 || third < 0 || fourth < 0)
            {
                return false;
            }

            payload[target++] = (byte)((first << 2) | (second >> 4));
            payload[target++] = (byte)(((second & 0x0F) << 4) | (third >> 2));
            payload[target++] = (byte)(((third & 0x03) << 6) | fourth);
        }

        return true;
    }

    private static int DecodeDigit(char value) =>
        value switch
        {
            >= 'A' and <= 'Z' => value - 'A',
            >= 'a' and <= 'z' => value - 'a' + 26,
            >= '0' and <= '9' => value - '0' + 52,
            '-' => 62,
            '_' => 63,
            _ => -1
        };
}
