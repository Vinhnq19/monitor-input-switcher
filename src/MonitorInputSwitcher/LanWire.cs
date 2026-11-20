using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

public enum LanFrameType : byte
{
    Pair = 1,
    PairAccepted = 2,
    Hello = 3,
    Ready = 4,
    Topology = 5,
    Enter = 6,
    Leave = 7,
    Return = 8,
    Input = 9,
    Ping = 10,
    Pong = 11,
    Applied = 12,
    MonitorRequest = 13,
    MonitorResult = 14
}

public readonly record struct LanFrame(LanFrameType Type, ulong Sequence, byte[] Payload);

public enum LanWireFault
{
    InvalidFrame,
    MissingOrOutOfOrder,
    Duplicate
}

public sealed class LanWireException : IOException
{
    public LanWireException(LanWireFault fault, string message)
        : base(message)
    {
        Fault = fault;
    }

    public LanWireFault Fault { get; }
}

/// <summary>
/// Owns the length-delimited frame format for one LAN TLS stream. The caller keeps
/// all writes serialized; this type deliberately retains no frame payload history.
/// </summary>
public sealed class LanWire(Stream stream)
{
    private const byte Version = 1;
    private const int HeaderSize = 14;
    private const int MaxPayloadLength = 1024;

    private readonly Stream _stream =
        stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly byte[] _readHeader = new byte[HeaderSize];
    private readonly byte[] _writeBuffer = new byte[HeaderSize + MaxPayloadLength];

    private ulong _nextReadSequence = 1;
    private ulong _nextWriteSequence = 1;

    public async ValueTask<LanFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        int firstByte = await _stream
            .ReadAsync(_readHeader.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);

        if (firstByte == 0)
        {
            return null;
        }

        await ReadExactlyAsync(
                _readHeader.AsMemory(1, HeaderSize - 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (_readHeader[0] != Version)
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame version is not supported.");
        }

        LanFrameType type = (LanFrameType)_readHeader[1];
        if (!Enum.IsDefined(type))
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame type is not defined.");
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
            _readHeader.AsSpan(2, sizeof(uint)));
        if (payloadLength > MaxPayloadLength)
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame payload length exceeds the limit.");
        }

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(
            _readHeader.AsSpan(6, sizeof(ulong)));
        if (sequence == 0 || sequence == ulong.MaxValue)
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame sequence is zero or would overflow.");
        }

        if (sequence < _nextReadSequence)
        {
            throw new LanWireException(
                LanWireFault.Duplicate,
                "The frame sequence was already accepted.");
        }

        if (sequence > _nextReadSequence)
        {
            throw new LanWireException(
                LanWireFault.MissingOrOutOfOrder,
                "The frame sequence is missing or out of order.");
        }

        byte[] payload = payloadLength == 0
            ? Array.Empty<byte>()
            : new byte[(int)payloadLength];
        if (payload.Length > 0)
        {
            await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        _nextReadSequence = sequence + 1;
        return new LanFrame(type, sequence, payload);
    }

    public ValueTask WriteAsync(
        LanFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(type))
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame type is not defined.");
        }

        if (payload.Length > MaxPayloadLength)
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame payload length exceeds the limit.");
        }

        if (_nextWriteSequence == 0 || _nextWriteSequence == ulong.MaxValue)
        {
            throw new LanWireException(
                LanWireFault.InvalidFrame,
                "The frame sequence would overflow.");
        }

        _writeBuffer[0] = Version;
        _writeBuffer[1] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(
            _writeBuffer.AsSpan(2, sizeof(uint)),
            (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(
            _writeBuffer.AsSpan(6, sizeof(ulong)),
            _nextWriteSequence);
        payload.Span.CopyTo(_writeBuffer.AsSpan(HeaderSize, payload.Length));

        return WriteCoreAsync(HeaderSize + payload.Length, cancellationToken);
    }

    private async ValueTask WriteCoreAsync(
        int frameLength,
        CancellationToken cancellationToken)
    {
        await _stream
            .WriteAsync(_writeBuffer.AsMemory(0, frameLength), cancellationToken)
            .ConfigureAwait(false);

        _nextWriteSequence++;
    }

    private async ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await _stream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new LanWireException(
                    LanWireFault.InvalidFrame,
                    "The frame is truncated.");
            }

            offset += read;
        }
    }
}
