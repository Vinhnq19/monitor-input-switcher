using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

/// <summary>
/// One-shot invitation pairing. The PC is the TLS client and proves the receiver pin from
/// the code; the laptop is the TLS server and redeems the invitation only after reading the
/// first framed Pair message.
/// </summary>
public static class LanPairingChannel
{
    private const int PinLength = 32;
    private const int PairPayloadLength = PinLength + 16;

    public static async Task PairAsPcAsync(
        Stream transport,
        X509Certificate2 local,
        string code,
        LanTrustStore trust,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(code);

        if (!LanPairingInvite.TryParse(
                code,
                out byte[] certificateSha256,
                out byte[] secret))
        {
            DisposeQuietly(transport);
            throw new ArgumentException("The pairing code is invalid.", nameof(code));
        }

        try
        {
            LanPeerTrust? existing = trust.Load();
            if (existing is not null && !existing.Pending)
            {
                throw new InvalidOperationException("A paired peer already exists.");
            }

            SslStream ssl = await LanSecureTransport.ConnectAsync(
                    transport,
                    local,
                    certificateSha256,
                    cancellationToken)
                .ConfigureAwait(false);

            using (ssl)
            {
                trust.Save(certificateSha256, pending: true);

                var wire = new LanWire(ssl);
                byte[] pairPayload = new byte[PairPayloadLength];
                try
                {
                    certificateSha256.AsSpan().CopyTo(pairPayload);
                    secret.AsSpan().CopyTo(pairPayload.AsSpan(PinLength));

                    await wire.WriteAsync(
                            LanFrameType.Pair,
                            pairPayload,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pairPayload);
                }

                LanFrame? reply = await wire
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (reply is null ||
                    reply.Value.Type != LanFrameType.PairAccepted ||
                    reply.Value.Payload.Length != 0)
                {
                    throw new InvalidOperationException("The pairing reply is invalid.");
                }

                trust.Save(certificateSha256, pending: false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateSha256);
            CryptographicOperations.ZeroMemory(secret);
            DisposeQuietly(transport);
        }
    }

    public static async Task PairAsLaptopAsync(
        Stream transport,
        X509Certificate2 local,
        LanPairingInvite invite,
        LanTrustStore trust,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(trust);

        try
        {
            if (trust.Load() is not null)
            {
                throw new InvalidOperationException("A paired peer already exists.");
            }

            (SslStream ssl, byte[] clientPin) = await LanSecureTransport.AcceptAsync(
                    transport,
                    local,
                    peerPin: null,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                using (ssl)
                {
                    var wire = new LanWire(ssl);
                    LanFrame? first = await wire
                        .ReadAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (first is null ||
                        first.Value.Type != LanFrameType.Pair ||
                        first.Value.Payload.Length != PairPayloadLength)
                    {
                        throw new InvalidOperationException(
                            "The pairing request is invalid.");
                    }

                    byte[] candidate = first.Value.Payload;
                    bool redeemed;
                    try
                    {
                        redeemed = invite.TryRedeem(
                            candidate,
                            () => trust.Save(clientPin, pending: false));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(candidate);
                    }

                    if (!redeemed)
                    {
                        throw new InvalidOperationException("Pairing failed.");
                    }

                    await wire.WriteAsync(
                            LanFrameType.PairAccepted,
                            ReadOnlyMemory<byte>.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clientPin);
            }
        }
        finally
        {
            DisposeQuietly(transport);
        }
    }

    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
            // Closing transport failures must not replace the pairing outcome.
        }
    }
}
