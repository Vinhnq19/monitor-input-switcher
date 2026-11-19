using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;

namespace MonitorInputSwitcher;

public partial class MainWindow : Window
{
    private IReadOnlyList<DisplayTarget> _targets = [];
    private bool _busy;
    private bool _hasTarget;
    private bool _hasLaptop;
    private bool _canToggle;

    public MainWindow() => InitializeComponent();

    public event Action<InputAction>? ActionRequested;
    public event Action? ProbeRequested;
    public event Action<AppSettings, bool>? SettingsRequested;
    public event Action? ShortcutsRequested;

    public void LoadSettings(AppSettings settings, bool startupEnabled, bool openSettings = false)
    {
        PopulateTargets(settings.MonitorId);
        HdmiBox.SelectedIndex = settings.HdmiPort ?? 0;
        PcCodeBox.Text = $"0x{settings.PcCode:X2}";
        HdmiCodeBox.Text = settings.LaptopOverride is byte customCode ? $"0x{customCode:X2}" : string.Empty;
        OverrideBox.IsChecked = settings.AllowUnverifiedWrites;
        StartupBox.IsChecked = startupEnabled;
        PcKeyBox.Text = settings.PcHotkey.ToString();
        LaptopKeyBox.Text = settings.LaptopHotkey.ToString();
        ToggleKeyBox.Text = settings.ToggleHotkey.ToString();
        _hasLaptop = settings.HdmiPort is not null;
        LaptopButton.Content = settings.HdmiPort is int port ? $"Laptop · HDMI {port}" : "Laptop · HDMI";
        UpdateButtons();

        if (openSettings)
        {
            Pages.SelectedIndex = 1;
        }
    }

    public void SetTargets(IReadOnlyList<DisplayTarget> targets)
    {
        var selectedId = (MonitorBox.SelectedItem as MonitorOption)?.Id;
        _targets = targets;
        PopulateTargets(selectedId);
    }

    public void SetStatus(string monitor, string ddc, string source, bool hasTarget, bool canToggle)
    {
        MonitorText.Text = monitor;
        DdcText.Text = ddc;
        SourceText.Text = source;
        _hasTarget = hasTarget;
        _canToggle = canToggle;
        UpdateButtons();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    public void ShowMessage(string message)
    {
        if (StatusText.Text == message)
        {
            return;
        }

        StatusText.Text = message;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void PopulateTargets(string? selectedId)
    {
        var choices = new List<MonitorOption> { new(null, UiText.Text("Tự động chọn MSI G255F", "Automatically select MSI G255F")) };
        foreach (var group in _targets.Where(target => target.IsUnambiguous).GroupBy(target => target.Name))
        {
            var index = 0;
            foreach (var target in group)
            {
                var label = group.Count() == 1 ? target.Name : $"{target.Name} · {++index}";
                choices.Add(new MonitorOption(target.Id, label));
            }
        }

        var selected = choices.FindIndex(option => string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            selected = choices.Count;
            choices.Add(new MonitorOption(selectedId, UiText.Text("Màn hình đã lưu hiện không khả dụng", "Saved monitor unavailable")));
        }

        MonitorBox.ItemsSource = choices;
        MonitorBox.SelectedIndex = selected;
    }

    private void UpdateButtons()
    {
        PcButton.IsEnabled = !_busy && _hasTarget;
        LaptopButton.IsEnabled = !_busy && _hasTarget && _hasLaptop;
        ToggleButton.IsEnabled = !_busy && _hasTarget && _hasLaptop && _canToggle;
        ProbeButton.IsEnabled = !_busy;
        SaveButton.IsEnabled = !_busy;
    }

    private void PcClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(InputAction.Pc);
    private void LaptopClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(InputAction.Laptop);
    private void ToggleClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(InputAction.Toggle);
    private void ProbeClick(object sender, RoutedEventArgs e) => ProbeRequested?.Invoke();
    private void ShortcutsClick(object sender, RoutedEventArgs e) => ShortcutsRequested?.Invoke();

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryCode(PcCodeBox.Text, out var pcCode))
        {
            ShowMessage(UiText.Text("Nhập mã DisplayPort dưới dạng byte hex, ví dụ 0x0F.", "Enter a hexadecimal byte for DisplayPort, such as 0x0F."));
            PcCodeBox.Focus();
            return;
        }

        byte? laptopOverride = null;
        if (!string.IsNullOrWhiteSpace(HdmiCodeBox.Text))
        {
            if (!TryCode(HdmiCodeBox.Text, out var code))
            {
                ShowMessage(UiText.Text("Nhập mã HDMI dạng byte hex, ví dụ 0x12, hoặc để trống.", "Enter a hexadecimal byte for HDMI, such as 0x12, or leave it blank."));
                HdmiCodeBox.Focus();
                return;
            }

            laptopOverride = code;
        }

        if (!HotkeyChord.TryParse(PcKeyBox.Text, out var pc) ||
            !HotkeyChord.TryParse(LaptopKeyBox.Text, out var laptop) ||
            !HotkeyChord.TryParse(ToggleKeyBox.Text, out var toggle))
        {
            ShowMessage(UiText.Text("Kiểm tra lại ba phím tắt. Ví dụ tổ hợp hợp lệ: Ctrl+Alt+1.", "Check the three hotkeys. Example: Ctrl+Alt+1."));
            return;
        }

        var settings = new AppSettings
        {
            MonitorId = (MonitorBox.SelectedItem as MonitorOption)?.Id,
            HdmiPort = HdmiBox.SelectedIndex is 1 or 2 ? HdmiBox.SelectedIndex : null,
            PcCode = pcCode,
            LaptopOverride = laptopOverride,
            AllowUnverifiedWrites = OverrideBox.IsChecked == true,
            PcHotkey = pc,
            LaptopHotkey = laptop,
            ToggleHotkey = toggle
        };

        if (settings.Validate() is string error)
        {
            ShowMessage(error);
            return;
        }

        SettingsRequested?.Invoke(settings, StartupBox.IsChecked == true);
    }

    private static bool TryCode(string text, out byte code)
    {
        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        code = 0;
        return value.Length is 1 or 2 &&
               byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    private sealed record MonitorOption(string? Id, string Label)
    {
        public override string ToString() => Label;
    }
}
