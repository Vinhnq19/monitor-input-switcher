using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

/// <summary>Why a <see cref="LanSession"/> stopped.</summary>
public enum LanSessionFault
{
    None = 0,
    Shutdown = 1,
    Cancelled = 2,
    Timeout = 3,
    PeerClosed = 4,
    Transport = 5,
    Protocol = 6,
    Handler = 7,
    Overflow = 8
}

/// <summary>
/// The terminal state of a session. The message stays generic so the connection service can
/// reconnect without leaking peer details or I/O text.
/// </summary>
public sealed class LanSessionException(LanSessionFault fault)
    : Exception($"The LAN session stopped ({fault}).")
{
    public LanSessionFault Fault { get; } = fault;
}

/// <summary>A snapshot of the session's link health and send pressure.</summary>
public readonly record struct LanSessionStatistics(
    double? RoundTripMs,
    double? JitterMs,
    int QueueDepth,
    int PeakQueueDepth,
    long QueueOverflows,
    long SequenceGaps = 0,
    long DuplicateFrames = 0,
    long InvalidFrames = 0);

/// <summary>
/// One supervised session over an already paired <see cref="LanLink"/>. Exactly one writer
/// task drains a bounded queue into the wire, exactly one reader task validates received
/// frames and runs the application handler sequentially, and an independent heartbeat loop
/// keeps the lease alive. Any fault closes the owned link immediately so the blocked read
/// and write complete and both loops exit; <see cref="RunAsync"/> joins them.
/// </summary>
public sealed class LanSession(LanLink link) : IDisposable
{
    private const int QueueCapacity = 256;
    private const int MaxPayloadLength = 1024;
    private const int HeartbeatPayloadLength = sizeof(long);
    private const int HeartbeatIntervalMilliseconds = 250;
    private const int LeaseMilliseconds = 1000;
    private const int MaximumInputAgeMilliseconds = 200;
    private const double JitterSmoothing = 16.0;

    private readonly LanLink _link = link ?? throw new ArgumentNullException(nameof(link));

    private readonly Channel<Outbound> _sendQueue = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _metricsGate = new();

    private double? _roundTripMilliseconds;
    private double? _jitterMilliseconds;

    private long _lastReceivedTicks;
    private long _writeStarted;
    private int _peakQueueDepth;
    private long _queueOverflows;
    private long _sequenceGaps, _duplicates, _invalid;
    private int _faultCode;
    private int _started;
    private int _disposed;
    private int _linkClosed;
    private int _cancellationDisposed;
    internal bool Healthy => Volatile.Read(ref _faultCode) == 0;
    internal void Abort() => Fault(LanSessionFault.Overflow);

    /// <summary>
    /// Runs the session until cancellation, a fault or <see cref="Dispose"/>. Calling it more
    /// than once is rejected; the returned task observes every worker failure.
    /// </summary>
    public async Task RunAsync(
        Func<LanFrame, CancellationToken, ValueTask> onFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The session is already running.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            DisposeCancellation();
            throw new ObjectDisposedException(nameof(LanSession));
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);

        try
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((LanSession)state!).Fault(LanSessionFault.Cancelled),
                this);

            Volatile.Write(ref _lastReceivedTicks, Stopwatch.GetTimestamp());

            Task writer = WriteLoopAsync(linked.Token);
            Task reader = ReadLoopAsync(onFrame, linked.Token);
            Task heartbeat = HeartbeatLoopAsync(linked.Token);

            // The loops translate their own failures into a fault and never throw, so the join
            // cannot be skipped and no worker survives the session.
            await Task.WhenAll(writer, reader, heartbeat).ConfigureAwait(false);
        }
        finally
        {
            RequestShutdown();
            linked.Dispose();
            DisposeCancellation();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        LanSessionFault fault = PeekFault();
        if (fault == LanSessionFault.Cancelled)
        {
            throw new OperationCanceledException();
        }

        if (fault != LanSessionFault.None)
        {
            throw new LanSessionException(fault);
        }
    }

    /// <summary>
    /// Copies <paramref name="payload"/> into the bounded send queue without waiting. Only
    /// operational application frames are accepted; the handshake frames and Ping/Pong are
    /// reserved for the session itself.
    /// </summary>
    public bool TrySend(LanFrameType type, ReadOnlyMemory<byte> payload)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _faultCode) != (int)LanSessionFault.None)
        {
            return false;
        }

        if (!Enum.IsDefined(type) ||
            !IsApplicationType(type) ||
            payload.Length > MaxPayloadLength)
        {
            return false;
        }

        if (!TryEnqueue(type, payload.Span))
        {
            return false;
        }

        UpdatePeakQueueDepth();
        return true;
    }

    /// <summary>The latest link health and send pressure. Safe to call at any time.</summary>
    public LanSessionStatistics ReadStatistics()
    {
        double? roundTrip;
        double? jitter;

        lock (_metricsGate)
        {
            roundTrip = _roundTripMilliseconds;
            jitter = _jitterMilliseconds;
        }

        return new LanSessionStatistics(
            roundTrip,
            jitter,
            _sendQueue.Reader.Count,
            Volatile.Read(ref _peakQueueDepth),
            Interlocked.Read(ref _queueOverflows),
            Interlocked.Read(ref _sequenceGaps), Interlocked.Read(ref _duplicates), Interlocked.Read(ref _invalid));
    }

    /// <summary>
    /// Signals shutdown, closes the owned link and stops accepting frames. Cancellation and
    /// disposal may run callbacks synchronously, so it must be called via a service worker,
    /// not from a Win32 hook or WPF UI. It never waits; <see cref="RunAsync"/> performs the join.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Fault(LanSessionFault.Shutdown);
        RequestShutdown();

        // When a run exists it owns the join and disposes the cancellation source afterwards,
        // so no worker can observe a disposed source.
        if (Volatile.Read(ref _started) == 0)
        {
            DisposeCancellation();
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _sendQueue.Reader
                       .WaitToReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                while (_sendQueue.Reader.TryRead(out var frame))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (frame.Type == LanFrameType.Input &&
                        Stopwatch.GetElapsedTime(frame.Timestamp, now).TotalMilliseconds > MaximumInputAgeMilliseconds)
                    {
                        Fault(LanSessionFault.Timeout);
                        return;
                    }
                    Volatile.Write(ref _writeStarted, now);
                    await _link.Wire
                        .WriteAsync(frame.Type, frame.Payload, cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _writeStarted, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown was requested; the owner reports the reason.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fault(LanSessionFault.Transport);
        }
    }

    private async Task ReadLoopAsync(
        Func<LanFrame, CancellationToken, ValueTask> onFrame,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                LanFrame? received = await _link.Wire
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (received is null)
                {
                    Fault(LanSessionFault.PeerClosed);
                    return;
                }

                LanFrame frame = received.Value;

                switch (frame.Type)
                {
                    case LanFrameType.Pair:
                    case LanFrameType.PairAccepted:
                    case LanFrameType.Hello:
                    case LanFrameType.Ready:
                        Fault(LanSessionFault.Protocol);
                        return;

                    case LanFrameType.Ping:
                        if (frame.Payload.Length != HeartbeatPayloadLength)
                        {
                            Fault(LanSessionFault.Protocol);
                            return;
                        }

                        MarkFrameReceived();

                        // The peer's tick is only echoed; no remote value is retained.
                        if (!TryEnqueue(LanFrameType.Pong, frame.Payload))
                        {
                            return;
                        }

                        break;

                    case LanFrameType.Pong:
                        if (frame.Payload.Length != HeartbeatPayloadLength)
                        {
                            Fault(LanSessionFault.Protocol);
                            return;
                        }

                        MarkFrameReceived();
                        RecordRoundTrip(frame.Payload);
                        break;

                    default:
                        if (!IsApplicationType(frame.Type))
                        {
                            Fault(LanSessionFault.Protocol);
                            return;
                        }

                        MarkFrameReceived();

                        try
                        {
                            await onFrame(frame, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            Fault(LanSessionFault.Handler);
                            return;
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown was requested; the owner reports the reason.
        }
        catch (LanWireException exception)
        {
            switch (exception.Fault)
            {
                case LanWireFault.Duplicate: Interlocked.Increment(ref _duplicates); break;
                case LanWireFault.MissingOrOutOfOrder: Interlocked.Increment(ref _sequenceGaps); break;
                default: Interlocked.Increment(ref _invalid); break;
            }
            Fault(LanSessionFault.Protocol);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fault(LanSessionFault.Transport);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        byte[] ping = new byte[HeartbeatPayloadLength];
        TimeSpan interval = TimeSpan.FromMilliseconds(HeartbeatIntervalMilliseconds);

        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                long writeStarted = Volatile.Read(ref _writeStarted);
                if (writeStarted != 0 &&
                    Stopwatch.GetElapsedTime(writeStarted, now).TotalMilliseconds > MaximumInputAgeMilliseconds)
                {
                    Fault(LanSessionFault.Timeout);
                    return;
                }
                if (now - Volatile.Read(ref _lastReceivedTicks) >=
                    Stopwatch.Frequency * (LeaseMilliseconds / 1000.0))
                {
                    Fault(LanSessionFault.Timeout);
                    return;
                }

                BinaryPrimitives.WriteInt64LittleEndian(ping, now);
                if (!TryEnqueue(LanFrameType.Ping, ping))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown was requested; the owner reports the reason.
        }
    }

    private bool TryEnqueue(LanFrameType type, ReadOnlySpan<byte> payload)
    {
        if (Volatile.Read(ref _faultCode) != (int)LanSessionFault.None)
        {
            return false;
        }

        // The wire writes from its own reusable buffer, but the queue outlives this call, so
        // the payload must be owned here.
        byte[] copy = payload.ToArray();
        if (_sendQueue.Writer.TryWrite(new Outbound(type, copy, Stopwatch.GetTimestamp())))
        {
            return true;
        }

        Interlocked.Increment(ref _queueOverflows);
        Fault(LanSessionFault.Overflow);
        return false;
    }

    private void UpdatePeakQueueDepth()
    {
        int depth = _sendQueue.Reader.Count;
        int peak = Volatile.Read(ref _peakQueueDepth);

        while (depth > peak)
        {
            int observed = Interlocked.CompareExchange(ref _peakQueueDepth, depth, peak);
            if (observed == peak)
            {
                return;
            }

            peak = observed;
        }
    }

    private void MarkFrameReceived() =>
        Volatile.Write(ref _lastReceivedTicks, Stopwatch.GetTimestamp());

    private void RecordRoundTrip(ReadOnlySpan<byte> payload)
    {
        long now = Stopwatch.GetTimestamp();
        long echoed = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (echoed <= 0 || echoed > now)
        {
            // A missing or future tick carries no measurement and is ignored, never stored.
            return;
        }

        long elapsed = now - echoed;
        if (elapsed > Stopwatch.Frequency * (LeaseMilliseconds / 1000.0))
        {
            // Arbitrary remote ticks must not produce implausibly long telemetry.
            return;
        }

        double milliseconds = elapsed * 1000.0 / Stopwatch.Frequency;

        lock (_metricsGate)
        {
            if (_roundTripMilliseconds is double previous)
            {
                double change = Math.Abs(milliseconds - previous);
                double smoothed = _jitterMilliseconds is double jitter ? jitter : 0.0;
                _jitterMilliseconds = smoothed + (change - smoothed) / JitterSmoothing;
            }
            else
            {
                _jitterMilliseconds = 0.0;
            }

            _roundTripMilliseconds = milliseconds;
        }
    }

    private void Fault(LanSessionFault fault)
    {
        if (fault == LanSessionFault.None)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _faultCode,
                (int)fault,
                (int)LanSessionFault.None) == (int)LanSessionFault.None)
        {
            RequestShutdown();
        }
    }

    private LanSessionFault PeekFault() => (LanSessionFault)Volatile.Read(ref _faultCode);

    /// <summary>
    /// Idempotent teardown: closing the link wakes the blocked read and write, completing the
    /// queue stops the writer and cancelling the source stops the heartbeat.
    /// </summary>
    private void RequestShutdown()
    {
        CloseLink();
        _sendQueue.Writer.TryComplete();

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already completed.
        }
    }

    private void CloseLink()
    {
        if (Interlocked.Exchange(ref _linkClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _link.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Releasing a failed link must not replace the reported fault.
        }
    }

    private void DisposeCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationDisposed, 1) != 0)
        {
            return;
        }

        _shutdown.Dispose();
    }

    private static bool IsApplicationType(LanFrameType type) =>
        type is LanFrameType.Topology
            or LanFrameType.Enter
            or LanFrameType.Leave
            or LanFrameType.Return
            or LanFrameType.Input
            or LanFrameType.Applied
            or LanFrameType.MonitorRequest
            or LanFrameType.MonitorResult;

    private readonly record struct Outbound(LanFrameType Type, byte[] Payload, long Timestamp);
}
