using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using static MonitorInputSwitcher.UiText;

namespace MonitorInputSwitcher;

public partial class App
{
    private const int LanToggleHotkeyId = 0xB004;
    private readonly SemaphoreSlim _lanCommands = new(1);
    private LanSharingOptions _lanOptions = new();
    private LanInputController? _lanInput;
    private LanPeerService? _lanPeer;
    private LanFileClipboard? _lanFiles;
    private DispatcherTimer? _lanUiTimer;
    private nint _lanWindowHandle;
    private bool _lanToggleRegistered;
    private long _lastMonitorRequest;
    private int _monitorRequestId, _pendingMonitor;
    private LanSession? _pendingMonitorSession;

    private void StartLan(MainWindow window)
    {
        _lanOptions = LanSharingOptions.Load(_store.DirectoryPath);
        _lanFiles = new LanFileClipboard(message => Dispatcher.BeginInvoke(() => Log(message)));
        _lanPeer = new LanPeerService(_store.DirectoryPath,
            (frame, stop) => _lanInput!.Receive(frame, stop), () => _lanInput?.LinkEnded(), _lanFiles);
        _lanInput = new LanInputController(_lanPeer, _lanFiles);
        _lanInput.Hotkey += action => Dispatcher.BeginInvoke(() => _ = LanCommand(action switch
        {
            0 => "pc", 1 => "laptop", 2 => "toggle", _ => "emergency"
        }));
        _lanInput.MonitorMessage += LanMonitorMessage;
        window.Sharing.Fill(_lanOptions);
        window.Sharing.Invoked += command => _ = LanCommand(command);
        _lanUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _lanUiTimer.Tick += (_, _) => RenderLan();
        _lanUiTimer.Start();
        _ = LanCommand("start");
        _ = RefreshLanScreens();
    }

    private async Task StopLan()
    {
        _lanUiTimer?.Stop();
        SetLanToggleRegistration(false);
        if (_lanInput is { } activeInput)
        {
            await activeInput.Suspend();
        }
        await _lanCommands.WaitAsync();
        try
        {
            if (_lanInput is { } input && _lanPeer is { } peer)
            {
                await peer.DisposeAsync();
                await input.DisposeAsync();
                if (_lanFiles is { } clipboard) await clipboard.DisposeAsync();
                _lanFiles = null;
                _lanInput = null;
                _lanPeer = null;
            }
        }
        finally
        {
            _lanCommands.Release();
        }
    }

    private void AddLanTrayItems(Forms.ContextMenuStrip menu)
    {
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Sharing, null, (_, _) =>
        {
            ShowWindow();
            _window?.OpenSharing();
        });
        menu.Items.Add(Text("Bật/tắt chia sẻ", "Toggle sharing"), null, (_, _) => _ = LanCommand("toggle"));
        menu.Items.Add(Text("Điều khiển PC", "Control PC"), null, (_, _) => _ = LanCommand("pc"));
        menu.Items.Add(Text("Điều khiển laptop", "Control laptop"), null, (_, _) => _ = LanCommand("laptop"));
        menu.Items.Add(Text("Khôi phục điều khiển cục bộ", "Restore local control"), null, (_, _) => _ = LanCommand("emergency"));
    }

    private async Task RefreshLanScreens()
    {
        try
        {
            var screens = await Task.Run(LanScreens.Read);
            if (!_exiting)
            {
                _window?.Sharing.UpdateScreens(screens);
                if (_lanInput is { } input)
                {
                    await input.Refresh(screens);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
        }
    }

    private async Task LanCommand(string command)
    {
        if (_exiting || _lanPeer is not { } peer || _lanInput is not { } input || _window is not { } window)
        {
            return;
        }
        var panel = window.Sharing;
        if (command == "emergency")
        {
            await input.Emergency();
            panel.SetMessage(Text("Đã khôi phục điều khiển cục bộ. Bấm Kết nối lại để tiếp tục.", "Local control restored. Select Reconnect to resume."));
            return;
        }
        await _lanCommands.WaitAsync();
        try
        {
            if (_exiting)
            {
                return;
            }
            switch (command)
            {
                case "start":
                    await ApplyLanOptions(_lanOptions, restart: true);
                    break;
                case "save":
                    if (panel.ReadOptions() is { } options && ValidLanOptions(options))
                    {
                        await SaveLanOptions(options);
                        panel.SetMessage(Text("Đã lưu cài đặt.", "Settings saved."));
                    }
                    else
                    {
                        ShowInvalidLanOptions(panel);
                    }
                    break;
                case "toggle":
                    var toggled = _lanOptions with { Enabled = !_lanOptions.Enabled };
                    if (ValidLanOptions(toggled))
                    {
                        await SaveLanOptions(toggled);
                    }
                    else
                    {
                        ShowInvalidLanOptions(panel);
                    }
                    break;
                case "invite":
                case "pair":
                    if (panel.ReadOptions() is not { } pairing || !ValidLanOptions(pairing) ||
                        pairing.Role != (command == "invite" ? LanRole.Laptop : LanRole.Pc))
                    {
                        panel.SetMessage(Text("Kiểm tra vai trò và cài đặt ghép đôi.", "Check the selected role and pairing settings."));
                        break;
                    }
                    await SaveLanOptions(pairing with { Enabled = true });
                    if (command == "invite")
                    {
                        panel.Invitation = await peer.InviteAsync();
                        panel.SetMessage(Text("Chuyển mã trực tiếp sang PC. Mã có hiệu lực trong 5 phút.", "Transfer the code directly to the PC. The invitation expires in 5 minutes."));
                    }
                    else
                    {
                        var code = panel.Invitation;
                        panel.Invitation = "";
                        await peer.PairAsync(code);
                        panel.SetMessage(Text("Đã ghép đôi. Đang kết nối qua LAN.", "Paired. Connecting over LAN."));
                    }
                    break;
                case "disconnect":
                    panel.Invitation = "";
                    await input.Suspend();
                    await peer.StopAsync();
                    panel.SetMessage(Text("Đã ngắt kết nối.", "Disconnected."));
                    break;
                case "reconnect":
                    if (ValidLanOptions(_lanOptions))
                    {
                        await ApplyLanOptions(_lanOptions, restart: true);
                        panel.SetMessage(Text("Đang kết nối lại…", "Reconnecting…"));
                    }
                    break;
                case "unpair":
                    panel.Invitation = "";
                    await input.Suspend();
                    await peer.RevokeAsync();
                    panel.SetMessage(Text("Đã thu hồi ghép đôi trên máy này.", "Pairing was revoked on this computer."));
                    break;
                case "test":
                    await TestLan(panel);
                    break;
                case "pc":
                    await input.ChoosePc();
                    await SmartMonitor(InputAction.Pc);
                    break;
                case "laptop":
                    if (await input.ChooseLaptop())
                    {
                        await SmartMonitor(InputAction.Laptop);
                    }
                    else
                    {
                        panel.SetMessage(Text("Chưa bàn giao được. Kiểm tra kết nối, màn hình đích và thả các phím đang giữ.", "Handoff unavailable. Check the connection and target screen, then release any held keys."));
                    }
                    break;
                case "export":
                    var dialog = new SaveFileDialog { FileName = "monitor-input-switcher-diagnostics.txt", DefaultExt = ".txt",
                        Filter = Text("Báo cáo văn bản|*.txt", "Text report|*.txt") };
                    if (dialog.ShowDialog(window) == true)
                    {
                        await File.WriteAllTextAsync(dialog.FileName,
                            $"{DateTimeOffset.Now:O}{Environment.NewLine}{peer.Status.State} {peer.Status.Error}{Environment.NewLine}{MetricsText()}" +
                            Environment.NewLine + string.Join(Environment.NewLine,
                                peer.ReadErrors().Select(entry => $"{entry.Time:O} {entry.Error}")));
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            panel.SetMessage(ConnectionError(LanPeerService.Classify(ex)));
        }
        finally
        {
            _lanCommands.Release();
            RenderLan();
        }
    }

    private async Task TestLan(LanSharingPanel panel)
    {
        var session = _lanPeer?.CurrentSession;
        if (session is null)
        {
            panel.SetMessage(Text("Chưa có phiên đã xác thực. Kiểm tra ghép đôi và hồ sơ mạng Private.", "No authenticated session. Check pairing and the Private network profile."));
            return;
        }
        await Task.Delay(400);
        var rtt = session.ReadStatistics().RoundTripMs;
        panel.SetMessage(session.Healthy && rtt.HasValue
            ? Text($"Phiên TLS hoạt động. RTT {rtt:F1} ms.", $"TLS session is responding. RTT {rtt:F1} ms.")
            : Text("Chưa có phản hồi kiểm tra.", "No test response yet."));
    }

    private async Task SaveLanOptions(LanSharingOptions options)
    {
        bool restart = options.Enabled != _lanOptions.Enabled || options.Role != _lanOptions.Role ||
            options.ManualAddress != _lanOptions.ManualAddress || options.AutoReconnect != _lanOptions.AutoReconnect;
        await Task.Run(() => options.Save(_store.DirectoryPath));
        _lanOptions = options;
        await ApplyLanOptions(options, restart || _lanPeer?.Status.State == LanState.Disconnected);
        _window?.Sharing.Fill(options);
        await RefreshLanScreens();
    }

    private async Task ApplyLanOptions(LanSharingOptions options, bool restart)
    {
        if (_lanPeer is not { } peer || _lanInput is not { } input)
        {
            return;
        }
        if (!ValidLanOptions(options))
        {
            options = options with { Enabled = false };
            ShowInvalidLanOptions(_window?.Sharing);
        }
        SetLanToggleRegistration(false);
        await input.Configure(options);
        if (options.Enabled && restart)
        {
            await peer.StartAsync(options.Role, options.Address, options.AutoReconnect);
        }
        if (!options.Enabled)
        {
            await peer.StopAsync();
            SetLanToggleRegistration(true);
        }
    }

    private bool ValidLanOptions(LanSharingOptions options) =>
        options.Valid() && !options.Chords.Intersect(MonitorChords()).Any();

    private HotkeyChord[] MonitorChords() => [_settings.PcHotkey, _settings.LaptopHotkey, _settings.ToggleHotkey];

    private void RefreshLanHotkeyConflicts()
    {
        if (!ValidLanOptions(_lanOptions) && _lanOptions.Enabled)
        {
            ShowInvalidLanOptions(_window?.Sharing);
            _ = LanCommand("start");
        }
        else if (!_lanOptions.Enabled)
        {
            SetLanToggleRegistration(true);
        }
    }

    private static void ShowInvalidLanOptions(LanSharingPanel? panel) => panel?.SetMessage(
        Text("Kiểm tra địa chỉ IPv4, độ nhạy 1–32 và phím tắt. Phím chia sẻ phải khác phím chuyển nguồn.",
            "Check the IPv4 address, threshold 1–32 and hotkeys. Sharing shortcuts must differ from monitor shortcuts."));

    private void SetLanToggleRegistration(bool register)
    {
        if (_lanWindowHandle == 0)
        {
            return;
        }
        if (_lanToggleRegistered)
        {
            _ = UnregisterHotKey(_lanWindowHandle, LanToggleHotkeyId);
            _lanToggleRegistered = false;
        }
        if (register && !MonitorChords().Contains(_lanOptions.Toggle))
        {
            _lanToggleRegistered = RegisterHotKey(_lanWindowHandle, LanToggleHotkeyId,
                _lanOptions.Toggle.Modifiers | 0x4000, _lanOptions.Toggle.VirtualKey);
            if (!_lanToggleRegistered)
            {
                _window?.Sharing.SetMessage(Text("Phím bật/tắt đang được ứng dụng khác sử dụng.", "Another application is using the sharing toggle shortcut."));
            }
        }
    }

    private async Task SmartMonitor(InputAction action)
    {
        if (!_lanOptions.SmartDdc || _lanInput is not { } input)
        {
            return;
        }
        var outcome = await RunMonitorOperation(action);
        if (outcome == SwitchOutcome.NotAvailable && _lanPeer?.CurrentSession is { } session)
        {
            _monitorRequestId = _monitorRequestId == int.MaxValue ? 1 : _monitorRequestId + 1;
            _pendingMonitor = _monitorRequestId;
            _pendingMonitorSession = session;
            var body = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(body, _pendingMonitor);
            body[4] = (byte)action;
            await input.SendMonitor(session, LanFrameType.MonitorRequest, body);
            _ = ExpireMonitorRequest(session, _pendingMonitor);
        }
    }

    private async Task ExpireMonitorRequest(LanSession session, int id)
    {
        await Task.Delay(4000);
        if (_pendingMonitor != id || !ReferenceEquals(session, _pendingMonitorSession))
        {
            return;
        }
        _pendingMonitor = 0;
        _pendingMonitorSession = null;
        _window?.Sharing.SetMessage(Text("Hết thời gian chờ kết quả DDC/CI từ máy kia.", "The paired computer did not report a DDC/CI result in time."));
    }

    private void LanMonitorMessage(LanSession session, LanFrame frame)
    {
        if (frame.Payload.Length != 5 || BinaryPrimitives.ReadInt32LittleEndian(frame.Payload) is <= 0)
        {
            throw new InvalidDataException();
        }
        int id = BinaryPrimitives.ReadInt32LittleEndian(frame.Payload);
        byte value = frame.Payload[4];
        if (frame.Type == LanFrameType.MonitorRequest && value > (byte)InputAction.Laptop ||
            frame.Type == LanFrameType.MonitorResult && value != byte.MaxValue && value > (byte)SwitchOutcome.SendFailed)
        {
            throw new InvalidDataException();
        }
        Dispatcher.BeginInvoke(() => _ = HandleLanMonitorMessage(session, frame.Type, id, value));
    }

    private async Task HandleLanMonitorMessage(LanSession session, LanFrameType type, int id, byte value)
    {
        if (_exiting || !ReferenceEquals(session, _lanPeer?.CurrentSession) || _lanInput is not { } input)
        {
            return;
        }
        if (type == LanFrameType.MonitorResult)
        {
            if (id == _pendingMonitor && ReferenceEquals(session, _pendingMonitorSession))
            {
                _pendingMonitor = 0;
                _pendingMonitorSession = null;
                _window?.Sharing.SetMessage(RemoteMonitorResult(value));
            }
            return;
        }
        SwitchOutcome? outcome = null;
        long now = Stopwatch.GetTimestamp();
        if (_lanOptions.PeerDdc && Stopwatch.GetElapsedTime(_lastMonitorRequest, now).TotalSeconds >= 2)
        {
            _lastMonitorRequest = now;
            outcome = await RunMonitorOperation((InputAction)value);
        }
        var body = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(body, id);
        body[4] = outcome.HasValue ? (byte)outcome.Value : byte.MaxValue;
        await input.SendMonitor(session, LanFrameType.MonitorResult, body);
    }

    private static string RemoteMonitorResult(byte outcome) => outcome switch
    {
        (byte)SwitchOutcome.Confirmed or (byte)SwitchOutcome.AlreadySelected =>
            Text("Máy kia đọc được nguồn đích qua VCP. Kiểm tra hình trên MSI.", "The paired computer read back the requested input over VCP. Check the picture on the MSI."),
        (byte)SwitchOutcome.SentUnconfirmed =>
            Text("Máy kia đã gửi lệnh DDC/CI, chưa xác nhận nguồn.", "The paired computer sent the DDC/CI command. Input is unconfirmed."),
        _ => Text("Máy kia không đổi được nguồn qua DDC/CI.", "The paired computer could not switch the input over DDC/CI.")
    };

    private void RenderLan()
    {
        if (_exiting || _lanPeer is not { } peer || _lanInput is not { } input || _window?.IsVisible != true)
        {
            return;
        }
        var snapshot = peer.Status;
        var state = snapshot.State switch
        {
            LanState.Connected => Text("Đã kết nối", "Connected"),
            LanState.Connecting => Text("Đang kết nối", "Connecting"),
            LanState.Reconnecting => Text("Đang kết nối lại", "Reconnecting"),
            _ => Text("Đã ngắt kết nối", "Disconnected")
        };
        var role = _lanOptions.Role == LanRole.Pc ? "PC" : "Laptop";
        var peerRole = _lanOptions.Role == LanRole.Pc ? "Laptop" : "PC";
        string identity = $"{role} · {Environment.MachineName}";
        if (snapshot.Name is { } name)
        {
            identity += $"  |  {peerRole} · {name} · {snapshot.Address}";
        }
        var authority = input.Status switch
        {
            LanInputStatus.Switching => Text("Đang bàn giao điều khiển…", "Handing off input…"),
            LanInputStatus.Remote => Text("Đang điều khiển laptop", "Controlling laptop"),
            LanInputStatus.Receiving => Text("Đang nhận input từ PC", "Receiving input from PC"),
            LanInputStatus.WindowsBlocked => Text("Windows chặn input. Kiểm tra UAC hoặc cửa sổ quyền cao.", "Windows blocked input. Check UAC or an elevated window."),
            LanInputStatus.CaptureFailed => Text("Không khởi động được hook Windows.", "Could not initialize Windows input capture."),
            LanInputStatus.Overloaded => Text("Đã trả quyền điều khiển vì hàng đợi quá tải.", "Local control restored after input queue overflow."),
            _ => Text("Điều khiển cục bộ", "Local control")
        };
        _window.Sharing.UpdateConnection($"{identity}\n{state} · {authority}\n{ConnectionError(snapshot.Error)}", MetricsText());
    }

    private string MetricsText()
    {
        if (_lanPeer is not { } peer || _lanInput is not { } input)
        {
            return "";
        }
        var statistics = peer.Statistics;
        double[] delivery = input.Delivery.ToArray();
        Array.Sort(delivery);
        string p95 = delivery.Length == 0 ? "—" : Duration(delivery[(int)Math.Ceiling(delivery.Length * .95) - 1]);
        string reconnect = peer.ReconnectMilliseconds < 0 ? "—" : Duration(peer.ReconnectMilliseconds);
        string error = peer.ReadErrors().LastOrDefault().Error is var last && last != LanError.None
            ? $"\n{Text("Lỗi gần nhất", "Last error")} · {ConnectionError(last)}" : "";
        return Text(
            $"RTT {Duration(statistics.RoundTripMs)} · Jitter {Duration(statistics.JitterMs)} · Kết nối lại {reconnect}\n" +
            $"Capture → ACK p95 {p95} ({delivery.Length} mẫu)\nHàng đợi {statistics.QueueDepth}/{statistics.PeakQueueDepth} · Gom chuột {input.Coalesced} · Tràn {statistics.QueueOverflows + input.Overflows}\n" +
            $"Thiếu/sai thứ tự {statistics.SequenceGaps} · Trùng {statistics.DuplicateFrames} · Khung lỗi {statistics.InvalidFrames}",
            $"RTT {Duration(statistics.RoundTripMs)} · Jitter {Duration(statistics.JitterMs)} · Reconnect {reconnect}\n" +
            $"Capture → ACK p95 {p95} ({delivery.Length} samples)\nQueue {statistics.QueueDepth}/{statistics.PeakQueueDepth} · Moves merged {input.Coalesced} · Overflows {statistics.QueueOverflows + input.Overflows}\n" +
            $"Missing/out of order {statistics.SequenceGaps} · Duplicates {statistics.DuplicateFrames} · Invalid frames {statistics.InvalidFrames}") + error;
    }

    private static string Duration(double? ms) => ms?.ToString("F1", CultureInfo.CurrentCulture) is { } text ? $"{text} ms" : "—";

    private static string ConnectionError(LanError error) => error switch
    {
        LanError.None => "",
        LanError.NoPrivateRoute => Text("Không có IPv4 trên mạng Windows Private.", "No IPv4 route on a Windows Private network."),
        LanError.InvalidSubnet => Text("IPv4 laptop phải cùng subnet Private.", "The laptop IPv4 address must be on a local Private subnet."),
        LanError.NotPaired => Text("Chưa ghép đôi.", "Pairing is required."),
        LanError.Refused => Text("Kết nối bị từ chối. Kiểm tra app đích và cổng TCP 49351.", "Connection refused. Check the peer app and TCP port 49351."),
        LanError.Timeout => Text("Hết thời gian chờ. Kiểm tra IP, firewall, Wi-Fi guest hoặc client isolation.", "Timed out. Check the IP, firewall, guest Wi-Fi or client isolation."),
        LanError.Authentication => Text("Xác thực thất bại. Kiểm tra mã mời hoặc ghép đôi lại.", "Authentication failed. Check the invitation or pair again."),
        _ => Text("Thao tác LAN thất bại. Kiểm tra vai trò, ghép đôi và kết nối.", "LAN operation failed. Check the role, pairing and connection.")
    };

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hWnd, int id);
}
