using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MonitorInputSwitcher;

internal enum LanInputStatus { Local, Switching, Remote, Receiving, WindowsBlocked, CaptureFailed, Overloaded }

// A trailing mouse hook can arrive after the last Raw Input packet when the user stops.
// Only continuing physical motion without any raw packets indicates capture loss.
internal struct RawMouseWatchdog
{
    private long _firstMove, _lastMove;
    private const int MissingMilliseconds = 180;

    public void ObserveMove(long now)
    {
        if (_lastMove == 0 || Stopwatch.GetElapsedTime(_lastMove, now).TotalMilliseconds >= MissingMilliseconds)
        {
            _firstMove = now;
        }
        _lastMove = now;
    }

    public readonly bool MissingRaw => _lastMove != 0 &&
        Stopwatch.GetElapsedTime(_firstMove, _lastMove).TotalMilliseconds >= MissingMilliseconds;
}

internal sealed class LanInputController : IAsyncDisposable
{
    private readonly LanPeerService _peer;
    private readonly Channel<Emission> _outbound = Channel.CreateBounded<Emission>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _sender;
    private readonly bool[] _physical = new bool[256];
    private readonly bool[] _swallowed = new bool[256];
    private readonly HashSet<(int Code, int Flags)> _down = [];
    private readonly HashSet<int> _buttons = [];
    private readonly List<double> _delivery = [];
    private LanSharingOptions _options = new();
    private LanScreen[] _screens = [];
    private Rectangle[] _rects = [];
    private LanScreen? _selected;
    private DispatcherTimer? _timer;
    private WindowsLanInput? _native;
    private LanSession? _session;
    private LanSession? _overflowedSession;
    private bool _peerReady;
    private bool _peerAllowsEdge;
    private byte? _advertised;
    private bool _defaultDesktop;
    private bool _fullScreen;
    private bool _armed = true;
    private bool _pause;
    private bool _captureError;
    private LanEdge _laptopEdge;
    private Point _point;
    private int _keyCount, _mouseButtons, _push, _pending, _active, _next, _pendingRatio;
    private long _enterTick, _retryRelease, _lastAck, _lastSample, _nextDesktopCheck;
    private RawMouseWatchdog _rawWatchdog;
    private TaskCompletionSource<bool>? _manualRequest;
    private long _manualTick;
    private int _state;
    private long _coalesced, _overflows;
    private double[] _latency = [];

    public LanInputController(LanPeerService peer)
    {
        _peer = peer;
        var thread = new Thread(ThreadMain) { IsBackground = true, Name = "LAN input (Win32)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _sender = Task.Run(SendLoop);
    }

    public LanInputStatus Status => (LanInputStatus)Volatile.Read(ref _state);
    public double[] Delivery => Volatile.Read(ref _latency);
    public long Coalesced => Interlocked.Read(ref _coalesced);
    public long Overflows => Interlocked.Read(ref _overflows);
    public event Action<int>? Hotkey;
    public event Action<LanSession, LanFrame>? MonitorMessage;

    public async Task Configure(LanSharingOptions options) =>
        await Dispatch(() =>
        {
            bool captureChanged = _options.Enabled != options.Enabled || _options.Role != options.Role;
            bool layoutChanged = _options.Edge != options.Edge || _options.Screen != options.Screen ||
                _options.KeyboardFollows != options.KeyboardFollows;
            if (captureChanged || layoutChanged)
            {
                Release(notify: true);
            }
            _options = options;
            _pause = false;
            if (captureChanged)
            {
                _native?.Dispose();
                _native = null;
                _timer?.Stop();
                _captureError = false;
                Array.Clear(_physical);
                Array.Clear(_swallowed);
                _keyCount = _mouseButtons = 0;
                if (options.Enabled)
                {
                    try
                    {
                        SyncPhysical();
                        _native = new WindowsLanInput(CaptureKey, CaptureMouse, Message);
                        _timer?.Start();
                    }
                    catch (Win32Exception)
                    {
                        _captureError = true;
                        SetStatus(LanInputStatus.CaptureFailed);
                    }
                }
            }
            _defaultDesktop = options.Enabled && WindowsLanInput.IsDefaultDesktop();
            UpdateSelectedScreen();
        }).ConfigureAwait(false);

    public Task Refresh(LanScreen[] screens) => Dispatch(() =>
    {
        _screens = screens;
        _rects = screens.Select(screen => screen.Bounds).ToArray();
        UpdateSelectedScreen();
    });
    public Task ChoosePc() => Dispatch(() => Release(notify: true));

    public async Task<bool> ChooseLaptop()
    {
        var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatch(() =>
        {
            if (_active != 0)
            {
                request.SetResult(true);
                return;
            }
            if (_options.Role == LanRole.Laptop || !Available || !_peerReady || _selected is null)
            {
                request.SetResult(false);
                return;
            }
            _manualRequest?.TrySetResult(false);
            _manualRequest = request;
            _manualTick = Stopwatch.GetTimestamp();
            RequestEnter(automatic: false);
        }).ConfigureAwait(false);
        return await request.Task.ConfigureAwait(false);
    }

    public Task Suspend() => Dispatch(() => Release(notify: true));

    public Task Emergency() => Dispatch(EmergencyCore);

    public Task SendMonitor(LanSession session, LanFrameType type, byte[] body) =>
        Dispatch(() =>
        {
            if (ReferenceEquals(session, _session))
            {
                Queue(type, body);
            }
        });

    public async ValueTask Receive(LanFrame frame, CancellationToken stop)
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() => ReceiveCore(frame), DispatcherPriority.Input, stop).Task.ConfigureAwait(false);
    }

    public void LinkEnded()
    {
        if (_ready.Task.IsCompletedSuccessfully)
        {
            _ready.Task.Result.BeginInvoke(DispatcherPriority.Send, () =>
            {
                if (!ReferenceEquals(_session, _peer.CurrentSession))
                {
                    ResetSession(_peer.CurrentSession);
                }
            });
        }
    }

    private async Task Dispatch(Action action)
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(action, DispatcherPriority.Input).Task.ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer = timer;
        timer.Tick += (_, _) => Check();
        _ready.SetResult(dispatcher);
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            timer.Stop();
            _native?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            Release(notify: true);
            _native?.Dispose();
            _native = null;
            _outbound.Writer.TryComplete();
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }, DispatcherPriority.Send).Task.ConfigureAwait(false);
        await _sender.ConfigureAwait(false);
    }

    private void Check()
    {
        var session = _peer.CurrentSession;
        if (session?.Healthy != true)
        {
            session = null;
        }
        if (!ReferenceEquals(_session, session))
        {
            ResetSession(session);
        }
        if (_native is null)
        {
            SetStatus(_captureError ? LanInputStatus.CaptureFailed : LanInputStatus.Local);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now >= _nextDesktopCheck)
        {
            _nextDesktopCheck = now + Stopwatch.Frequency / 10;
            bool onDefault = WindowsLanInput.IsDefaultDesktop();
            if (onDefault && !_defaultDesktop)
            {
                SyncPhysical();
            }
            _defaultDesktop = onDefault;
            _fullScreen = _options.PauseFullScreen && _defaultDesktop && WindowsLanInput.IsFullScreen();
            Advertise();
            if (!_defaultDesktop || _fullScreen)
            {
                Release(notify: true);
            }
        }
        if ((_active != 0 || _pending != 0) && session?.InputResponsive != true ||
            _pending != 0 && Stopwatch.GetElapsedTime(_enterTick, now).TotalMilliseconds >= 350)
        {
            Release(notify: true);
        }
        if (_manualRequest is not null && _pending == 0)
        {
            if (Stopwatch.GetElapsedTime(_manualTick, now).TotalSeconds >= 1)
            {
                CompleteManual(false);
            }
            else
            {
                RequestEnter(automatic: false);
            }
        }
        if (_options.Role == LanRole.Pc && _active != 0 && _rawWatchdog.MissingRaw)
        {
            EmergencyCore();
        }
        if (_active == 0 && now >= _retryRelease && (_down.Count != 0 || _buttons.Count != 0) && _defaultDesktop)
        {
            ReleaseInjected();
        }
    }

    private void ResetSession(LanSession? session)
    {
        Release(notify: false);
        _session = session;
        _peerReady = false;
        _peerAllowsEdge = false;
        _advertised = null;
        _lastSample = _lastAck = 0;
        Advertise();
    }

    private void UpdateSelectedScreen()
    {
        var selected = LanScreens.Pick(_screens, _options.Screen, _options.Role);
        if (_options.Role == LanRole.Laptop && _selected != selected)
        {
            Release(notify: true);
        }
        _selected = selected;
        Advertise();
    }

    private bool Available => _options.Enabled && !_pause && _defaultDesktop && !_fullScreen && _native is not null;

    private void Advertise()
    {
        if (_session is null)
        {
            return;
        }
        byte availability = Available && _selected is not null
            ? (byte)(_options.EdgeSwitch && !_options.LockCursor ? 3 : 1) : (byte)0;
        if (availability != _advertised)
        {
            _advertised = availability;
            Queue(LanFrameType.Topology, [availability]);
        }
    }

    private bool CaptureKey(int message, WindowsLanInput.KeyHook key)
    {
        int vk = (int)key.VirtualKey;
        if (vk is < 1 or > 255)
        {
            return false;
        }
        bool up = message is 0x101 or 0x105;
        bool repeat = _physical[vk];
        if (repeat == up)
        {
            _physical[vk] = !up;
            _keyCount += up ? -1 : 1;
        }
        bool swallowed = _swallowed[vk];
        if (up)
        {
            _swallowed[vk] = false;
        }
        if (_options.Role == LanRole.Laptop)
        {
            if (_active != 0)
            {
                DeferLocalOverride();
            }
        }
        uint mods = Modifiers();
        int chord = !up ? FindChord(mods, key.VirtualKey) : -1;
        if (chord >= 0)
        {
            if (!repeat)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    if (chord == 3)
                    {
                        EmergencyCore();
                    }
                    Hotkey?.Invoke(chord);
                });
            }
            _swallowed[vk] = true;
            return true;
        }

        if (_options.Role != LanRole.Pc || !_options.KeyboardFollows)
        {
            return swallowed;
        }
        if (!up && _active != 0 && (vk == 0x2E && (mods & 3) == 3 || vk == 0x4C && (mods & 8) != 0))
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                EmergencyCore();
                if (vk == 0x4C)
                {
                    WindowsLanInput.Lock();
                }
            });
            _swallowed[vk] = vk == 0x4C;
            return vk == 0x4C;
        }
        if (_active == 0 || !CanForward || !Available)
        {
            return swallowed;
        }
        if (up && !swallowed)
        {
            return false;
        }

        bool exceptional = vk is 0x13 or 0x2C;
        int scan = exceptional ? vk : (int)key.Scan;
        if (scan is < 1 or > 255)
        {
            return swallowed;
        }
        int flags = (up ? 2 : 0) | (exceptional ? 4 : ((int)key.Flags & 1));
        bool queued = QueueInput(LanInputKind.Key, scan, flags);
        if (queued && !up)
        {
            _swallowed[vk] = true;
        }
        return swallowed || queued;
    }

    private bool CaptureMouse(int message, WindowsLanInput.MouseHook mouse)
    {
        if (message == 0x200)
        {
            if (_active == 0)
            {
                _point = mouse.Point;
            }
            else if (_options.Role == LanRole.Pc)
            {
                _rawWatchdog.ObserveMove(Stopwatch.GetTimestamp());
            }
        }
        else
        {
            int button = message switch
            {
                0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2,
                0x20B or 0x20C => (mouse.Data >> 16) == 1 ? 3 : 4, _ => -1
            };
            if (button >= 0)
            {
                if (message is 0x201 or 0x204 or 0x207 or 0x20B)
                {
                    _mouseButtons |= 1 << button;
                }
                else
                {
                    _mouseButtons &= ~(1 << button);
                }
            }
        }
        if (_active != 0 && _options.Role == LanRole.Laptop)
        {
            DeferLocalOverride();
        }
        return _active != 0 && _options.Role == LanRole.Pc && CanForward && Available;
    }

    private void DeferLocalOverride()
    {
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, () => Release(notify: true));
    }

    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WindowsLanInput.RawMessage && _options.Role == LanRole.Pc &&
            WindowsLanInput.TryReadMouse(lParam, out var mouse))
        {
            Raw(mouse);
        }
        else if (message == WindowsLanInput.SessionMessage && (int)wParam is 0x7 or 0x2 or 0x4)
        {
            _defaultDesktop = false;
            Release(notify: true);
            Advertise();
        }
        return 0;
    }

    private void Raw(WindowsLanInput.RawMouse mouse)
    {
        _rawWatchdog = default;
        if (_options.Role != LanRole.Pc || !CanForward || !Available)
        {
            return;
        }
        if (_active != 0)
        {
            if (mouse.X != 0 || mouse.Y != 0)
            {
                QueueInput(LanInputKind.Move, Math.Clamp(mouse.X, -32767, 32767), Math.Clamp(mouse.Y, -32767, 32767));
            }
            for (int button = 0; button < 5; button++)
            {
                if ((mouse.Buttons & (1 << (2 * button))) != 0)
                {
                    QueueInput(LanInputKind.Button, button, 0);
                }
                if ((mouse.Buttons & (2 << (2 * button))) != 0)
                {
                    QueueInput(LanInputKind.Button, button, 1);
                }
            }
            if ((mouse.Buttons & 0x400) != 0)
            {
                QueueInput(LanInputKind.Wheel, (short)mouse.Data, 0);
            }
            if ((mouse.Buttons & 0x800) != 0)
            {
                QueueInput(LanInputKind.Wheel, (short)mouse.Data, 1);
            }
            return;
        }
        if (mouse.X != 0 || mouse.Y != 0)
        {
            var current = WindowsLanInput.CursorPosition();
            if (Crossed(current, _options.Edge, mouse.X, mouse.Y))
            {
                _point = current;
                RequestEnter(automatic: true);
            }
        }
    }

    private bool Crossed(Point point, LanEdge edge, int dx, int dy)
    {
        if (_selected is not { } screen || !_options.EdgeSwitch || _options.LockCursor)
        {
            return false;
        }
        if (screen.Bounds.Contains(point) && LanGeometry.Distance(screen.Bounds, point, edge) >= 8)
        {
            _armed = true;
        }
        int outward = LanGeometry.Outward(edge, dx, dy);
        if (!_armed || outward <= 0 || !LanGeometry.CanCross(screen.Bounds, _rects, point, edge, _options.CornerGuard))
        {
            _push = 0;
            return false;
        }
        _push = Math.Min(64, _push + outward);
        return _push >= _options.Threshold;
    }

    private bool RequestEnter(bool automatic)
    {
        if (!Available || !_peerReady || !CanForward || _session?.InputResponsive != true || _active != 0 || _pending != 0 ||
            _selected is not { } screen || _mouseButtons != 0 || _keyCount != 0 ||
            (automatic && !_options.EdgeSwitch))
        {
            return false;
        }
        var point = screen.Bounds.Contains(_point) ? _point : WindowsLanInput.CursorPosition();
        _pendingRatio = LanGeometry.Ratio(screen.Bounds, point, _options.Edge);
        _next = _next == int.MaxValue ? 1 : _next + 1;
        var data = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(data, _next);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), _pendingRatio);
        data[8] = (byte)LanGeometry.Opposite(_options.Edge);
        if (!Queue(LanFrameType.Enter, data))
        {
            return false;
        }
        _enterTick = Stopwatch.GetTimestamp();
        _pending = _next;
        SetStatus(LanInputStatus.Switching);
        return true;
    }

    private void ReceiveCore(LanFrame frame)
    {
        var session = _peer.CurrentSession;
        if (session?.Healthy != true)
        {
            return;
        }
        if (!ReferenceEquals(_session, session))
        {
            ResetSession(session);
        }

        var bytes = frame.Payload.AsSpan();
        switch (frame.Type)
        {
            case LanFrameType.Topology when bytes.Length == 1 && bytes[0] is 0 or 1 or 3:
                _peerReady = (bytes[0] & 1) != 0;
                _peerAllowsEdge = (bytes[0] & 2) != 0;
                if (!_peerReady && _options.Role == LanRole.Pc)
                {
                    Release(notify: true);
                }
                break;
            case LanFrameType.Enter when _options.Role == LanRole.Laptop && bytes.Length == 9 && bytes[8] <= 3:
                AcceptEnter(ReadId(bytes), ReadRatio(bytes), (LanEdge)bytes[8]);
                break;
            case LanFrameType.Applied when _options.Role == LanRole.Pc && bytes.Length == 4:
                FinishEnter(ReadId(bytes));
                break;
            case LanFrameType.Applied when _options.Role == LanRole.Pc && bytes.Length == 8:
                RecordSample(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                break;
            case LanFrameType.Leave when _options.Role == LanRole.Laptop && bytes.Length == 4:
                if (ReadId(bytes) == _active)
                {
                    Release(notify: false);
                }
                break;
            case LanFrameType.Return when _options.Role == LanRole.Pc && bytes.Length == 9 && bytes[8] <= 1:
                int id = ReadId(bytes);
                int ratio = ReadRatio(bytes);
                if (id == _pending || id == _active)
                {
                    Release(notify: false, ratio);
                    if (bytes[8] == 1)
                    {
                        SetStatus(LanInputStatus.WindowsBlocked);
                    }
                }
                break;
            case LanFrameType.Input when _options.Role == LanRole.Laptop:
                Apply(LanInputEvent.Decode(bytes));
                break;
            case LanFrameType.MonitorRequest or LanFrameType.MonitorResult:
                MonitorMessage?.Invoke(session, frame);
                break;
            default:
                throw new InvalidDataException();
        }
    }

    private void AcceptEnter(int id, int ratio, LanEdge edge)
    {
        if (!Available || _session?.InputResponsive != true || _active != 0 || _selected is null || _keyCount != 0 || _mouseButtons != 0 ||
            _down.Count != 0 || _buttons.Count != 0 || !WindowsLanInput.IsDefaultDesktop() ||
            !WindowsLanInput.Position(LanGeometry.AtEdge(_selected.Bounds, edge, ratio)))
        {
            Queue(LanFrameType.Return, ReturnControl(id, ratio, windowsBlocked: !_defaultDesktop));
            return;
        }
        _laptopEdge = edge;
        _active = id;
        _armed = false;
        _push = 0;
        Queue(LanFrameType.Applied, Control(id));
        SetStatus(LanInputStatus.Receiving);
    }

    private void FinishEnter(int id)
    {
        if (id != _pending)
        {
            return;
        }
        if (!Available || _session?.InputResponsive != true || _keyCount != 0 || _mouseButtons != 0 || _native is null || _selected is null)
        {
            Release(notify: true);
            return;
        }
        if (!_native.Park(LanGeometry.AtEdge(_selected.Bounds, _options.Edge, _pendingRatio, 0)))
        {
            Release(notify: true);
            return;
        }
        _active = id;
        _pending = 0;
        _rawWatchdog = default;
        SetStatus(LanInputStatus.Remote);
        CompleteManual(true);
    }

    private void Apply(LanInputEvent input)
    {
        if (_active == 0 || !Available)
        {
            return;
        }
        if (input.Kind == LanInputKind.Move && _peerAllowsEdge && _buttons.Count == 0 && _down.Count == 0 &&
            _selected is not null)
        {
            var point = WindowsLanInput.CursorPosition();
            if (Crossed(point, _laptopEdge, input.A, input.B))
            {
                Release(notify: true, LanGeometry.Ratio(_selected.Bounds, point, _laptopEdge));
                return;
            }
        }
        if (!WindowsLanInput.Inject(input))
        {
            Release(notify: true, windowsBlocked: true);
            SetStatus(LanInputStatus.WindowsBlocked);
            return;
        }
        if (input.Kind == LanInputKind.Key)
        {
            var key = (input.A, input.B & ~2);
            if ((input.B & 2) == 0)
            {
                _down.Add(key);
            }
            else
            {
                _down.Remove(key);
            }
        }
        if (input.Kind == LanInputKind.Button)
        {
            if (input.B == 0)
            {
                _buttons.Add(input.A);
            }
            else
            {
                _buttons.Remove(input.A);
            }
        }
        if (input.Timestamp != 0)
        {
            var echo = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(echo, input.Timestamp);
            Queue(LanFrameType.Applied, echo);
        }
    }

    private void Release(bool notify, int? ratio = null, bool windowsBlocked = false)
    {
        int id = _active != 0 ? _active : _pending;
        bool warped = _active != 0 && _options.Role == LanRole.Pc;
        _active = _pending = _push = 0;
        CompleteManual(false);
        if (id != 0)
        {
            _armed = false;
            if (notify)
            {
                if (_options.Role == LanRole.Pc)
                {
                    Queue(LanFrameType.Leave, Control(id));
                }
                else
                {
                    Queue(LanFrameType.Return, ReturnControl(id, ratio ?? (LanGeometry.Unit / 2), windowsBlocked));
                }
            }
        }
        if (warped)
        {
            _native?.Unpark();
            if (_selected is not null && _defaultDesktop)
            {
                _point = LanGeometry.AtEdge(_selected.Bounds, _options.Edge, ratio ?? _pendingRatio, 4);
                WindowsLanInput.Position(_point);
            }
        }
        ReleaseInjected();
        if (Status != LanInputStatus.CaptureFailed)
        {
            SetStatus(LanInputStatus.Local);
        }
    }

    private void ReleaseInjected()
    {
        if (!_defaultDesktop || _down.Count == 0 && _buttons.Count == 0)
        {
            return;
        }
        _down.RemoveWhere(key =>
        {
            int vk = WindowsLanInput.VirtualKey(key.Code, key.Flags);
            return (vk is > 0 and < 256 && _physical[vk]) || WindowsLanInput.Key(key.Code, key.Flags | 2);
        });
        _buttons.RemoveWhere(button => (_mouseButtons & (1 << button)) != 0 || WindowsLanInput.Button(button, up: true));
        _retryRelease = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
    }

    private void EmergencyCore()
    {
        Release(notify: true);
        _pause = true;
        Advertise();
    }

    private void CompleteManual(bool accepted)
    {
        _manualRequest?.TrySetResult(accepted);
        _manualRequest = null;
    }

    private void SyncPhysical()
    {
        _keyCount = _mouseButtons = 0;
        var injectedKeys = _down.Select(key => WindowsLanInput.VirtualKey(key.Code, key.Flags)).ToHashSet();
        for (int key = 8; key < _physical.Length; key++)
        {
            bool held = key is not (0x10 or 0x11 or 0x12) && !injectedKeys.Contains(key) && WindowsLanInput.IsKeyDown(key);
            _physical[key] = held;
            _swallowed[key] &= held;
            if (held)
            {
                _keyCount++;
            }
        }
        for (int button = 0; button < 5; button++)
        {
            if (!_buttons.Contains(button) && WindowsLanInput.IsKeyDown(ButtonKey(button)))
            {
                _mouseButtons |= 1 << button;
            }
        }
    }

    private bool QueueInput(LanInputKind kind, int a, int b)
    {
        long now = Stopwatch.GetTimestamp();
        long sample = now - _lastSample >= Stopwatch.Frequency / 8 ? now : 0;
        if (sample != 0)
        {
            _lastSample = sample;
        }
        return Queue(new Emission(_session, LanFrameType.Input, null, new LanInputEvent(kind, a, b, sample)));
    }

    private bool Queue(LanFrameType type, byte[] payload) => Queue(new Emission(_session, type, payload, default));

    private bool CanForward => _session?.Healthy == true && !ReferenceEquals(_overflowedSession, _session);

    private bool Queue(Emission emission)
    {
        if (!CanForward || emission.Session is null)
        {
            return false;
        }
        if (_outbound.Writer.TryWrite(emission))
        {
            return true;
        }
        Interlocked.Increment(ref _overflows);
        _overflowedSession = emission.Session;
        _ = Task.Run(emission.Session.Abort);
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            Release(notify: false);
            SetStatus(LanInputStatus.Overloaded);
        });
        return false;
    }

    private async Task SendLoop()
    {
        var reader = _outbound.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (!reader.TryRead(out var next))
            {
                continue;
            }
            while (true)
            {
                if (!reader.TryRead(out var following))
                {
                    Send(next);
                    break;
                }
                if (next.Type == LanFrameType.Input && following.Type == LanFrameType.Input &&
                    next.Input.Kind == LanInputKind.Move && following.Input.Kind == LanInputKind.Move &&
                    ReferenceEquals(next.Session, following.Session))
                {
                    var old = next.Input;
                    var move = following.Input;
                    int x = old.A + move.A;
                    int y = old.B + move.B;
                    if (Math.Abs(x) <= 32767 && Math.Abs(y) <= 32767 && old.Timestamp == 0)
                    {
                        next = following with { Input = move with { A = x, B = y } };
                        Interlocked.Increment(ref _coalesced);
                        continue;
                    }
                }
                Send(next);
                next = following;
            }
        }
    }

    private static void Send(Emission emission)
    {
        if (emission.Session?.Healthy == true)
        {
            emission.Session.TrySend(emission.Type, emission.Payload ?? emission.Input.Encode());
        }
    }

    private void RecordSample(long sent)
    {
        long now = Stopwatch.GetTimestamp();
        if (sent <= _lastAck || sent > _lastSample || sent <= 0 || sent > now)
        {
            return;
        }
        double ms = Stopwatch.GetElapsedTime(sent, now).TotalMilliseconds;
        if (ms > 3000)
        {
            return;
        }
        _lastAck = sent;
        if (_delivery.Count == 256)
        {
            _delivery.RemoveAt(0);
        }
        _delivery.Add(ms);
        Volatile.Write(ref _latency, _delivery.ToArray());
    }

    private uint Modifiers() =>
        (_physical[0xA2] || _physical[0xA3] ? HotkeyChord.Ctrl : 0) |
        (_physical[0xA4] || _physical[0xA5] ? HotkeyChord.Alt : 0) |
        (_physical[0xA0] || _physical[0xA1] ? HotkeyChord.Shift : 0) |
        (_physical[0x5B] || _physical[0x5C] ? HotkeyChord.Win : 0);

    private int FindChord(uint modifiers, uint key)
    {
        var chord = new HotkeyChord(modifiers, key);
        if (chord == _options.Emergency) return 3;
        if (chord == _options.ToPc) return 0;
        if (chord == _options.ToLaptop) return 1;
        return chord == _options.Toggle ? 2 : -1;
    }

    private void SetStatus(LanInputStatus status) => Volatile.Write(ref _state, (int)status);
    private static int ButtonKey(int button) => button switch { 0 => 1, 1 => 2, 2 => 4, 3 => 5, _ => 6 };
    private static int ReadId(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt32LittleEndian(data) is > 0 and var id ? id : throw new InvalidDataException();
    private static int ReadRatio(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt32LittleEndian(data[4..]) is >= 0 and <= LanGeometry.Unit and var ratio
            ? ratio : throw new InvalidDataException();
    private static byte[] Control(int id)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(result, id);
        return result;
    }

    private static byte[] ReturnControl(int id, int ratio, bool windowsBlocked)
    {
        var result = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(result, id);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), ratio);
        result[8] = windowsBlocked ? (byte)1 : (byte)0;
        return result;
    }
    private readonly record struct Emission(LanSession? Session, LanFrameType Type, byte[]? Payload, LanInputEvent Input);
}
