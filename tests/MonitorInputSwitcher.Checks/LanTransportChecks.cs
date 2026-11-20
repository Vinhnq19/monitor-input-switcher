using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher.Checks;

internal delegate void LanTestAssertion(string name, bool condition, string? detail = null);

internal static class LanTransportChecks
{
    private const byte Version = 1;
    private const int HeaderSize = 14;
    private const string Alpn = "monitor-input-switcher/1";
    private const string CertificateFileName = "lan-identity.cer";

    public static async Task RunAsync(LanTestAssertion check)
    {
        await RunWireChecksAsync(check).ConfigureAwait(false);
        await RunTlsChecksAsync(check).ConfigureAwait(false);
    }

    private static async Task RunWireChecksAsync(LanTestAssertion check)
    {
        CancellationToken cancellationToken = CancellationToken.None;

        var nullRejected = false;
        try
        {
            _ = new LanWire(null!);
        }
        catch (ArgumentNullException)
        {
            nullRejected = true;
        }

        check("wire rejects a null stream", nullRejected);

        var writeStream = new ProbeStream(Array.Empty<byte>());
        var writer = new LanWire(writeStream);
        await writer.WriteAsync(
            LanFrameType.Hello,
            new byte[] { 0xAA, 0xBB },
            cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(
            LanFrameType.Input,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);

        check("wire emits one write per frame", writeStream.WriteCount == 2,
            $"write count {writeStream.WriteCount}");
        check("wire first frame has the exact header and payload",
            writeStream.Writes.Count == 2 &&
            writeStream.Writes[0].SequenceEqual(new byte[]
            {
                0x01, 0x03, 0x02, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xAA, 0xBB
            }),
            "first frame bytes differed");
        check("wire second frame has sequence two and an empty payload",
            writeStream.Writes.Count == 2 &&
            writeStream.Writes[1].SequenceEqual(new byte[]
            {
                0x01, 0x09, 0x00, 0x00, 0x00, 0x00,
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            }),
            "second frame bytes differed");

        var invalidWriteGuard = new ProbeStream(Array.Empty<byte>());
        var invalidWriter = new LanWire(invalidWriteGuard);
        await ExpectFaultAsync(check, "wire rejects an undefined write type",
            LanWireFault.InvalidFrame,
            () => invalidWriter.WriteAsync(
                (LanFrameType)0, ReadOnlyMemory<byte>.Empty, cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects an oversized write payload",
            LanWireFault.InvalidFrame,
            () => invalidWriter.WriteAsync(
                LanFrameType.Input, new byte[1025], cancellationToken).AsTask());
        await invalidWriter.WriteAsync(
            LanFrameType.Ping,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        check("wire failed writes do not consume a sequence number",
            invalidWriteGuard.WriteCount == 1 &&
            ReadSequence(invalidWriteGuard.Writes[0]) == 1,
            $"write count {invalidWriteGuard.WriteCount}");

        var validFrames = Combine(
            BuildFrame(Version, (byte)LanFrameType.Hello, 1, new byte[] { 0x10 }),
            BuildFrame(Version, (byte)LanFrameType.Input, 2, Array.Empty<byte>()));
        var readStream = new ProbeStream(validFrames, maxReadChunk: 1);
        var reader = new LanWire(readStream);

        LanFrame? first = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        check("wire reads an ordered first frame", first.HasValue &&
            first.Value.Type == LanFrameType.Hello &&
            first.Value.Sequence == 1 &&
            first.Value.Payload.SequenceEqual(new byte[] { 0x10 }),
            "first frame mismatch");
        LanFrame? second = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        check("wire reads an ordered second frame", second.HasValue &&
            second.Value.Type == LanFrameType.Input &&
            second.Value.Sequence == 2 &&
            second.Value.Payload.Length == 0,
            "second frame mismatch");
        LanFrame? end = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        check("wire returns null at full EOF", end is null);
        check("wire fragmented reads consume the complete frame",
            readStream.BytesRead == validFrames.Length,
            $"bytes read {readStream.BytesRead}");

        var emptyReader = new LanWire(new ProbeStream(Array.Empty<byte>()));
        check("wire returns null at empty EOF",
            await emptyReader.ReadAsync(cancellationToken).ConfigureAwait(false) is null);

        await ExpectFaultAsync(check, "wire rejects a truncated header",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(new byte[] { 0x01, 0x03, 0x00 }, 1))
                .ReadAsync(cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects a truncated payload",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(
                    Header(1, (byte)LanFrameType.Hello, 1, 3).Concat(new byte[] { 0x01, 0x02 }).ToArray(), 2))
                .ReadAsync(cancellationToken).AsTask());

        var oversizedStream = new ProbeStream(
            Header(1, (byte)LanFrameType.Hello, 1, 1025)
                .Concat(new byte[100])
                .ToArray());
        await ExpectFaultAsync(check, "wire rejects an oversized length",
            LanWireFault.InvalidFrame,
            () => new LanWire(oversizedStream).ReadAsync(cancellationToken).AsTask());
        check("wire rejects an oversized length before reading payload bytes",
            oversizedStream.BytesRead == HeaderSize,
            $"bytes read {oversizedStream.BytesRead}");

        await ExpectFaultAsync(check, "wire rejects a version mismatch",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(Header(2, (byte)LanFrameType.Hello, 1, 0)))
                .ReadAsync(cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects an undefined read type",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(Header(1, 0, 1, 0)))
                .ReadAsync(cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects sequence zero",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(Header(1, (byte)LanFrameType.Hello, 0, 0)))
                .ReadAsync(cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects sequence overflow",
            LanWireFault.InvalidFrame,
            () => new LanWire(new ProbeStream(Header(1, (byte)LanFrameType.Hello, ulong.MaxValue, 0)))
                .ReadAsync(cancellationToken).AsTask());
        await ExpectFaultAsync(check, "wire rejects a sequence gap at the start",
            LanWireFault.MissingOrOutOfOrder,
            () => new LanWire(new ProbeStream(
                    BuildFrame(Version, (byte)LanFrameType.Hello, 2, Array.Empty<byte>())))
                .ReadAsync(cancellationToken).AsTask());

        var duplicateReader = new LanWire(new ProbeStream(Combine(
            BuildFrame(Version, (byte)LanFrameType.Hello, 1, Array.Empty<byte>()),
            BuildFrame(Version, (byte)LanFrameType.Input, 2, Array.Empty<byte>()),
            BuildFrame(Version, (byte)LanFrameType.Input, 2, Array.Empty<byte>()))));
        _ = await duplicateReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _ = await duplicateReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        await ExpectFaultAsync(check, "wire rejects a duplicate sequence",
            LanWireFault.Duplicate,
            () => duplicateReader.ReadAsync(cancellationToken).AsTask());

        var gapReader = new LanWire(new ProbeStream(Combine(
            BuildFrame(Version, (byte)LanFrameType.Hello, 1, Array.Empty<byte>()),
            BuildFrame(Version, (byte)LanFrameType.Input, 2, Array.Empty<byte>()),
            BuildFrame(Version, (byte)LanFrameType.Input, 4, Array.Empty<byte>()))));
        _ = await gapReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        _ = await gapReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        await ExpectFaultAsync(check, "wire rejects a sequence gap after ordered frames",
            LanWireFault.MissingOrOutOfOrder,
            () => gapReader.ReadAsync(cancellationToken).AsTask());

        using var canceledSource = new CancellationTokenSource();
        canceledSource.Cancel();
        var canceledRead = false;
        try
        {
            _ = await new LanWire(new ProbeStream(Array.Empty<byte>()))
                .ReadAsync(canceledSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceledRead = true;
        }

        check("wire read honors cancellation", canceledRead);
    }

    private static async Task RunTlsChecksAsync(LanTestAssertion check)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MonitorInputSwitcher.Checks",
            "lan-" + Guid.NewGuid().ToString("N"));
        string clientDirectory = Path.Combine(root, "client");
        string serverDirectory = Path.Combine(root, "server");
        X509Certificate2? clientIdentity = null;
        X509Certificate2? serverIdentity = null;

        try
        {
            clientIdentity = LanIdentity.Open(clientDirectory);
            serverIdentity = LanIdentity.Open(serverDirectory);
            byte[] clientPin = LanIdentity.Fingerprint(clientIdentity);
            byte[] serverPin = LanIdentity.Fingerprint(serverIdentity);

            check("tls test identities have private keys",
                clientIdentity.HasPrivateKey && serverIdentity.HasPrivateKey);

            HandshakePair success = await RunHandshakeAsync(
                serverIdentity, clientPin, clientIdentity, serverPin).ConfigureAwait(false);
            check("tls mutual pin client handshake succeeds",
                success.Client.Succeeded,
                $"client failure {success.Client.FailureType}");
            check("tls mutual pin server handshake succeeds",
                success.Server.Succeeded,
                $"server failure {success.Server.FailureType}");
            check("tls client negotiates the expected ALPN",
                success.Client.Alpn == Alpn,
                "client ALPN mismatch");
            check("tls server negotiates the expected ALPN",
                success.Server.Alpn == Alpn,
                "server ALPN mismatch");
            check("tls mutual authentication is established",
                success.Client.MutuallyAuthenticated && success.Server.MutuallyAuthenticated,
                "mutual authentication missing");

            byte[] wrongPin = new byte[32];
            HandshakePair clientMismatch = await RunHandshakeAsync(
                serverIdentity, wrongPin, clientIdentity, serverPin).ConfigureAwait(false);
            check("tls mismatched client pin is rejected by the server",
                !clientMismatch.Server.Succeeded,
                $"server failure {clientMismatch.Server.FailureType}");
            check("tls mismatched client pin does not establish a mutual session",
                !(clientMismatch.Client.Succeeded && clientMismatch.Server.Succeeded),
                "session unexpectedly established");

            HandshakePair serverMismatch = await RunHandshakeAsync(
                serverIdentity, clientPin, clientIdentity, wrongPin).ConfigureAwait(false);
            check("tls mismatched server pin is rejected by the client",
                !serverMismatch.Client.Succeeded,
                $"client failure {serverMismatch.Client.FailureType}");
            check("tls mismatched server pin does not establish a mutual session",
                !(serverMismatch.Client.Succeeded && serverMismatch.Server.Succeeded),
                "session unexpectedly established");

            bool canceled = await CanceledHandshakeAsync(
                clientIdentity, serverPin).ConfigureAwait(false);
            check("tls handshake honors cancellation", canceled);
            await LanPairingChecks.RunAsync(check, root, clientIdentity, serverIdentity).ConfigureAwait(false);
        }
        finally
        {
            DeleteIdentityKey(check, "tls client key cleanup", clientIdentity);
            DeleteIdentityKey(check, "tls server key cleanup", serverIdentity);
            clientIdentity?.Dispose();
            serverIdentity?.Dispose();
            RemoveKnownCertificate(clientDirectory);
            RemoveKnownCertificate(serverDirectory);
            RemoveEmptyDirectory(clientDirectory);
            RemoveEmptyDirectory(serverDirectory);
            RemoveEmptyDirectory(root);
        }
    }

    private static async Task<HandshakePair> RunHandshakeAsync(
        X509Certificate2 serverIdentity,
        ReadOnlyMemory<byte> expectedClientPin,
        X509Certificate2 clientIdentity,
        ReadOnlyMemory<byte> expectedServerPin)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Task<HandshakeOutcome> serverTask = Task.Run(async () =>
        {
            try
            {
                using TcpClient tcp = await listener
                    .AcceptTcpClientAsync(cancellation.Token)
                    .ConfigureAwait(false);
                using NetworkStream network = tcp.GetStream();
                var accepted = await LanSecureTransport
                    .AcceptAsync(network, serverIdentity, expectedClientPin, cancellation.Token)
                    .ConfigureAwait(false);
                using SslStream ssl = accepted.Stream;
                return new HandshakeOutcome(
                    true,
                    ssl.NegotiatedApplicationProtocol.ToString(),
                    ssl.IsMutuallyAuthenticated,
                    null);
            }
            catch (OperationCanceledException)
            {
                return new HandshakeOutcome(false, string.Empty, false, nameof(OperationCanceledException));
            }
            catch (Exception exception)
            {
                return new HandshakeOutcome(false, string.Empty, false, exception.GetType().Name);
            }
        });

        HandshakeOutcome client;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellation.Token)
                .ConfigureAwait(false);
            using NetworkStream network = tcp.GetStream();
            using SslStream ssl = await LanSecureTransport
                .ConnectAsync(network, clientIdentity, expectedServerPin, cancellation.Token)
                .ConfigureAwait(false);
            client = new HandshakeOutcome(
                true,
                ssl.NegotiatedApplicationProtocol.ToString(),
                ssl.IsMutuallyAuthenticated,
                null);
        }
        catch (OperationCanceledException)
        {
            client = new HandshakeOutcome(false, string.Empty, false, nameof(OperationCanceledException));
        }
        catch (Exception exception)
        {
            client = new HandshakeOutcome(false, string.Empty, false, exception.GetType().Name);
        }

        listener.Stop();

        HandshakeOutcome server;
        try
        {
            server = await serverTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            server = new HandshakeOutcome(false, string.Empty, false, nameof(TimeoutException));
        }

        return new HandshakePair(client, server);
    }

    private static async Task<bool> CanceledHandshakeAsync(
        X509Certificate2 clientIdentity,
        ReadOnlyMemory<byte> serverPin)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            using NetworkStream network = tcp.GetStream();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Task<SslStream> handshake = LanSecureTransport
                .ConnectAsync(network, clientIdentity, serverPin, cancellation.Token);
            try
            {
                using SslStream ssl = await handshake
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void DeleteIdentityKey(
        LanTestAssertion check,
        string name,
        X509Certificate2? identity)
    {
        if (identity is null)
        {
            return;
        }

        try
        {
            using ECDsa? key = identity.GetECDsaPrivateKey();
            if (key is ECDsaCng cng)
            {
                cng.Key.Delete();
                check(name, true);
                return;
            }

            check(name, false, "identity key is not a named CNG key");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            check(name, false, $"cleanup failed {exception.GetType().Name}");
        }
    }

    private static void RemoveKnownCertificate(string directory)
    {
        try
        {
            string certificatePath = Path.Combine(directory, CertificateFileName);
            if (File.Exists(certificatePath))
            {
                File.Delete(certificatePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task ExpectFaultAsync(
        LanTestAssertion check,
        string name,
        LanWireFault expected,
        Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            check(name, false, "no exception was thrown");
        }
        catch (LanWireException exception)
        {
            check(name, exception.Fault == expected, $"fault {exception.Fault}");
        }
        catch (Exception exception)
        {
            check(name, false, $"unexpected exception {exception.GetType().Name}");
        }
    }

    private static byte[] BuildFrame(
        byte version,
        byte type,
        ulong sequence,
        byte[] payload) =>
        Header(version, type, sequence, (uint)payload.Length)
            .Concat(payload)
            .ToArray();

    private static byte[] Header(
        byte version,
        byte type,
        ulong sequence,
        uint payloadLength)
    {
        byte[] header = new byte[HeaderSize];
        header[0] = version;
        header[1] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, sizeof(uint)), payloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(6, sizeof(ulong)), sequence);
        return header;
    }

    private static ulong ReadSequence(byte[] frame) =>
        BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(6, sizeof(ulong)));

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private readonly record struct HandshakeOutcome(
        bool Succeeded,
        string Alpn,
        bool MutuallyAuthenticated,
        string? FailureType);

    private readonly record struct HandshakePair(
        HandshakeOutcome Client,
        HandshakeOutcome Server);

    private sealed class ProbeStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxReadChunk;
        private int _position;

        public ProbeStream(byte[] data, int maxReadChunk = int.MaxValue)
        {
            _data = data;
            _maxReadChunk = maxReadChunk;
        }

        public List<byte[]> Writes { get; } = new();

        public int WriteCount => Writes.Count;

        public int BytesRead => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Writes.Add(buffer.AsSpan(offset, count).ToArray());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            int remaining = _data.Length - _position;
            if (remaining == 0)
            {
                return 0;
            }

            int count = Math.Min(Math.Min(buffer.Length, remaining), _maxReadChunk);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
