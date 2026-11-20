using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

/// <summary>
/// Finds the one laptop that already owns a known certificate pin with a single small UDP
/// multicast round trip. A frame is exactly 56 bytes: 7 ASCII magic bytes, one kind byte,
/// a 16 byte nonce and the 32 byte SHA-256 certificate pin. Only a reply carrying the same
/// nonce and the expected pin, received on the same Windows IPv4 interface index and from
/// an address inside the same Private route, is accepted. Peer identity is never taken
/// from this exchange; the pin stays the only identity input.
/// </summary>
public static class LanDiscovery
{
    public const int Port = 49351;

    private const int MagicLength = 7;
    private const int KindOffset = MagicLength;
    private const int NonceOffset = 8;
    private const int NonceLength = 16;
    private const int PinOffset = NonceOffset + NonceLength;
    private const int PinLength = 32;
    private const int FrameLength = PinOffset + PinLength;
    private const byte QueryKind = 1;
    private const byte ReplyKind = 2;
    private const int TimeToLive = 1;

    // One extra byte lets a datagram that is not exactly one frame be detected and ignored.
    private const int ReceiveBufferLength = FrameLength + 1;

    private static readonly byte[] Magic = "MISLAN1"u8.ToArray();
    private static readonly IPAddress GroupAddress = IPAddress.Parse("239.255.77.31");
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks every eligible Private route whether the holder of <paramref name="expectedPin"/>
    /// is present. Returns the validated source IPv4 address of the laptop, or null when no
    /// matching reply arrives within one second. Multicast traffic never identifies a peer.
    /// </summary>
    public static async Task<IPAddress?> FindAsync(
        IReadOnlyList<LanPrivateRoute> routes,
        ReadOnlyMemory<byte> expectedPin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (expectedPin.Length != PinLength)
        {
            throw new ArgumentException(
                $"The expected certificate pin must be {PinLength} bytes.",
                nameof(expectedPin));
        }

        List<LanPrivateRoute> eligible = EligibleRoutes(routes);
        if (eligible.Count == 0)
        {
            return null;
        }

        byte[] pin = expectedPin.ToArray();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] query = CreateFrame(QueryKind, nonce, pin);
        var found = new TaskCompletionSource<IPAddress?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var window = new CancellationTokenSource(DiscoveryWindow);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                window.Token);

            var probes = new Task[eligible.Count];
            for (int i = 0; i < eligible.Count; i++)
            {
                probes[i] = ProbeAsync(eligible[i], query, nonce, pin, found, linked.Token);
            }

            Task all = Task.WhenAll(probes);
            Task completed = await Task.WhenAny(found.Task, all).ConfigureAwait(false);
            if (!ReferenceEquals(completed, all))
            {
                // A validated reply arrived; stop the remaining probes before returning.
                linked.Cancel();
            }

            // Every probe is awaited exactly once, after the race has been decided, so a
            // probe that fails on its way out has its failure observed without discarding
            // a validated result.
            try
            {
                await all.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                // Ownership of every probe socket has already been released.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pin);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(query);
        }

        if (found.Task.IsCompletedSuccessfully)
        {
            return found.Task.Result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// Waits for discovery queries and answers only an exact matching-pin query with a reply
    /// of the same size, unicast to the requester from the same eligible source address the
    /// query arrived on.
    /// </summary>
    public static async Task RespondAsync(
        IReadOnlyList<LanPrivateRoute> routes,
        ReadOnlyMemory<byte> localPin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (localPin.Length != PinLength)
        {
            throw new ArgumentException(
                $"The local certificate pin must be {PinLength} bytes.",
                nameof(localPin));
        }

        List<LanPrivateRoute> eligible = EligibleRoutes(routes);
        if (eligible.Count == 0)
        {
            return;
        }

        byte[] pin = localPin.ToArray();
        byte[] buffer = new byte[ReceiveBufferLength];
        byte[] reply = new byte[FrameLength];
        var responses = new Socket?[eligible.Count];
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            listener.Bind(new IPEndPoint(IPAddress.Any, Port));

            // Only eligible adapter addresses join the group; a duplicate join on the same
            // interface is skipped rather than failing the responder.
            var joinedInterfaces = new HashSet<int>();
            foreach (LanPrivateRoute route in eligible)
            {
                if (joinedInterfaces.Add(route.InterfaceIndex))
                {
                    listener.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(GroupAddress, route.LocalAddress));
                }
            }

            var remoteTemplate = new IPEndPoint(IPAddress.Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult result;
                try
                {
                    result = await listener
                        .ReceiveMessageFromAsync(
                            buffer,
                            SocketFlags.None,
                            remoteTemplate,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SocketException exception)
                    when (exception.SocketErrorCode is
                        SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    // A stray ICMP error or oversized datagram is not a discovery query.
                    continue;
                }

                if (result.ReceivedBytes != FrameLength)
                {
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint sender)
                {
                    continue;
                }

                // Interface and subnet are checked before any frame byte is parsed.
                int routeIndex = FindRoute(
                    eligible,
                    result.PacketInformation.Interface,
                    sender.Address);
                if (routeIndex < 0)
                {
                    continue;
                }

                if (!TryBuildReply(buffer, result.ReceivedBytes, pin, reply))
                {
                    continue;
                }

                Socket? response = GetOrCreateResponseSocket(
                    responses,
                    routeIndex,
                    eligible[routeIndex]);
                if (response is null)
                {
                    continue;
                }

                try
                {
                    await response
                        .SendToAsync(
                            reply,
                            SocketFlags.None,
                            new IPEndPoint(sender.Address, sender.Port),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A failed reply must not end the responder.
                }
            }
        }
        finally
        {
            foreach (Socket? response in responses)
            {
                response?.Dispose();
            }

            CryptographicOperations.ZeroMemory(pin);
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(reply);
        }
    }

    private static async Task ProbeAsync(
        LanPrivateRoute route,
        byte[] query,
        byte[] nonce,
        byte[] pin,
        TaskCompletionSource<IPAddress?> found,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferLength];
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                route.LocalAddress.GetAddressBytes());
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive,
                TimeToLive);
            socket.Bind(new IPEndPoint(route.LocalAddress, 0));

            await socket
                .SendToAsync(
                    query,
                    SocketFlags.None,
                    new IPEndPoint(GroupAddress, Port),
                    cancellationToken)
                .ConfigureAwait(false);

            var remoteTemplate = new IPEndPoint(IPAddress.Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult result = await socket
                    .ReceiveMessageFromAsync(
                        buffer,
                        SocketFlags.None,
                        remoteTemplate,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Datagrams from any other interface are dropped before parsing.
                if (result.PacketInformation.Interface != route.InterfaceIndex)
                {
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint sender ||
                    !route.ContainsPeer(sender.Address))
                {
                    continue;
                }

                if (result.ReceivedBytes != FrameLength ||
                    !IsMatchingReply(buffer, nonce, pin))
                {
                    continue;
                }

                found.TrySetResult(sender.Address);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The discovery window closed or the caller cancelled.
        }
        catch (SocketException)
        {
            // One unusable route must not stop the remaining routes from being probed.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static List<LanPrivateRoute> EligibleRoutes(IReadOnlyList<LanPrivateRoute> routes)
    {
        var eligible = new List<LanPrivateRoute>(routes.Count);
        foreach (LanPrivateRoute? route in routes)
        {
            if (route is not null &&
                route.InterfaceIndex > 0 &&
                route.LocalAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                eligible.Add(route);
            }
        }

        return eligible;
    }

    private static int FindRoute(
        List<LanPrivateRoute> routes,
        int interfaceIndex,
        IPAddress peer)
    {
        if (interfaceIndex <= 0)
        {
            return -1;
        }

        for (int i = 0; i < routes.Count; i++)
        {
            if (routes[i].InterfaceIndex == interfaceIndex &&
                routes[i].ContainsPeer(peer))
            {
                return i;
            }
        }

        return -1;
    }

    private static Socket? GetOrCreateResponseSocket(
        Socket?[] sockets,
        int index,
        LanPrivateRoute route)
    {
        Socket? existing = sockets[index];
        if (existing is not null)
        {
            return existing;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Binding the precise eligible source address pins the reply to that route.
            socket.Bind(new IPEndPoint(route.LocalAddress, 0));
            sockets[index] = socket;
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static byte[] CreateFrame(byte kind, byte[] nonce, byte[] pin)
    {
        var frame = new byte[FrameLength];
        Magic.AsSpan().CopyTo(frame);
        frame[KindOffset] = kind;
        nonce.AsSpan().CopyTo(frame.AsSpan(NonceOffset, NonceLength));
        pin.AsSpan().CopyTo(frame.AsSpan(PinOffset, PinLength));
        return frame;
    }

    private static bool TryBuildReply(byte[] frame, int receivedBytes, byte[] pin, byte[] reply)
    {
        // The receive buffer is one byte longer than a frame on purpose, so the validated
        // datagram size, not the buffer size, decides whether this is a well formed query.
        if (receivedBytes != FrameLength ||
            !frame.AsSpan(0, MagicLength).SequenceEqual(Magic) ||
            frame[KindOffset] != QueryKind ||
            !CryptographicOperations.FixedTimeEquals(
                frame.AsSpan(PinOffset, PinLength),
                pin))
        {
            return false;
        }

        Magic.AsSpan().CopyTo(reply);
        reply[KindOffset] = ReplyKind;
        frame.AsSpan(NonceOffset, NonceLength).CopyTo(reply.AsSpan(NonceOffset, NonceLength));
        pin.AsSpan(0, PinLength).CopyTo(reply.AsSpan(PinOffset, PinLength));
        return true;
    }

    private static bool IsMatchingReply(byte[] buffer, byte[] nonce, byte[] pin)
    {
        if (buffer.Length < FrameLength)
        {
            return false;
        }

        ReadOnlySpan<byte> frame = buffer.AsSpan(0, FrameLength);
        if (!frame[..MagicLength].SequenceEqual(Magic) ||
            frame[KindOffset] != ReplyKind)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
                   frame.Slice(NonceOffset, NonceLength),
                   nonce) &&
               CryptographicOperations.FixedTimeEquals(
                   frame.Slice(PinOffset, PinLength),
                   pin);
    }
}
