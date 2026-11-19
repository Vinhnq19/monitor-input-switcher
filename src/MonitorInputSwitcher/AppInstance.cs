using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorInputSwitcher;

public enum AppCommand : byte
{
    Open,
    Tray,
    Pc,
    Laptop
}

public sealed class AppInstance : IDisposable
{
    private const byte AckByte = 1;
    private const int ClientReadTimeoutMilliseconds = 5000;
    private const string PipeNamePrefix = "MonitorInputSwitcher";
    private const string MutexNamePrefix = @"Local\MonitorInputSwitcher.";

    private readonly object _gate = new();
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly int _mutexOwnerThreadId;

    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private Action<AppCommand>? _receive;
    private Action<Exception>? _report;
    private bool _ownsMutex;
    private volatile bool _isPrimary;
    private volatile bool _disposed;

    public bool IsPrimary => _isPrimary;

    public AppInstance()
    {
        string sid = GetCurrentUserSid();
        int sessionId = GetCurrentSessionId();

        _pipeName = $"{PipeNamePrefix}.{sid}.{sessionId}";
        _mutexOwnerThreadId = Environment.CurrentManagedThreadId;
        _mutex = new Mutex(initiallyOwned: false, name: MutexNamePrefix + sid);

        try
        {
            _ownsMutex = _mutex.WaitOne(0);
            _isPrimary = _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            _isPrimary = true;
        }
        catch
        {
            _mutex.Dispose();
            throw;
        }
    }

    public static bool TryParseArgs(string[] args, out AppCommand command)
    {
        command = AppCommand.Open;

        if (args == null)
        {
            return false;
        }

        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length != 1)
        {
            return false;
        }

        string? argument = args[0];
        if (string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase))
        {
            command = AppCommand.Tray;
            return true;
        }

        if (string.Equals(argument, "--pc", StringComparison.OrdinalIgnoreCase))
        {
            command = AppCommand.Pc;
            return true;
        }

        if (string.Equals(argument, "--laptop", StringComparison.OrdinalIgnoreCase))
        {
            command = AppCommand.Laptop;
            return true;
        }

        return false;
    }

    public void StartListening(Action<AppCommand> receive, Action<Exception>? report = null)
    {
        if (receive == null)
        {
            throw new ArgumentNullException(nameof(receive));
        }

        lock (_gate)
        {
            if (_disposed || !_isPrimary || _listenTask != null)
            {
                return;
            }

            _receive = receive;
            _report = report;

            var cts = new CancellationTokenSource();
            _listenCts = cts;
            _listenTask = Task.Run(() => ListenLoopAsync(cts));
        }
    }

    public bool TryForward(AppCommand command, int timeoutMilliseconds = 1500)
    {
        if (_disposed || _isPrimary || timeoutMilliseconds <= 0 || !IsValidCommand(command))
        {
            return false;
        }

        try
        {
            return ForwardAsync(command, timeoutMilliseconds)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ForwardAsync(AppCommand command, int timeoutMilliseconds)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            CancellationToken token = timeoutCts.Token;

            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await client.ConnectAsync(token).ConfigureAwait(false);

            byte[] request = { (byte)command };
            await client.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
            await client.FlushAsync(token).ConfigureAwait(false);

            byte[] ack = new byte[1];
            int read = await client.ReadAsync(ack, 0, ack.Length, token).ConfigureAwait(false);
            return read == 1 && ack[0] == AckByte;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;

                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    using (token.Register(() => DisposeServer(server)))
                    {
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    _ = HandleClientAsync(server, token);
                    server = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    Report(ex);

                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Report(ex);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Report(ex);
                    }

                    break;
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Report(ex);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_listenCts, cts))
                {
                    _listenCts = null;
                    _listenTask = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken stopToken)
    {
        using (server)
        {
            CancellationTokenSource? timeoutCts = null;

            try
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                timeoutCts.CancelAfter(ClientReadTimeoutMilliseconds);
                CancellationToken clientToken = timeoutCts.Token;

                using (clientToken.Register(() => DisposeServer(server)))
                {
                    byte[] request = new byte[1];
                    int read = await server.ReadAsync(request, 0, request.Length, clientToken).ConfigureAwait(false);

                    if (read != 1 || !TryConvertCommand(request[0], out AppCommand command))
                    {
                        return;
                    }

                    if (clientToken.IsCancellationRequested)
                    {
                        return;
                    }

                    Action<AppCommand>? receive = _receive;
                    if (receive == null)
                    {
                        return;
                    }

                    await Task.Run(() => receive(command), CancellationToken.None).ConfigureAwait(false);

                    if (clientToken.IsCancellationRequested)
                    {
                        return;
                    }

                    byte[] ack = { AckByte };
                    await server.WriteAsync(ack, 0, ack.Length, clientToken).ConfigureAwait(false);
                    await server.FlushAsync(clientToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                if (!stopToken.IsCancellationRequested)
                {
                    Report(ex);
                }
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }
    }

    private static void DisposeServer(NamedPipeServerStream? server)
    {
        try
        {
            server?.Dispose();
        }
        catch
        {
        }
    }

    private static bool IsValidCommand(AppCommand command)
    {
        switch (command)
        {
            case AppCommand.Open:
            case AppCommand.Tray:
            case AppCommand.Pc:
            case AppCommand.Laptop:
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertCommand(byte value, out AppCommand command)
    {
        switch (value)
        {
            case (byte)AppCommand.Open:
                command = AppCommand.Open;
                return true;
            case (byte)AppCommand.Tray:
                command = AppCommand.Tray;
                return true;
            case (byte)AppCommand.Pc:
                command = AppCommand.Pc;
                return true;
            case (byte)AppCommand.Laptop:
                command = AppCommand.Laptop;
                return true;
            default:
                command = AppCommand.Open;
                return false;
        }
    }

    private void Report(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        Action<Exception>? report = _report;
        if (report == null)
        {
            return;
        }

        try
        {
            report(exception);
        }
        catch
        {
        }
    }

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;

        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        }

        return sid;
    }

    private static int GetCurrentSessionId()
    {
        using Process process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    public void Dispose()
    {
        CancellationTokenSource? listenCts;
        bool releaseMutex;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isPrimary = false;
            _receive = null;
            _report = null;

            listenCts = _listenCts;
            _listenCts = null;
            _listenTask = null;

            releaseMutex = _ownsMutex &&
                _mutexOwnerThreadId == Environment.CurrentManagedThreadId;
            _ownsMutex = false;
        }

        if (listenCts != null)
        {
            try
            {
                listenCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (releaseMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            _mutex.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
