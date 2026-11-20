using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MonitorInputSwitcher.Checks;

/// <summary>
/// Pairing coverage over the existing trusted loopback identities: one real concurrent pc/laptop
/// pairing, one simulated acceptance-reply loss, one consumed-invite replay and explicit clearing.
/// </summary>
internal static class LanPairingChecks
{
    private static readonly TimeSpan AttemptDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(
        LanTestAssertion check,
        string root,
        X509Certificate2 clientIdentity,
        X509Certificate2 serverIdentity)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(clientIdentity);
        ArgumentNullException.ThrowIfNull(serverIdentity);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A pairing test root is required.", nameof(root));
        }

        string[] names = { "pc", "laptop", "ack-pc", "replay-pc", "replay-laptop" };
        var directories = new string[names.Length];
        var stores = new LanTrustStore[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            directories[index] = Path.Combine(root, "pairing-" + names[index]);
            Directory.CreateDirectory(directories[index]);
            stores[index] = new LanTrustStore(directories[index]);
        }

        LanTrustStore pcStore = stores[0], laptopStore = stores[1], ackPcStore = stores[2],
            replayPcStore = stores[3], replayLaptopStore = stores[4];
        byte[] clientPin = LanIdentity.Fingerprint(clientIdentity);
        byte[] serverPin = LanIdentity.Fingerprint(serverIdentity);

        try
        {
            var invite = new LanPairingInvite(serverPin);
            var paired = await AttemptAsync(clientIdentity, pcStore, invite.Code, (stream, token) =>
                LanPairingChannel.PairAsLaptopAsync(
                    stream, serverIdentity, invite, laptopStore, token)).ConfigureAwait(false);
            check("pairing concurrent pc run succeeds", paired.Pc is null, paired.Pc?.GetType().Name);
            check("pairing concurrent laptop run succeeds", paired.Peer is null,
                paired.Peer?.GetType().Name);
            check("pairing pc store trusts the laptop fingerprint", Trusts(pcStore, serverPin));
            check("pairing laptop store trusts the pc fingerprint", Trusts(laptopStore, clientPin));

            // Test-only partner: redeem the invitation, then close without PairAccepted.
            var lostInvite = new LanPairingInvite(serverPin);
            var redeemed = false;
            var lost = await AttemptAsync(clientIdentity, ackPcStore, lostInvite.Code,
                async (stream, token) =>
                {
                    (SslStream ssl, byte[] peerPin) = await LanSecureTransport
                        .AcceptAsync(stream, serverIdentity, peerPin: null, token)
                        .ConfigureAwait(false);
                    try
                    {
                        using (ssl)
                        {
                            if (await new LanWire(ssl).ReadAsync(token).ConfigureAwait(false)
                                is LanFrame pair && pair.Type == LanFrameType.Pair)
                            {
                                redeemed = lostInvite.TryRedeem(
                                    pair.Payload, () => laptopStore.Save(peerPin, pending: false));
                            }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(peerPin);
                    }
                }).ConfigureAwait(false);
            check("pairing lost acceptance reply redeems the invitation", redeemed);
            check("pairing lost acceptance reply fails the pc run", lost.Pc is not null,
                lost.Pc?.GetType().Name);
            check("pairing lost acceptance reply leaves the pc pending",
                ackPcStore.Load() is { Pending: true });
            check("pairing lost acceptance reply keeps the laptop trusted",
                Trusts(laptopStore, clientPin));

            var replay = await AttemptAsync(clientIdentity, replayPcStore, invite.Code,
                (stream, token) => LanPairingChannel.PairAsLaptopAsync(
                    stream, serverIdentity, invite, replayLaptopStore, token)).ConfigureAwait(false);
            check("pairing consumed invite fails the pc run", replay.Pc is not null,
                replay.Pc?.GetType().Name);
            check("pairing consumed invite fails the laptop run", replay.Peer is not null,
                replay.Peer?.GetType().Name);
            check("pairing consumed invite leaves no laptop trust",
                replayLaptopStore.Load() is null);
            check("pairing consumed invite leaves the pc pending",
                replayPcStore.Load() is { Pending: true });

            foreach (LanTrustStore store in stores)
            {
                store.Clear();
            }

            for (var index = 0; index < stores.Length; index++)
            {
                check($"pairing clear empties the {names[index]} store",
                    stores[index].Load() is null);
            }
        }
        finally
        {
            foreach (LanTrustStore store in stores)
            {
                store.Clear();
            }

            foreach (string directory in directories)
            {
                RemoveEmptyDirectory(directory);
            }
        }
    }

    /// <summary>Joins the pc side to a partner over a fresh loopback listener with bounded waits.</summary>
    private static async Task<(Exception? Pc, Exception? Peer)> AttemptAsync(
        X509Certificate2 clientIdentity,
        LanTrustStore pcTrust,
        string code,
        Func<Stream, CancellationToken, Task> partnerAction)
    {
        using var deadline = new CancellationTokenSource(AttemptDeadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<Exception?> partner = Task.Run(async () =>
        {
            try
            {
                using TcpClient accepted = await listener
                    .AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                await partnerAction(accepted.GetStream(), deadline.Token).ConfigureAwait(false);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        Exception? pcFailure;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token)
                .ConfigureAwait(false);
            await LanPairingChannel.PairAsPcAsync(
                    client.GetStream(), clientIdentity, code, pcTrust, deadline.Token)
                .ConfigureAwait(false);
            pcFailure = null;
        }
        catch (Exception exception)
        {
            pcFailure = exception;
        }

        listener.Stop();

        try
        {
            return (pcFailure, await partner.WaitAsync(JoinDeadline).ConfigureAwait(false));
        }
        catch (TimeoutException exception)
        {
            return (pcFailure, exception);
        }
    }

    private static bool Trusts(LanTrustStore store, ReadOnlySpan<byte> expectedPin)
    {
        if (store.Load() is not { Pending: false } peer)
        {
            return false;
        }

        byte[] stored = peer.ReadPin();
        try
        {
            return CryptographicOperations.FixedTimeEquals(stored, expectedPin);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    private static void RemoveEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
