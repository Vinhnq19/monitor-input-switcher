using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

/// <summary>
/// One authenticated paired TCP connection bound to an eligible Private IPv4 route. The
/// link owns the socket and the TLS stream, performs only the ordered Hello/Ready exchange
/// and then hands the same <see cref="LanWire"/> to the caller so that application framing
/// continues at the next sequence number. Peer identity is always the persisted pin; the
/// remote address and the reported host name are informational only.
/// </summary>
public sealed class LanLink : IDisposable
{
    private const int MaxNameLength = 128;
    private const int ListenerBacklog = 8;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private SslStream? _ssl;
    private TcpClient? _tcp;
    private int _disposed;

    internal LanLink(
        TcpClient tcp,
        SslStream ssl,
        LanWire wire,
        IPAddress peerAddress,
        string peerName)
    {
        _tcp = tcp;
        _ssl = ssl;
        Wire = wire;
        PeerAddress = peerAddress;
        PeerName = peerName;
    }

    /// <summary>The framed stream positioned immediately after the Hello/Ready exchange.</summary>
    public LanWire Wire { get; }

    /// <summary>The host name the peer reported in its handshake frame.</summary>
    public string PeerName { get; }

    /// <summary>The remote IPv4 address of this connection.</summary>
    public IPAddress PeerAddress { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Connects to <paramref name="peer"/> from the most specific eligible Private route
    /// that contains it. The socket is bound to that route's address, port 0, before the
    /// connect so the operating system cannot choose another source address.
    /// </summary>
    public static async Task<TcpClient> OpenClientAsync(
        IPAddress peer,
        IReadOnlyList<LanPrivateRoute> routes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(routes);

        if (peer.AddressFamily != AddressFamily.InterNetwork ||
            peer.Equals(IPAddress.Any) ||
            peer.Equals(IPAddress.Broadcast))
        {
            throw new ArgumentException("The peer must be an IPv4 address.", nameof(peer));
        }

        LanPrivateRoute? route = SelectRoute(peer, routes);
        if (route is null)
        {
            throw new InvalidOperationException(
                "No eligible private route can reach the peer.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new TcpClient(AddressFamily.InterNetwork);
        bool success = false;
        try
        {
            client.NoDelay = true;
            client.Client.Bind(new IPEndPoint(route.LocalAddress, 0));

            await client
                .ConnectAsync(peer, LanDiscovery.Port, cancellationToken)
                .ConfigureAwait(false);

            if (!IsEligibleClient(client, route))
            {
                throw new InvalidOperationException(
                    "The connection is not bound to an eligible private route.");
            }

            success = true;
            return client;
        }
        finally
        {
            if (!success)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates an exclusive listener bound to exactly the route's address and the LAN
    /// discovery port. A failed bind leaves no listener behind.
    /// </summary>
    public static TcpListener StartListener(LanPrivateRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        IPAddress local = route.LocalAddress;
        if (!IsConcreteIPv4(local) ||
            !IsConcreteIPv4(route.BroadcastAddress) ||
            local.Equals(route.BroadcastAddress))
        {
            throw new ArgumentException("The route cannot bind a listener.", nameof(route));
        }

        TcpListener? listener = null;
        try
        {
            // The explicit endpoint keeps the bind on this one eligible address and port.
            listener = new TcpListener(local, LanDiscovery.Port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(ListenerBacklog);
            return listener;
        }
        catch
        {
            listener?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True only when both endpoints are IPv4, the local address is exactly the route's
    /// address and the remote address is an ordinary host inside that route's subnet.
    /// </summary>
    public static bool IsEligibleClient(TcpClient client, LanPrivateRoute route)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(route);

        if (!IsConcreteIPv4(route.LocalAddress))
        {
            return false;
        }

        if (client.Client.AddressFamily != AddressFamily.InterNetwork ||
            client.Client.LocalEndPoint is not IPEndPoint local ||
            local.AddressFamily != AddressFamily.InterNetwork ||
            !local.Address.Equals(route.LocalAddress))
        {
            return false;
        }

        if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
            remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return route.ContainsPeer(remote.Address);
    }

    /// <summary>
    /// PC role. The TLS client proves the stored laptop pin (Pending is allowed) and then
    /// writes Hello and reads Ready. A Pending pin is promoted only after a valid Ready.
    /// The caller transfers socket ownership on success only.
    /// </summary>
    public static async Task<LanLink> ConnectPairedAsync(
        TcpClient tcp,
        X509Certificate2 identity,
        LanTrustStore trust,
        string localName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);

        SslStream? ssl = null;
        bool success = false;

        try
        {
            byte[] localNameBytes = EncodeName(localName, nameof(localName));

            EnsureUsableClient(tcp);
            cancellationToken.ThrowIfCancellationRequested();

            LanPeerTrust? saved = trust.Load();
            if (saved is null)
            {
                throw new InvalidOperationException("The peer is not trusted.");
            }

            bool promote = saved.Pending;
            byte[] pin = saved.ReadPin();
            try
            {
                ssl = await LanSecureTransport.ConnectAsync(
                        tcp.GetStream(),
                        identity,
                        pin,
                        cancellationToken)
                    .ConfigureAwait(false);

                var wire = new LanWire(ssl);

                await wire
                    .WriteAsync(LanFrameType.Hello, localNameBytes, cancellationToken)
                    .ConfigureAwait(false);

                LanFrame? reply = await wire
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (reply is null || reply.Value.Type != LanFrameType.Ready)
                {
                    throw new InvalidOperationException("The handshake reply is invalid.");
                }

                string peerName = DecodeName(reply.Value.Payload);
                IPAddress peerAddress = ReadRemoteAddress(tcp);

                // The laptop's pin becomes trusted only once its Ready was accepted.
                if (promote)
                {
                    trust.Save(pin, pending: false);
                }

                var link = new LanLink(tcp, ssl, wire, peerAddress, peerName);
                success = true;
                return link;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pin);
            }
        }
        finally
        {
            if (!success)
            {
                DisposeQuietly(ssl);
                DisposeQuietly(tcp);
            }
        }
    }

    /// <summary>
    /// Laptop role. The TLS server requires an existing, non-Pending pin (passed to the
    /// accept as the non-null client certificate pin), reads Hello and then writes Ready.
    /// The caller transfers accepted socket ownership on success only.
    /// </summary>
    public static async Task<LanLink> AcceptPairedAsync(
        TcpClient tcp,
        LanPrivateRoute route,
        X509Certificate2 identity,
        LanTrustStore trust,
        string localName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trust);

        SslStream? ssl = null;
        bool success = false;

        try
        {
            byte[] localNameBytes = EncodeName(localName, nameof(localName));

            if (!IsEligibleClient(tcp, route))
            {
                throw new InvalidOperationException(
                    "The accepted connection is not an eligible private peer.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            LanPeerTrust? saved = trust.Load();
            if (saved is null || saved.Pending)
            {
                throw new InvalidOperationException("The peer is not trusted.");
            }

            byte[] pin = saved.ReadPin();
            try
            {
                (ssl, byte[] peerPin) = await LanSecureTransport.AcceptAsync(
                        tcp.GetStream(),
                        identity,
                        pin,
                        cancellationToken)
                    .ConfigureAwait(false);

                CryptographicOperations.ZeroMemory(peerPin);

                var wire = new LanWire(ssl);

                LanFrame? first = await wire
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (first is null || first.Value.Type != LanFrameType.Hello)
                {
                    throw new InvalidOperationException("The handshake request is invalid.");
                }

                string peerName = DecodeName(first.Value.Payload);
                IPAddress peerAddress = ReadRemoteAddress(tcp);

                await wire
                    .WriteAsync(LanFrameType.Ready, localNameBytes, cancellationToken)
                    .ConfigureAwait(false);

                var link = new LanLink(tcp, ssl, wire, peerAddress, peerName);
                success = true;
                return link;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pin);
            }
        }
        finally
        {
            if (!success)
            {
                DisposeQuietly(ssl);
                DisposeQuietly(tcp);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SslStream? ssl = Interlocked.Exchange(ref _ssl, null);
        TcpClient? tcp = Interlocked.Exchange(ref _tcp, null);

        DisposeQuietly(ssl);
        DisposeQuietly(tcp);
    }

    /// <summary>
    /// Picks the route with the longest contiguous prefix among those that contain the
    /// peer. An unusable route is never selected.
    /// </summary>
    private static LanPrivateRoute? SelectRoute(
        IPAddress peer,
        IReadOnlyList<LanPrivateRoute> routes)
    {
        LanPrivateRoute? selected = null;
        int selectedPrefix = -1;

        foreach (LanPrivateRoute? route in routes)
        {
            if (route is null || !IsConcreteIPv4(route.LocalAddress))
            {
                continue;
            }

            if (!route.ContainsPeer(peer))
            {
                continue;
            }

            int prefix = PrefixLength(route.SubnetMask);
            if (prefix < 0)
            {
                continue;
            }

            if (prefix > selectedPrefix)
            {
                selected = route;
                selectedPrefix = prefix;
            }
        }

        return selected;
    }

    private static int PrefixLength(IPAddress? mask)
    {
        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return -1;
        }

        byte[] bytes = mask.GetAddressBytes();
        if (bytes.Length != sizeof(uint))
        {
            return -1;
        }

        int bits = 0;
        bool sawZero = false;
        foreach (byte value in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) != 0)
                {
                    if (sawZero)
                    {
                        // A non-contiguous mask is not a usable subnet.
                        return -1;
                    }

                    bits++;
                }
                else
                {
                    sawZero = true;
                }
            }
        }

        return bits == 0 ? -1 : bits;
    }

    private static bool IsConcreteIPv4(IPAddress? address) =>
        address is not null &&
        address.AddressFamily == AddressFamily.InterNetwork &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.Broadcast) &&
        !address.Equals(IPAddress.Loopback);

    private static void EnsureUsableClient(TcpClient tcp)
    {
        if (tcp.Client.AddressFamily != AddressFamily.InterNetwork ||
            !tcp.Connected ||
            tcp.Client.LocalEndPoint is not IPEndPoint local ||
            local.AddressFamily != AddressFamily.InterNetwork ||
            !IsConcreteIPv4(local.Address) ||
            tcp.Client.RemoteEndPoint is not IPEndPoint remote ||
            remote.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(
                "The connection is not an eligible IPv4 TCP connection.");
        }
    }

    private static IPAddress ReadRemoteAddress(TcpClient tcp)
    {
        if (tcp.Client.RemoteEndPoint is not IPEndPoint remote ||
            remote.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("The connection has no IPv4 peer address.");
        }

        return remote.Address;
    }

    private static byte[] EncodeName(string name, string parameterName)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A host name is required.", parameterName);
        }

        foreach (char character in name)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("The host name is invalid.", parameterName);
            }
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The host name is invalid.", parameterName);
        }

        if (bytes.Length == 0 || bytes.Length > MaxNameLength)
        {
            throw new ArgumentException("The host name is invalid.", parameterName);
        }

        return bytes;
    }

    private static string DecodeName(byte[]? payload)
    {
        if (payload is null || payload.Length == 0 || payload.Length > MaxNameLength)
        {
            throw new InvalidOperationException("The peer name is invalid.");
        }

        string name;
        try
        {
            name = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("The peer name is invalid.");
        }

        if (name.Length == 0)
        {
            throw new InvalidOperationException("The peer name is invalid.");
        }

        foreach (char character in name)
        {
            if (char.IsControl(character))
            {
                throw new InvalidOperationException("The peer name is invalid.");
            }
        }

        return name;
    }

    private static void DisposeQuietly(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Releasing a failed link must not replace the reported failure.
        }
    }
}
