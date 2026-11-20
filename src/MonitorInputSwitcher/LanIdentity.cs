using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MonitorInputSwitcher;

/// <summary>
/// Owns this user's LAN leaf identity. The private key lives only in a per-user named
/// Microsoft Software Key Storage Provider key; the directory contains only the public DER.
/// </summary>
public static class LanIdentity
{
    private const string CertificateFileName = "lan-identity.cer";
    private const string KeyNamePrefix = "MonitorInputSwitcher.LanIdentity.";
    private const string KeyStorageProviderName = "Microsoft Software Key Storage Provider";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string EcP256Oid = "1.2.840.10045.3.1.7";

    private static readonly CngProvider KeyStorageProvider = new(KeyStorageProviderName);
    private static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(365 * 5);
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    public static X509Certificate2 Open(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A LAN identity directory is required.", nameof(directory));
        }

        string identityDirectory;
        try
        {
            identityDirectory = NormalizeDirectory(directory);
            Directory.CreateDirectory(identityDirectory);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException("The LAN identity directory is unavailable.", exception);
        }

        string keyName = CreateKeyName(identityDirectory);
        string certificatePath = Path.Combine(identityDirectory, CertificateFileName);

        return File.Exists(certificatePath)
            ? LoadIdentity(certificatePath, keyName)
            : CreateIdentity(certificatePath, keyName);
    }

    public static byte[] Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.GetRawCertData());
    }

    private static X509Certificate2 CreateIdentity(string certificatePath, string keyName)
    {
        CngKey? cngKey = null;
        ECDsaCng? signingKey = null;
        X509Certificate2? selfSigned = null;
        X509Certificate2? publicCertificate = null;
        X509Certificate2? identity = null;

        try
        {
            if (CngKey.Exists(keyName, KeyStorageProvider, CngKeyOpenOptions.None))
            {
                // A previous first creation may have persisted the application-owned key and
                // then failed (crash or process end) before the DER was written. Recover by
                // opening the existing key with the same provider and options instead of
                // failing forever; the key is validated and the DER is saved atomically below.
                cngKey = CngKey.Open(keyName, KeyStorageProvider, CngKeyOpenOptions.None);
                ValidateKey(cngKey);
                signingKey = new ECDsaCng(cngKey);
            }
            else
            {
                var creationParameters = new CngKeyCreationParameters
                {
                    Provider = KeyStorageProvider,
                    KeyCreationOptions = CngKeyCreationOptions.None,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                };

                cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, creationParameters);
                ValidateKey(cngKey);
                signingKey = new ECDsaCng(cngKey);
            }

            var request = new CertificateRequest(
                "CN=Monitor Input Switcher",
                signingKey,
                HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, critical: true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

            var enhancedKeyUsages = new OidCollection
            {
                new Oid(ServerAuthenticationOid),
                new Oid(ClientAuthenticationOid)
            };
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(enhancedKeyUsages, critical: false));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset notBefore = now - ClockSkewAllowance;
            DateTimeOffset notAfter = now + CertificateLifetime;

            selfSigned = request.CreateSelfSigned(notBefore, notAfter);
            byte[] publicDer = selfSigned.Export(X509ContentType.Cert);

            publicCertificate = new X509Certificate2(publicDer);
            ValidateCertificate(publicCertificate);

            identity = publicCertificate.CopyWithPrivateKey(signingKey);
            ValidateLoadedIdentity(identity);

            WriteCertificateAtomically(certificatePath, publicDer);
            return identity;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            identity?.Dispose();
            throw new InvalidOperationException(
                "The LAN identity could not be created securely.",
                exception);
        }
        finally
        {
            publicCertificate?.Dispose();
            selfSigned?.Dispose();
            signingKey?.Dispose();
            cngKey?.Dispose();
        }
    }

    private static X509Certificate2 LoadIdentity(string certificatePath, string keyName)
    {
        byte[] publicDer;
        try
        {
            publicDer = File.ReadAllBytes(certificatePath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The LAN identity certificate could not be read.",
                exception);
        }

        X509Certificate2? certificate = null;
        CngKey? cngKey = null;
        ECDsaCng? signingKey = null;
        X509Certificate2? identity = null;

        try
        {
            certificate = new X509Certificate2(publicDer);
            ValidateCertificate(certificate);

            cngKey = CngKey.Open(keyName, KeyStorageProvider, CngKeyOpenOptions.None);
            ValidateKey(cngKey);

            signingKey = new ECDsaCng(cngKey);
            EnsureKeyMatchesCertificate(certificate, signingKey);

            identity = certificate.CopyWithPrivateKey(signingKey);
            ValidateLoadedIdentity(identity);
            return identity;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            identity?.Dispose();
            throw new InvalidOperationException(
                "The LAN identity could not be opened securely.",
                exception);
        }
        finally
        {
            signingKey?.Dispose();
            cngKey?.Dispose();
            certificate?.Dispose();
        }
    }

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        DateTime utcNow = DateTime.UtcNow;
        DateTime notBefore = certificate.NotBefore.ToUniversalTime();
        DateTime notAfter = certificate.NotAfter.ToUniversalTime();

        if (utcNow > notAfter || utcNow + ClockSkewAllowance < notBefore)
        {
            throw new CryptographicException("The LAN identity certificate is not currently valid.");
        }

        X509BasicConstraintsExtension? constraints =
            certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (constraints is null || constraints.CertificateAuthority)
        {
            throw new CryptographicException("The LAN identity certificate is not a leaf certificate.");
        }

        X509KeyUsageExtension? keyUsage =
            certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is null ||
            (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        {
            throw new CryptographicException(
                "The LAN identity certificate cannot authenticate a TLS peer.");
        }

        X509EnhancedKeyUsageExtension? enhancedKeyUsage =
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (enhancedKeyUsage is null ||
            !HasEnhancedKeyUsage(enhancedKeyUsage, ServerAuthenticationOid) ||
            !HasEnhancedKeyUsage(enhancedKeyUsage, ClientAuthenticationOid))
        {
            throw new CryptographicException(
                "The LAN identity certificate lacks the required authentication usages.");
        }

        using ECDsa? publicKey = certificate.GetECDsaPublicKey();
        if (publicKey is null || !IsP256(publicKey))
        {
            throw new CryptographicException(
                "The LAN identity certificate does not contain a P-256 public key.");
        }
    }

    private static void ValidateKey(CngKey key)
    {
        if (key.Algorithm != CngAlgorithm.ECDsaP256)
        {
            throw new CryptographicException("The LAN identity key is not an ECDSA P-256 key.");
        }

        if ((key.KeyUsage & CngKeyUsages.Signing) == 0)
        {
            throw new CryptographicException("The LAN identity key is not a signing key.");
        }

        if (key.ExportPolicy != CngExportPolicies.None)
        {
            throw new CryptographicException("The LAN identity key is exportable.");
        }
    }

    private static void EnsureKeyMatchesCertificate(
        X509Certificate2 certificate,
        ECDsa signingKey)
    {
        using ECDsa? publicKey = certificate.GetECDsaPublicKey();
        if (publicKey is null)
        {
            throw new CryptographicException("The LAN identity certificate has no ECDSA public key.");
        }

        ECParameters expected = publicKey.ExportParameters(includePrivateParameters: false);
        ECParameters actual = signingKey.ExportParameters(includePrivateParameters: false);

        if (!SameCurve(expected.Curve, actual.Curve) ||
            expected.Q.X is null ||
            expected.Q.Y is null ||
            actual.Q.X is null ||
            actual.Q.Y is null ||
            !CryptographicOperations.FixedTimeEquals(expected.Q.X, actual.Q.X) ||
            !CryptographicOperations.FixedTimeEquals(expected.Q.Y, actual.Q.Y))
        {
            throw new CryptographicException(
                "The named LAN identity key does not match the public certificate.");
        }
    }

    private static void ValidateLoadedIdentity(X509Certificate2 identity)
    {
        if (!identity.HasPrivateKey)
        {
            throw new CryptographicException("The LAN identity has no private key.");
        }

        using ECDsa? privateKey = identity.GetECDsaPrivateKey();
        using ECDsa? publicKey = identity.GetECDsaPublicKey();
        if (privateKey is null || publicKey is null || !IsP256(publicKey))
        {
            throw new CryptographicException("The LAN identity private key is not usable.");
        }

        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] signature = privateKey.SignData(challenge, HashAlgorithmName.SHA256);
        if (!publicKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("The LAN identity private key is not usable.");
        }
    }

    private static void WriteCertificateAtomically(string certificatePath, byte[] publicDer)
    {
        string? directory = Path.GetDirectoryName(certificatePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The LAN identity directory is unavailable.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".lan-identity-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(publicDer);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, certificatePath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A stale temporary file is preferable to losing the identity failure.
            }
        }
    }

    private static string NormalizeDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return trimmed.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
    }

    private static string CreateKeyName(string normalizedDirectory)
    {
        byte[] directoryHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedDirectory.ToUpperInvariant()));
        return KeyNamePrefix + Convert.ToHexString(directoryHash);
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

    private static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256)
        {
            return false;
        }

        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        return parameters.Curve.IsNamed &&
               string.Equals(parameters.Curve.Oid.Value, EcP256Oid, StringComparison.Ordinal);
    }

    private static bool SameCurve(ECCurve left, ECCurve right)
    {
        if (!left.IsNamed || !right.IsNamed)
        {
            return false;
        }

        if (left.Oid.Value is not null && right.Oid.Value is not null)
        {
            return string.Equals(left.Oid.Value, right.Oid.Value, StringComparison.Ordinal);
        }

        return string.Equals(
            left.Oid.FriendlyName,
            right.Oid.FriendlyName,
            StringComparison.Ordinal);
    }
}
