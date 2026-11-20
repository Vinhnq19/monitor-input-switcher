using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static MonitorInputSwitcher.UiText;

namespace MonitorInputSwitcher;

public sealed class LanSharingPanel : UserControl
{
    private readonly ComboBox _role = new();
    private readonly ComboBox _edge = new();
    private readonly ComboBox _screen = new();
    private readonly TextBox _address = new();
    private readonly TextBox _invite = new();
    private readonly CheckBox _enabled = Box("Bật chia sẻ", "Enable sharing");
    private readonly CheckBox _keyboard = Box("Bàn phím đi theo chuột", "Keyboard follows mouse");
    private readonly CheckBox _edges = Box("Chuyển qua mép", "Switch at screen edges");
    private readonly CheckBox _guard = Box("Chống chuyển nhầm ở góc", "Guard screen corners");
    private readonly CheckBox _lock = Box("Khóa chuyển qua mép", "Lock screen edges");
    private readonly CheckBox _full = Box("Tạm dừng khi toàn màn hình", "Pause in full screen");
    private readonly CheckBox _auto = Box("Tự kết nối lại", "Reconnect automatically");
    private readonly CheckBox _smart = Box("Smart Switch cũng đổi nguồn MSI", "Smart Switch also changes MSI input");
    private readonly CheckBox _peer = Box("Cho phép máy đã ghép đổi nguồn MSI", "Allow paired device to change MSI input");
    private readonly TextBox _threshold = new();
    private readonly TextBox[] _chords = [new(), new(), new(), new()];
    private readonly TextBlock _connection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _metrics = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _layout = new() { Height = 108, AllowDrop = true, Margin = new Thickness(0, 6, 0, 3), Background = Brushes.Transparent };
    private readonly Border _pcTile;
    private readonly Border _laptopTile;
    private readonly TextBlock _path = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private bool _updating;
    private string? _savedScreen;

    public LanSharingPanel()
    {
        var content = new StackPanel { Margin = new Thickness(0, 10, 3, 8) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Heading(content, Text("Kết nối thiết bị", "Device connection"));
        _role.ItemsSource = new[] { Text("PC · Chuột và bàn phím đang cắm", "PC · Physical mouse and keyboard"), Text("Laptop · Nhận điều khiển", "Laptop · Receives input") };
        Row(content, Text("Vai trò máy này", "This computer's role"), _role);
        Row(content, Text("IPv4 laptop (không bắt buộc)", "Laptop IPv4 (optional)"), _address);
        Row(content, Text("Mã mời ghép đôi", "Pairing invitation"), _invite);
        content.Children.Add(Buttons(
            (Text("Tạo mã", "Create invite"), "invite"),
            (Text("Ghép đôi", "Pair"), "pair"),
            (Text("Ngắt kết nối", "Disconnect"), "disconnect"),
            (Text("Kết nối lại", "Reconnect"), "reconnect")));
        content.Children.Add(Buttons((Text("Kiểm tra", "Test connection"), "test"),
            (Text("Thu hồi ghép đôi", "Unpair"), "unpair")));
        content.Children.Add(_connection);
        content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        Heading(content, Text("Bố cục màn hình", "Screen layout"));
        Row(content, Text("Màn hình ở máy này", "Screen on this computer"), _screen);
        _screen.SelectionChanged += (_, _) =>
        {
            if (!_updating)
            {
                _savedScreen = (_screen.SelectedItem as ScreenChoice)?.Name;
            }
        };
        _edge.ItemsSource = new[] { Text("Bên trái PC", "Left of PC"), Text("Bên phải PC", "Right of PC"),
            Text("Phía trên PC", "Above PC"), Text("Phía dưới PC", "Below PC") };
        Row(content, Text("Vị trí laptop", "Laptop position"), _edge);
        for (int axis = 0; axis < 3; axis++)
        {
            _layout.RowDefinitions.Add(new RowDefinition());
            _layout.ColumnDefinitions.Add(new ColumnDefinition());
        }
        _pcTile = Tile("PC");
        _laptopTile = Tile("Laptop");
        Grid.SetRow(_pcTile, 1);
        Grid.SetColumn(_pcTile, 1);
        _layout.Children.Add(_pcTile);
        _layout.Children.Add(_laptopTile);
        _pcTile.Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "PC", HorizontalAlignment = HorizontalAlignment.Center }, _path }
        };
        _laptopTile.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(_laptopTile, new DataObject("MonitorInputSwitcher.Layout", "Laptop"), DragDropEffects.Move);
            }
        };
        _layout.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("MonitorInputSwitcher.Layout") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        _layout.Drop += (_, e) =>
        {
            if (e.Data.GetDataPresent("MonitorInputSwitcher.Layout"))
            {
                var point = e.GetPosition(_layout);
                double x = point.X / _layout.ActualWidth - 0.5;
                double y = point.Y / _layout.ActualHeight - 0.5;
                _edge.SelectedIndex = Math.Abs(x) >= Math.Abs(y) ? x < 0 ? 0 : 1 : y < 0 ? 2 : 3;
            }
        };
        _edge.SelectionChanged += (_, _) => UpdatePreview();
        content.Children.Add(_layout);

        Heading(content, Text("Chia sẻ đầu vào", "Input sharing"));
        foreach (var option in new[] { _enabled, _keyboard, _edges, _guard, _lock, _full, _auto, _smart, _peer })
        {
            content.Children.Add(option);
        }
        Row(content, Text("Độ nhạy mép (1–32, số nhỏ nhạy hơn)", "Edge threshold (1–32, lower is more sensitive)"), _threshold);
        Row(content, Text("Về PC", "Control PC"), _chords[0]);
        Row(content, Text("Sang laptop", "Control laptop"), _chords[1]);
        Row(content, Text("Bật/tắt chia sẻ", "Toggle sharing"), _chords[2]);
        Row(content, Text("Khôi phục khẩn cấp", "Emergency recovery"), _chords[3]);
        content.Children.Add(Buttons((Text("Lưu và áp dụng", "Save and apply"), "save"),
            (Text("Điều khiển PC", "Control PC"), "pc"), (Text("Điều khiển laptop", "Control laptop"), "laptop")));

        Heading(content, Text("Chẩn đoán", "Diagnostics"));
        content.Children.Add(_metrics);
        content.Children.Add(Buttons((Text("Xuất báo cáo", "Export report"), "export")));
    }

    internal event Action<string>? Invoked;
    internal string Invitation { get => _invite.Text; set => _invite.Text = value; }

    internal void UpdateConnection(string status, string metrics)
    {
        _connection.Text = status;
        _metrics.Text = metrics;
    }

    internal void SetMessage(string message) => _status.Text = message;

    internal void Fill(LanSharingOptions options)
    {
        _role.SelectedIndex = (int)options.Role;
        _edge.SelectedIndex = (int)options.Edge;
        _address.Text = options.ManualAddress ?? "";
        _enabled.IsChecked = options.Enabled;
        _keyboard.IsChecked = options.KeyboardFollows;
        _edges.IsChecked = options.EdgeSwitch;
        _guard.IsChecked = options.CornerGuard;
        _lock.IsChecked = options.LockCursor;
        _full.IsChecked = options.PauseFullScreen;
        _auto.IsChecked = options.AutoReconnect;
        _smart.IsChecked = options.SmartDdc;
        _peer.IsChecked = options.PeerDdc;
        _threshold.Text = options.Threshold.ToString(CultureInfo.InvariantCulture);
        _savedScreen = options.Screen;
        for (int index = 0; index < _chords.Length; index++)
        {
            _chords[index].Text = options.Chords[index].ToString();
        }
    }

    internal void UpdateScreens(IEnumerable<LanScreen> screens)
    {
        _updating = true;
        var choices = new[] { new ScreenChoice(null, Text("Tự động", "Automatic")) }
            .Concat(screens.Select(screen => new ScreenChoice(screen.Name, screen.ToString()))).ToList();
        if (_savedScreen is { } missing && !choices.Any(choice => choice.Name == missing))
        {
            choices.Add(new ScreenChoice(missing, $"{missing} · {Text("Không khả dụng", "Unavailable")}"));
        }
        _screen.ItemsSource = choices;
        _screen.SelectedItem = choices.Single(choice => choice.Name == _savedScreen);
        _updating = false;
    }

    internal LanSharingOptions? ReadOptions()
    {
        if (!int.TryParse(_threshold.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold) ||
            !HotkeyChord.TryParse(_chords[0].Text, out var pc) ||
            !HotkeyChord.TryParse(_chords[1].Text, out var laptop) ||
            !HotkeyChord.TryParse(_chords[2].Text, out var toggle) ||
            !HotkeyChord.TryParse(_chords[3].Text, out var emergency))
        {
            return null;
        }
        return new LanSharingOptions
        {
            Role = (LanRole)_role.SelectedIndex, Edge = (LanEdge)_edge.SelectedIndex,
            ManualAddress = string.IsNullOrWhiteSpace(_address.Text) ? null : _address.Text.Trim(),
            Screen = _savedScreen, Enabled = _enabled.IsChecked == true, KeyboardFollows = _keyboard.IsChecked == true,
            EdgeSwitch = _edges.IsChecked == true, CornerGuard = _guard.IsChecked == true,
            LockCursor = _lock.IsChecked == true, PauseFullScreen = _full.IsChecked == true,
            AutoReconnect = _auto.IsChecked == true, SmartDdc = _smart.IsChecked == true, PeerDdc = _peer.IsChecked == true,
            Threshold = threshold, ToPc = pc, ToLaptop = laptop, Toggle = toggle, Emergency = emergency
        };
    }

    private void UpdatePreview()
    {
        var edge = (LanEdge)_edge.SelectedIndex;
        Grid.SetRow(_laptopTile, edge == LanEdge.Top ? 0 : edge == LanEdge.Bottom ? 2 : 1);
        Grid.SetColumn(_laptopTile, edge == LanEdge.Left ? 0 : edge == LanEdge.Right ? 2 : 1);
        _path.Text = LanGeometry.IsHorizontal(edge) ? "↔" : "↕";
    }

    private WrapPanel Buttons(params (string Label, string Command)[] buttons)
    {
        var group = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (label, command) in buttons)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
            button.Click += (_, _) => Invoked?.Invoke(command);
            group.Children.Add(button);
        }
        return group;
    }

    private static void Heading(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 6)
    });

    private static void Row(Panel parent, string label, Control control)
    {
        parent.Children.Add(new Label { Content = label, Target = control });
        parent.Children.Add(control);
    }

    private static CheckBox Box(string vi, string en) => new() { Content = Text(vi, en) };

    private static Border Tile(string text) => new()
    {
        BorderBrush = SystemColors.HighlightBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Margin = new Thickness(3),
        Child = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };

    private sealed record ScreenChoice(string? Name, string Label)
    {
        public override string ToString() => Label;
    }
}
