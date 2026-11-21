using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MonitorInputSwitcher;

public partial class App : System.Windows.Application
{
    private const string AppTitle = "Monitor Input Switcher";
    private const int DisplayChanged = 0x007E;
    private const int DeviceChanged = 0x0219;
    private const int DeviceNodesChanged = 0x0007;
    private const int PowerBroadcast = 0x0218;
    private const int ResumeSuspend = 0x0007;
    private const int ResumeAutomatic = 0x0012;
    private const int UseImmersiveDarkMode = 20;

    private readonly SettingsStore _store = new();
    private readonly DisplayDiscovery _discovery = new();
    private readonly DdcMonitor _monitor;
    private readonly object _logGate = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private AppInstance? _instance;
    private MainWindow? _window;
    private HwndSource? _source;
    private GlobalHotkeys? _hotkeys;
    private DesktopIntegration? _desktop;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private AppSettings _settings = new();
    private CachedCapabilities? _cached;
    private string? _loadIssue;
    private string? _hotkeyIssue;
    private bool _startupEnabled;
    private bool _busy;
    private bool _exiting;
    private bool _refreshPending;
    private int _displayGeneration;

    public App() => _monitor = new DdcMonitor(_discovery);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!AppInstance.TryParseArgs(e.Args, out var command))
        {
            MessageBox.Show(UiText.Text("Tham số hợp lệ: --tray, --pc hoặc --laptop.", "Supported arguments: --tray, --pc, or --laptop."), AppTitle);
            Shutdown(2);
            return;
        }

        try
        {
            _instance = new AppInstance();
            if (!_instance.IsPrimary)
            {
                var forwarded = _instance.TryForward(command);
                if (!forwarded)
                {
                    MessageBox.Show(UiText.Text("Ứng dụng đang chạy nhưng không nhận được lệnh. Hãy mở ứng dụng từ khay hệ thống.", "The app is running but could not receive the command. Open it from the system tray."), AppTitle);
                }

                Shutdown(forwarded ? 0 : 1);
                return;
            }

            StartPrimary(command);
        }
        catch (Exception ex)
        {
            Log((UiText.IsEnglish ? $"Startup: {ex.Message}" : $"Khởi động: {ex.Message}"));
            MessageBox.Show((UiText.IsEnglish ? $"Could not start Monitor Input Switcher. {ex.Message}" : $"Không thể khởi động Monitor Input Switcher. {ex.Message}"), AppTitle);
            Quit(1);
        }
    }

    private void StartPrimary(AppCommand command)
    {
        (_settings, _loadIssue) = _store.Load();
        _desktop = new DesktopIntegration(Environment.ProcessPath
            ?? throw new InvalidOperationException(UiText.Text("Không xác định được vị trí tệp chạy.", "Could not locate the application executable.")));
        _startupEnabled = ReadStartupState();

        var window = new MainWindow();
        _window = window;
        MainWindow = window;
        window.ActionRequested += action => _ = RunMonitorOperation(action);
        window.ProbeRequested += () => _ = RunMonitorOperation(null);
        window.SettingsRequested += SaveSettings;
        window.ShortcutsRequested += CreateShortcuts;
        window.Closing += (_, args) =>
        {
            if (!_exiting)
            {
                args.Cancel = true;
                window.Hide();
            }
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.Hide();
            }
        };
        SessionEnding += (_, _) =>
        {
            _exiting = true;
            _ = _lanInput?.Suspend();
        };

        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) && !SystemParameters.HighContrast)
        {
            var enabled = 1;
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        }

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(HandleMessage);
        _lanWindowHandle = handle;
        _hotkeys = new GlobalHotkeys(handle);
        var hotkeyErrors = _hotkeys.InstallInitial(_settings);
        _hotkeyIssue = hotkeyErrors.Count == 0 ? null : string.Join(" ", hotkeyErrors);
        if (_hotkeyIssue is not null)
        {
            Log(_hotkeyIssue);
        }

        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (window.IsVisible && !_exiting)
            {
                if (_busy)
                {
                    _refreshPending = true;
                }
                else
                {
                    _ = RunMonitorOperation(null);
                }
            }
        };

        StartLan(window);
        _tray = CreateTray();
        window.LoadSettings(_settings, _startupEnabled, openSettings: _settings.HdmiPort is null);
        _instance!.StartListening(queued => Dispatcher.BeginInvoke(() => DispatchCommand(queued)), ex => Log(ex.Message));
        DispatchCommand(command);
        if (command is AppCommand.Open or AppCommand.Tray)
        {
            _ = RunMonitorOperation(null);
        }
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(UiText.Text("Mở Monitor Input Switcher", "Open Monitor Input Switcher"), null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("PC · DisplayPort", null, (_, _) => _ = RunMonitorOperation(InputAction.Pc));
        menu.Items.Add("Laptop · HDMI", null, (_, _) => _ = RunMonitorOperation(InputAction.Laptop));
        menu.Items.Add("Switch Input", null, (_, _) => _ = RunMonitorOperation(InputAction.Toggle));
        menu.Items.Add(UiText.Text("Kiểm tra kết nối", "Check connection"), null, (_, _) => _ = RunMonitorOperation(null));
        AddLanTrayItems(menu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(UiText.Text("Thoát", "Exit"), null, (_, _) => Quit());
        using var iconStream = GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative)).Stream;
        using var iconResource = new System.Drawing.Icon(iconStream, 16, 16);
        _trayIcon = (System.Drawing.Icon)iconResource.Clone();
        var icon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            ContextMenuStrip = menu,
            Text = AppTitle,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private void DispatchCommand(AppCommand command)
    {
        if (_exiting)
        {
            return;
        }

        switch (command)
        {
            case AppCommand.Open:
                ShowWindow();
                break;
            case AppCommand.Pc:
                _ = RunMonitorOperation(InputAction.Pc);
                break;
            case AppCommand.Laptop:
                _ = RunMonitorOperation(InputAction.Laptop);
                break;
        }
    }

    private void ShowWindow()
    {
        if (_window is not { } window || _exiting)
        {
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        if (_refreshPending && !_busy)
        {
            _ = RunMonitorOperation(null);
        }
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == GlobalHotkeys.MessageId && _hotkeys?.TryGetAction(wParam, lParam, out var action) == true)
        {
            handled = true;
            _ = RunMonitorOperation(action);
        }
        else if (message == GlobalHotkeys.MessageId && (long)wParam == LanToggleHotkeyId && _lanToggleRegistered)
        {
            handled = true;
            _ = LanCommand("toggle");
        }
        else if (message == DisplayChanged ||
                 (message == DeviceChanged && wParam == DeviceNodesChanged) ||
                 (message == PowerBroadcast && (wParam == ResumeSuspend || wParam == ResumeAutomatic)))
        {
            ++_displayGeneration;
            _cached = null;
            _refreshPending = true;
            _ = RefreshLanScreens();
            if (_window?.IsVisible == true)
            {
                _window.SetStatus(UiText.Text("Đang cập nhật…", "Refreshing…"), UiText.Text("Chờ kiểm tra", "Awaiting check"), UiText.Text("Chưa xác định", "Unknown"), false, false);
                _refresh.Stop();
                _refresh.Start();
            }
        }
        else if (message == PowerBroadcast && wParam == 4)
        {
            _ = _lanInput?.Suspend();
        }

        return 0;
    }

    private async Task<SwitchOutcome?> RunMonitorOperation(InputAction? action, string? summary = null)
    {
        if (_exiting || _window is not { } window)
        {
            return null;
        }

        if (_busy)
        {
            if (action is not null)
            {
                Report(UiText.Text("Đang xử lý một thao tác với màn hình. Hãy thử lại.", "A monitor operation is in progress. Try again."), notify: true);
            }

            return null;
        }

        var settings = _settings;
        if ((action is InputAction.Laptop or InputAction.Toggle) && settings.HdmiPort is null)
        {
            Report(UiText.Text("Hãy chọn cổng HDMI của laptop trong Cài đặt.", "Select the laptop HDMI port in Settings."), notify: true);
            return null;
        }

        _busy = true;
        _refreshPending = false;
        window.SetBusy(true);
        Report(action is null ? UiText.Text("Đang kiểm tra kết nối…", "Checking connection…") : UiText.Text("Đang gửi yêu cầu đổi nguồn…", "Requesting an input switch…"), log: false);

        try
        {
            var generation = _displayGeneration;
            var cache = _cached;
            var result = await Task.Run(() => Operate(settings, action, cache));
            if (_exiting)
            {
                return null;
            }

            if (generation != _displayGeneration)
            {
                Report(result.Message, notify: action is not null);
                if (!window.IsVisible)
                {
                    window.SetStatus(UiText.Text("Đang cập nhật…", "Refreshing…"), UiText.Text("Chờ kiểm tra", "Awaiting check"), UiText.Text("Chưa xác định", "Unknown"), false, false);
                }

                return result.Outcome;
            }

            _cached = result.Cache;
            window.SetTargets(result.Targets);
            var profile = settings.ToProfile();
            var canToggle = profile.Laptop is byte laptop &&
                            (result.Current == profile.Pc || result.Current == laptop);
            window.SetStatus(result.Target?.Name ?? UiText.Text("Chưa chọn được màn hình", "No monitor selected"), result.Status,
                DescribeSource(result.Current, settings), result.Target is not null, canToggle);
            Report(summary is null ? result.Message : $"{summary} {result.Message}",
                notify: action is not null);
            return result.Outcome;
        }
        catch (Exception ex)
        {
            if (!_exiting)
            {
                window.SetStatus(UiText.Text("Chưa xác định", "Unknown"), UiText.Text("Lỗi truy vấn", "Query failed"), UiText.Text("Chưa xác định", "Unknown"), false, false);
                Report((UiText.IsEnglish ? $"Could not complete the operation. {ex.Message}" : $"Không thể hoàn tất thao tác. {ex.Message}"), notify: action is not null);
            }
            return null;
        }
        finally
        {
            _busy = false;
            if (!_exiting)
            {
                window.SetBusy(false);
                if (_refreshPending && window.IsVisible)
                {
                    _refresh.Start();
                }
            }
        }
    }

    private MonitorOperation Operate(AppSettings settings, InputAction? action, CachedCapabilities? cache)
    {
        var targets = _discovery.List();
        var target = SelectTarget(targets, settings.MonitorId);
        if (target is null)
        {
            var hint = settings.MonitorId is not null
                ? UiText.Text("Màn hình đã lưu hiện không khả dụng. Kết nối lại hoặc chọn màn hình trong Cài đặt.", "The saved monitor is unavailable. Reconnect it or select a monitor in Settings.")
                : UiText.Text("Chưa nhận diện được đúng một MSI G255F. Hãy chọn màn hình trong Cài đặt.", "Could not uniquely identify an MSI G255F. Select a monitor in Settings.");
            return new MonitorOperation(targets, null, null, UiText.Text("Chưa kiểm tra", "Unchecked"), hint, null,
                action is null ? null : SwitchOutcome.NotAvailable);
        }

        if (action is null)
        {
            var probe = _monitor.Read(target.Id, includeCapabilities: true);
            var status = DescribeDdc(probe);
            return new MonitorOperation(targets, target, probe.Current, status,
                DescribeProbe(probe), CacheCapabilities(target, probe));
        }

        VcpRead? initial = null;
        VcpRead? followUp = null;
        var result = InputSwitch.Execute(action.Value, settings.ToProfile(), includeCapabilities =>
        {
            if (!includeCapabilities)
            {
                return followUp = _monitor.Read(target.Id, includeCapabilities: false);
            }

            if (cache is not null && string.Equals(cache.TargetId, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                var fresh = _monitor.Read(target.Id, includeCapabilities: false);
                return initial = fresh.ConnectionError is null
                    ? fresh with { AdvertisesVcp60 = true, AdvertisedInputs = cache.Inputs }
                    : fresh;
            }

            return initial = _monitor.Read(target.Id, includeCapabilities: true);
        }, value => _monitor.Write(target.Id, value), Thread.Sleep);

        var ddc = result.Outcome == SwitchOutcome.SendFailed ? UiText.Text("Lỗi gửi lệnh", "Send failed")
            : followUp?.ConnectionError is not null ? UiText.Text("Mất liên lạc sau lệnh", "Connection lost after sending")
            : result.Current is not null ? UiText.Text("Đọc được VCP 0x60", "VCP 0x60 readable")
            : initial is null ? UiText.Text("Chưa kiểm tra", "Unchecked") : DescribeDdc(initial with { Current = null });
        return new MonitorOperation(targets, target, result.Current, ddc, result.Message,
            initial is null ? null : CacheCapabilities(target, initial), result.Outcome);
    }

    private static DisplayTarget? SelectTarget(IReadOnlyList<DisplayTarget> targets, string? id)
    {
        var matches = targets.Where(target => target.IsUnambiguous &&
            (id is null ? target.IsG255F : string.Equals(id, target.Id, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static CachedCapabilities? CacheCapabilities(DisplayTarget target, VcpRead probe) =>
        probe.AdvertisedInputs is { Count: > 0 } inputs ? new CachedCapabilities(target.Id, inputs) : null;

    private static string DescribeDdc(VcpRead read) =>
        read.ConnectionError is not null ? UiText.Text("Không kết nối được", "Unavailable")
        : read.Current is not null ? UiText.Text("Đọc được VCP 0x60", "VCP 0x60 readable")
        : read.AdvertisesVcp60 ? UiText.Text("Có VCP 0x60 · Chưa đọc được nguồn", "VCP 0x60 advertised · Current input unreadable")
        : UiText.Text("Chưa xác nhận hỗ trợ VCP 0x60", "VCP 0x60 support unconfirmed");

    private static string DescribeProbe(VcpRead probe)
    {
        if (probe.ConnectionError is not null)
        {
            return probe.ConnectionError;
        }

        var details = new List<string>();
        if (probe.AdvertisedInputs is { Count: > 0 } inputs)
        {
            details.Add((UiText.IsEnglish ? $"Monitor-advertised input codes: {string.Join(", ", inputs.Select(value => $"0x{value:X2}"))}." : $"Mã nguồn màn hình công bố: {string.Join(", ", inputs.Select(value => $"0x{value:X2}"))}."));
        }

        if (probe.ReadError is int readError)
        {
            details.Add((UiText.IsEnglish ? $"Could not read VCP 0x60 (Win32 0x{unchecked((uint)readError):X8})." : $"Lỗi đọc VCP 0x60 (Win32 0x{unchecked((uint)readError):X8})."));
        }

        if (probe.CapabilitiesError is int capabilitiesError)
        {
            details.Add((UiText.IsEnglish ? $"Could not read capabilities (Win32 0x{unchecked((uint)capabilitiesError):X8})." : $"Không đọc được danh sách khả năng (Win32 0x{unchecked((uint)capabilitiesError):X8})."));
        }

        return details.Count > 0 ? string.Join(" ", details) : UiText.Text("Đã kiểm tra kết nối màn hình.", "Monitor connection checked.");
    }

    private static string DescribeSource(uint? code, AppSettings settings)
    {
        if (code is not uint value)
        {
            return UiText.Text("Chưa xác định", "Unknown");
        }

        var profile = settings.ToProfile();
        var label = value == profile.Pc ? "PC · DisplayPort"
            : value == profile.Laptop ? $"Laptop · HDMI {settings.HdmiPort}"
            : value switch { 0x0F => "DisplayPort", 0x11 => "HDMI 1", 0x12 => "HDMI 2", _ => null };
        return label is null ? $"0x{value:X2}" : $"{label} (0x{value:X2})";
    }

    private void SaveSettings(AppSettings settings, bool startup)
    {
        if (_busy || _exiting || _hotkeys is null || _desktop is null)
        {
            return;
        }

        var previous = _settings;
        var hotkeysChanged = settings.PcHotkey != previous.PcHotkey ||
                             settings.LaptopHotkey != previous.LaptopHotkey ||
                             settings.ToggleHotkey != previous.ToggleHotkey;
        if (hotkeysChanged)
        {
            SetLanToggleRegistration(false);
        }
        if (hotkeysChanged && !_hotkeys.TryApply(settings, out var error))
        {
            RefreshLanHotkeyConflicts();
            Report(error ?? UiText.Text("Không thể thay đổi phím tắt.", "Could not update the hotkeys."));
            return;
        }

        try
        {
            _store.Save(settings);
        }
        catch (Exception ex)
        {
            if (hotkeysChanged && !_hotkeys.TryApply(previous, out var rollbackError))
            {
                _hotkeyIssue = rollbackError;
            }
            RefreshLanHotkeyConflicts();

            Report((UiText.IsEnglish ? $"Could not save settings. {ex.Message}" : $"Không thể lưu cài đặt. {ex.Message}"));
            return;
        }

        _settings = settings;
        RefreshLanHotkeyConflicts();
        _loadIssue = null;
        if (hotkeysChanged)
        {
            _hotkeyIssue = null;
        }

        var summary = UiText.Text("Đã lưu cài đặt.", "Settings saved.");
        if (startup != _startupEnabled)
        {
            try
            {
                _desktop.SetStartup(startup);
                _startupEnabled = startup;
            }
            catch (Exception ex)
            {
                summary += (UiText.IsEnglish ? $" Could not update Windows startup. {ex.Message}" : $" Không thể cập nhật khởi động cùng Windows. {ex.Message}");
            }
        }

        _cached = null;
        _window?.LoadSettings(settings, _startupEnabled);
        _ = RunMonitorOperation(null, summary);
    }

    private bool ReadStartupState()
    {
        try
        {
            return _desktop?.IsStartupEnabled() == true;
        }
        catch (Exception ex)
        {
            Log((UiText.IsEnglish ? $"Reading startup configuration: {ex.Message}" : $"Đọc cấu hình khởi động: {ex.Message}"));
            return false;
        }
    }

    private void CreateShortcuts()
    {
        try
        {
            _desktop?.CreateShortcuts();
            Report(UiText.Text("Đã tạo shortcut PC và Laptop trên Desktop.", "Created PC and Laptop shortcuts on the desktop."));
        }
        catch (Exception ex)
        {
            Report((UiText.IsEnglish ? $"Could not create both desktop shortcuts. {ex.Message}" : $"Không thể tạo đầy đủ shortcut trên Desktop. {ex.Message}"));
        }
    }

    private void Report(string message, bool notify = false, bool log = true)
    {
        _window?.ShowMessage(string.Join(" ", new[] { message, _loadIssue, _hotkeyIssue }
            .Where(part => !string.IsNullOrWhiteSpace(part))));
        if (log)
        {
            Log(message);
        }

        if (notify && _window?.IsVisible == false)
        {
            _tray?.ShowBalloonTip(3000, AppTitle, message, Forms.ToolTipIcon.Info);
        }
    }

    private void Log(string message)
    {
        lock (_logGate)
        {
            try
            {
                Directory.CreateDirectory(_store.DirectoryPath);
                var path = Path.Combine(_store.DirectoryPath, "monitor.log");
                var current = new FileInfo(path);
                if (current.Exists && current.Length >= 256 * 1024)
                {
                    File.Move(path, Path.Combine(_store.DirectoryPath, "monitor.previous.log"), overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Debug.WriteLine(ex.Message);
            }
        }
    }

    private async void Quit(int exitCode = 0)
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _hotkeys?.Dispose();
        try
        {
            await StopLan().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            Log("LAN shutdown exceeded two seconds.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("LAN shutdown failed.");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _refresh.Stop();
        _lanUiTimer?.Stop();
        SetLanToggleRegistration(false);
        if (_source is { IsDisposed: false })
        {
            _source.RemoveHook(HandleMessage);
        }

        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();

        _instance?.Dispose();
        base.OnExit(e);
    }

    private sealed record CachedCapabilities(string TargetId, IReadOnlyList<byte> Inputs);

    private sealed record MonitorOperation(
        IReadOnlyList<DisplayTarget> Targets, DisplayTarget? Target, uint? Current,
        string Status, string Message, CachedCapabilities? Cache, SwitchOutcome? Outcome = null);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);
}
