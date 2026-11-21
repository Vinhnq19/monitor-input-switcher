using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

/// <summary>
/// Authenticated TLS for the paired LAN channel. Trust is established by the caller's
/// SHA-256 leaf pin (or, on the receiving side only, by the invitation gate that owns the
/// null-pin overload). The TLS handshakes always require a certificate in both directions
/// and prove possession of its private key.
/// </summary>
public static class LanSecureTransport
{
    private const int PinLength = 32;
    private const string TargetHost = "monitor-input-switcher";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private static readonly SslApplicationProtocol ApplicationProtocol =
        new("monitor-input-switcher/1");
    internal static readonly SslApplicationProtocol ControlWithFiles = new("monitor-input-switcher/2");
    internal static readonly SslApplicationProtocol FilesProtocol = new("monitor-input-switcher/files/1");

    public static async Task<SslStream> ConnectAsync(
        Stream transport,
        X509Certificate2 local,
        ReadOnlyMemory<byte> peerPin,
        CancellationToken cancellationToken, bool paired = false, bool files = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(local);

        if (peerPin.Length != PinLength)
        {
            throw new ArgumentException(
                "The peer certificate pin must be 32 bytes.",
                nameof(peerPin));
        }

        EnsureUsableLocalCertificate(local);
        byte[] expectedPin = peerPin.ToArray();

        SslStream? sslStream = null;
        try
        {
            sslStream = new SslStream(transport, leaveInnerStreamOpen: false);

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = TargetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = files ? [FilesProtocol] :
                    paired ? [ControlWithFiles, ApplicationProtocol] : [ApplicationProtocol],
                ClientCertificates = new X509CertificateCollection { local },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => local,
                RemoteCertificateValidationCallback =
                    (_, certificate, _, _) => ValidateRemoteCertificate(
                        certificate,
                        expectedPin,
                        ServerAuthenticationOid),
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true
                },
                AllowTlsResume = false
            };

            await sslStream.AuthenticateAsClientAsync(options, cancellationToken)
                .ConfigureAwait(false);

            EnsureAuthenticatedSession(sslStream, options.ApplicationProtocols);

            SslStream established = sslStream;
            sslStream = null;
            return established;
        }
        catch
        {
            if (sslStream is not null)
            {
                sslStream.Dispose();
            }
            else
            {
                transport.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Accepts a peer. A null <paramref name="peerPin"/> is only for the future
    /// invitation-gated receiver; the returned pin must be persisted through that gate.
    /// </summary>
    public static async Task<(SslStream Stream, byte[] PeerPin)> AcceptAsync(
        Stream transport,
        X509Certificate2 local,
        ReadOnlyMemory<byte>? peerPin,
        CancellationToken cancellationToken, bool paired = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(local);

        byte[]? expectedPin = null;
        if (peerPin.HasValue)
        {
            if (peerPin.Value.Length != PinLength)
            {
                throw new ArgumentException(
                    "The peer certificate pin must be 32 bytes.",
                    nameof(peerPin));
            }

            expectedPin = peerPin.Value.ToArray();
        }

        EnsureUsableLocalCertificate(local);
        SslStreamCertificateContext serverContext =
            SslStreamCertificateContext.Create(local, null, offline: true);

        SslStream? sslStream = null;
        try
        {
            sslStream = new SslStream(transport, leaveInnerStreamOpen: false);

            var options = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = serverContext,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = paired ? [ControlWithFiles, ApplicationProtocol, FilesProtocol] : [ApplicationProtocol],
                RemoteCertificateValidationCallback =
                    (_, certificate, _, _) => ValidateRemoteCertificate(
                        certificate,
                        expectedPin,
                        ClientAuthenticationOid),
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true
                },
                AllowTlsResume = false
            };

            await sslStream.AuthenticateAsServerAsync(options, cancellationToken)
                .ConfigureAwait(false);

            EnsureAuthenticatedSession(sslStream, options.ApplicationProtocols);

            X509Certificate? remoteCertificate = sslStream.RemoteCertificate;
            if (remoteCertificate is null)
            {
                throw new AuthenticationException(
                    "The peer did not present an authenticated certificate.");
            }

            byte[] actualPin = SHA256.HashData(remoteCertificate.GetRawCertData());
            if (expectedPin is not null &&
                !CryptographicOperations.FixedTimeEquals(actualPin, expectedPin))
            {
                throw new AuthenticationException(
                    "The peer certificate does not match the expected pin.");
            }

            SslStream established = sslStream;
            sslStream = null;
            return (established, actualPin);
        }
        catch
        {
            if (sslStream is not null)
            {
                sslStream.Dispose();
            }
            else
            {
                transport.Dispose();
            }

            throw;
        }
    }

    private static bool ValidateRemoteCertificate(
        X509Certificate? certificate,
        byte[]? expectedPin,
        string requiredEnhancedKeyUsage)
    {
        if (certificate is null)
        {
            return false;
        }

        try
        {
            using var leaf = new X509Certificate2(certificate.GetRawCertData());

            if (!IsValidPeerLeaf(leaf, requiredEnhancedKeyUsage))
            {
                return false;
            }

            if (expectedPin is not null && !MatchesPin(leaf, expectedPin))
            {
                return false;
            }

            return HasAcceptableOfflineChain(leaf);
        }
        catch (Exception exception)
        {
            if (exception is OutOfMemoryException)
            {
                throw;
            }

            return false;
        }
    }

    private static bool IsValidPeerLeaf(
        X509Certificate2 certificate,
        string requiredEnhancedKeyUsage)
    {
        DateTime utcNow = DateTime.UtcNow;
        if (utcNow < certificate.NotBefore.ToUniversalTime() ||
            utcNow > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        foreach (X509BasicConstraintsExtension constraints in
                 certificate.Extensions.OfType<X509BasicConstraintsExtension>())
        {
            if (constraints.CertificateAuthority)
            {
                return false;
            }
        }

        X509KeyUsageExtension? keyUsage =
            FindExtension<X509KeyUsageExtension>(certificate);
        if (keyUsage is not null &&
            (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        {
            return false;
        }

        X509EnhancedKeyUsageExtension? enhancedKeyUsage =
            FindExtension<X509EnhancedKeyUsageExtension>(certificate);
        if (enhancedKeyUsage is not null &&
            !HasEnhancedKeyUsage(enhancedKeyUsage, requiredEnhancedKeyUsage))
        {
            return false;
        }

        return true;
    }

    private static bool HasAcceptableOfflineChain(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
            VerificationTime = DateTime.UtcNow,
            VerificationFlags =
                X509VerificationFlags.AllowUnknownCertificateAuthority |
                X509VerificationFlags.IgnoreInvalidBasicConstraints |
                X509VerificationFlags.IgnoreWrongUsage
        };

        // Trust is intentionally not taken from the platform chain. For a pinned peer the
        // pin is the authority; for an invitation peer, TLS possession plus the invitation
        // gate is the authority. The offline build is only an extension-shape check.
        _ = chain.Build(certificate);

        foreach (X509ChainStatus status in chain.ChainStatus)
        {
            if (status.Status == X509ChainStatusFlags.NotTimeNested ||
                status.Status == X509ChainStatusFlags.InvalidExtension)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesPin(X509Certificate2 certificate, byte[] expectedPin)
    {
        if (expectedPin.Length != PinLength)
        {
            return false;
        }

        byte[] actualPin = SHA256.HashData(certificate.RawData);
        return CryptographicOperations.FixedTimeEquals(actualPin, expectedPin);
    }

    private static void EnsureUsableLocalCertificate(X509Certificate2 local)
    {
        if (!local.HasPrivateKey)
        {
            throw new ArgumentException(
                "The local LAN identity certificate has no private key.",
                nameof(local));
        }

        DateTime utcNow = DateTime.UtcNow;
        if (utcNow < local.NotBefore.ToUniversalTime() ||
            utcNow > local.NotAfter.ToUniversalTime())
        {
            throw new ArgumentException(
                "The local LAN identity certificate is not currently valid.",
                nameof(local));
        }
    }

    private static void EnsureAuthenticatedSession(SslStream sslStream, List<SslApplicationProtocol> protocols)
    {
        if (!sslStream.IsAuthenticated || !sslStream.IsMutuallyAuthenticated)
        {
            throw new AuthenticationException(
                "Mutual TLS authentication was not established.");
        }

        if (sslStream.SslProtocol != SslProtocols.Tls12 &&
            sslStream.SslProtocol != SslProtocols.Tls13)
        {
            throw new AuthenticationException(
                "The negotiated TLS protocol is not supported.");
        }

        if (!protocols.Contains(sslStream.NegotiatedApplicationProtocol))
        {
            throw new AuthenticationException(
                "The negotiated application protocol is not supported.");
        }
    }

    private static T? FindExtension<T>(X509Certificate2 certificate)
        where T : X509Extension
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static bool HasEnhancedKeyUsage(
        X509EnhancedKeyUsageExtension extension,
        string oid)
    {
        foreach (Oid usage in extension.EnhancedKeyUsages)
        {
            if (string.Equals(usage.Value, oid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
