using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MonitorInputSwitcher;

internal enum LanRole { Pc, Laptop }
internal enum LanState { Disconnected, Connecting, Connected, Reconnecting }
internal enum LanError { None, NoPrivateRoute, InvalidSubnet, NotPaired, Refused, Timeout, Authentication, Transport }

internal sealed record LanConnection(LanState State, LanError Error = LanError.None, string? Name = null, IPAddress? Address = null);

internal sealed class LanPeerService(
    string directory, Func<LanFrame, CancellationToken, ValueTask> receive, Action linkEnded) : IAsyncDisposable
{
    private readonly LanTrustStore _trust = new(directory);
    private readonly SemaphoreSlim _lifecycle = new(1);
    private CancellationTokenSource? _stop;
    private Task? _run;
    private LanPairingInvite? _invite;
    private LanSession? _session;
    private LanConnection _status = new(LanState.Disconnected);
    private LanRole _role;
    private IPAddress? _manual;
    private bool _auto;
    private bool _disposed;
    private long _lostAt, _reconnectedMilliseconds = -1;
    private LanSessionStatistics _completedStats;
    private readonly object _statisticsGate = new();
    private readonly Queue<(DateTimeOffset Time, LanError Error)> _errors = new();

    public LanConnection Status => Volatile.Read(ref _status);
    internal LanSession? CurrentSession => Volatile.Read(ref _session);
    public LanSessionStatistics Statistics
    {
        get
        {
            lock (_statisticsGate)
            {
                var running = _session?.ReadStatistics() ?? default;
                return running with
                {
                    PeakQueueDepth = Math.Max(running.PeakQueueDepth, _completedStats.PeakQueueDepth),
                    QueueOverflows = running.QueueOverflows + _completedStats.QueueOverflows,
                    SequenceGaps = running.SequenceGaps + _completedStats.SequenceGaps,
                    DuplicateFrames = running.DuplicateFrames + _completedStats.DuplicateFrames,
                    InvalidFrames = running.InvalidFrames + _completedStats.InvalidFrames
                };
            }
        }
    }
    public long ReconnectMilliseconds => Interlocked.Read(ref _reconnectedMilliseconds);
    public (DateTimeOffset Time, LanError Error)[] ReadErrors()
    {
        lock (_statisticsGate)
        {
            return _errors.ToArray();
        }
    }

    public Task StartAsync(LanRole role, IPAddress? manual, bool auto) => Exclusive(async () =>
    {
        await StopCore().ConfigureAwait(false);
        _role = role;
        _manual = manual;
        _auto = auto;
        StartCore();
    });

    public Task StopAsync() => Exclusive(StopCore);

    public Task<string> InviteAsync() => Exclusive(async () =>
    {
        if (_role != LanRole.Laptop || _run is not { IsCompleted: false })
        {
            throw new InvalidOperationException();
        }

        using var identity = await Task.Run(() => LanIdentity.Open(directory)).ConfigureAwait(false);
        if (_trust.Load() is not null)
        {
            throw new InvalidOperationException();
        }

        var invite = new LanPairingInvite(LanIdentity.Fingerprint(identity));
        Interlocked.Exchange(ref _invite, invite)?.Invalidate();
        return invite.Code;
    });

    public Task PairAsync(string code) => Exclusive(async () =>
    {
        if (_role != LanRole.Pc || !LanPairingInvite.TryParse(code, out var pin, out var secret))
        {
            throw new AuthenticationException();
        }

        CryptographicOperations.ZeroMemory(secret);
        await StopCore().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var identity = LanIdentity.Open(directory);
            var routes = GetRoutes();
            var address = await FindAddress(routes, pin, _manual, timeout.Token).ConfigureAwait(false);
            using var tcp = await LanLink.OpenClientAsync(address, routes, timeout.Token).ConfigureAwait(false);
            await LanPairingChannel.PairAsPcAsync(tcp.GetStream(), identity, code, _trust, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            StartCore();
        }
    });

    public Task RevokeAsync() => Exclusive(async () =>
    {
        await StopCore().ConfigureAwait(false);
        _trust.Clear();
    });

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCore().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task Exclusive(Func<Task> operation) => Exclusive(async () =>
    {
        await operation().ConfigureAwait(false);
        return true;
    });

    private Task<T> Exclusive<T>(Func<Task<T>> operation) => Task.Run(async () =>
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    });

    private void StartCore()
    {
        var stop = new CancellationTokenSource();
        _stop = stop;
        _run = Task.Run(() => Run(stop.Token));
    }

    private async Task StopCore()
    {
        Interlocked.Exchange(ref _invite, null)?.Invalidate();
        if (_stop is { } stop)
        {
            stop.Cancel();
            await _run!.ConfigureAwait(false);
            stop.Dispose();
            _stop = null;
            _run = null;
        }
        Publish(LanState.Disconnected);
    }

    private async Task Run(CancellationToken stop)
    {
        try
        {
            using var identity = LanIdentity.Open(directory);
            var retry = 250;
            var reconnecting = false;
            while (!stop.IsCancellationRequested)
            {
                Publish(reconnecting ? LanState.Reconnecting : LanState.Connecting);
                try
                {
                    var routes = GetRoutes();
                    using var generation = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    var watcher = WatchRoutes(routes, generation);
                    try
                    {
                        if (_role == LanRole.Pc)
                        {
                            await RunPc(routes, identity, generation.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await RunLaptop(routes, identity, generation.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        generation.Cancel();
                        await watcher.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    bool retrying = _auto || _role == LanRole.Laptop && ex is NetworkFailure { Error: LanError.NoPrivateRoute };
                    Publish(retrying ? LanState.Reconnecting : LanState.Disconnected, Classify(ex));
                    if (!retrying || ex is NetworkFailure { Error: LanError.NotPaired })
                    {
                        Publish(LanState.Disconnected, Classify(ex));
                        return;
                    }
                }

                reconnecting = true;
                await Task.Delay(retry, stop).ConfigureAwait(false);
                retry = Math.Min(3000, retry * 2);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Publish(LanState.Disconnected, Classify(ex));
        }
    }

    private async Task RunPc(IReadOnlyList<LanPrivateRoute> routes, X509Certificate2 identity, CancellationToken stop)
    {
        var trust = _trust.Load() ?? throw new NetworkFailure(LanError.NotPaired);
        var pin = trust.ReadPin();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        TcpClient? tcp = null;
        LanError? manualError = null;
        if (_manual is { } manual)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(900));
            try
            {
                var address = await FindAddress(routes, pin, manual, attempt.Token).ConfigureAwait(false);
                tcp = await LanLink.OpenClientAsync(address, routes, attempt.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!deadline.IsCancellationRequested &&
                ex is SocketException or OperationCanceledException or NetworkFailure { Error: LanError.InvalidSubnet })
            {
                manualError = Classify(ex);
            }
        }
        if (tcp is null)
        {
            var discovered = await LanDiscovery.FindAsync(routes, pin, deadline.Token).ConfigureAwait(false)
                ?? throw new NetworkFailure(manualError ?? LanError.Timeout);
            tcp = await LanLink.OpenClientAsync(discovered, routes, deadline.Token).ConfigureAwait(false);
        }

        using (tcp)
        using (var link = await LanLink.ConnectPairedAsync(tcp, identity, _trust, Environment.MachineName, deadline.Token).ConfigureAwait(false))
        {
            await RunSession(link, stop).ConfigureAwait(false);
        }
    }

    private async Task RunLaptop(IReadOnlyList<LanPrivateRoute> routes, X509Certificate2 identity, CancellationToken stop)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var admission = new SemaphoreSlim(1);
        var listeners = new List<TcpListener>();
        var workers = new List<Task>();
        try
        {
            foreach (var route in routes.DistinctBy(route => route.LocalAddress))
            {
                try
                {
                    var listener = LanLink.StartListener(route);
                    listeners.Add(listener);
                    workers.Add(Accept(listener, route, identity, admission, listening.Token));
                }
                catch (SocketException) when (routes.Count > 1)
                {
                    // Another eligible local address may still be usable.
                }
            }
            if (listeners.Count == 0)
            {
                throw new NetworkFailure(LanError.Refused);
            }
            workers.Add(Respond(routes, identity, listening.Token));
            await Task.WhenAny(workers.Take(listeners.Count)).ConfigureAwait(false);
        }
        finally
        {
            listening.Cancel();
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private async Task Accept(TcpListener listener, LanPrivateRoute route, X509Certificate2 identity, SemaphoreSlim admission, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var tcp = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            if (!LanLink.IsEligibleClient(tcp, route) || !admission.Wait(0))
            {
                continue;
            }

            try
            {
                tcp.NoDelay = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                if (_trust.Load() is null)
                {
                    var invite = Volatile.Read(ref _invite) ?? throw new NetworkFailure(LanError.NotPaired);
                    await LanPairingChannel.PairAsLaptopAsync(tcp.GetStream(), identity, invite, _trust, deadline.Token).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref _invite, null, invite);
                }
                else
                {
                    using var link = await LanLink.AcceptPairedAsync(tcp, route, identity, _trust, Environment.MachineName, deadline.Token).ConfigureAwait(false);
                    await RunSession(link, stop).ConfigureAwait(false);
                    if (!_auto)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                Publish(LanState.Connecting, Classify(ex));
                if (ex is LanSessionException && !_auto)
                {
                    return;
                }
            }
            finally
            {
                admission.Release();
            }
        }
    }

    private static async Task Respond(IReadOnlyList<LanPrivateRoute> routes, X509Certificate2 identity, CancellationToken stop)
    {
        try
        {
            await LanDiscovery.RespondAsync(routes, LanIdentity.Fingerprint(identity), stop).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Manual TCP pairing can still work when multicast is blocked.
        }
    }

    private async Task RunSession(LanLink link, CancellationToken stop)
    {
        using var session = new LanSession(link);
        Volatile.Write(ref _session, session);
        if (_lostAt != 0)
        {
            Interlocked.Exchange(ref _reconnectedMilliseconds, (long)Stopwatch.GetElapsedTime(_lostAt).TotalMilliseconds);
        }
        Volatile.Write(ref _status, new LanConnection(LanState.Connected, Name: link.PeerName, Address: link.PeerAddress));
        try
        {
            await session.RunAsync(receive, stop).ConfigureAwait(false);
        }
        finally
        {
            lock (_statisticsGate)
            {
                _completedStats = Statistics with { QueueDepth = 0 };
                Volatile.Write(ref _session, null);
            }
            _lostAt = Stopwatch.GetTimestamp();
            Publish(LanState.Reconnecting);
            linkEnded();
        }
    }

    private static async Task WatchRoutes(IReadOnlyList<LanPrivateRoute> routes, CancellationTokenSource generation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            var original = routes.ToHashSet();
            while (await timer.WaitForNextTickAsync(generation.Token).ConfigureAwait(false))
            {
                if (!original.SetEquals(LanPrivateNetwork.GetRoutes()))
                {
                    generation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
        }
    }

    private static IReadOnlyList<LanPrivateRoute> GetRoutes()
    {
        var routes = LanPrivateNetwork.GetRoutes();
        return routes.Count > 0 ? routes : throw new NetworkFailure(LanError.NoPrivateRoute);
    }

    private static async Task<IPAddress> FindAddress(IReadOnlyList<LanPrivateRoute> routes, byte[] pin, IPAddress? manual, CancellationToken stop)
    {
        if (manual is not null)
        {
            return routes.Any(route => route.ContainsPeer(manual)) ? manual : throw new NetworkFailure(LanError.InvalidSubnet);
        }
        return await LanDiscovery.FindAsync(routes, pin, stop).ConfigureAwait(false)
            ?? throw new NetworkFailure(LanError.Timeout);
    }

    private void Publish(LanState state, LanError error = LanError.None)
    {
        if (error != LanError.None)
        {
            lock (_statisticsGate)
            {
                if (_errors.Count == 16)
                {
                    _errors.Dequeue();
                }
                _errors.Enqueue((DateTimeOffset.UtcNow, error));
            }
        }
        Volatile.Write(ref _status, new LanConnection(state, error));
    }

    internal static LanError Classify(Exception ex) => ex switch
    {
        NetworkFailure failure => failure.Error,
        AuthenticationException => LanError.Authentication,
        OperationCanceledException or LanSessionException { Fault: LanSessionFault.Timeout } => LanError.Timeout,
        SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.AddressAlreadyInUse } => LanError.Refused,
        IOException or SocketException => LanError.Transport,
        _ => LanError.Transport
    };

    private sealed class NetworkFailure(LanError error) : Exception
    {
        public LanError Error { get; } = error;
    }
}
