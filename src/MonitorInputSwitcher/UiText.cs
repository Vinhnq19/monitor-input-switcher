using System.Windows.Markup;

namespace MonitorInputSwitcher;

public static class UiText
{
#if APP_ENGLISH
    internal static bool IsEnglish => true;
#else
    internal static bool IsEnglish => false;
#endif

    internal static string Text(string vietnamese, string english) => IsEnglish ? english : vietnamese;

    public static XmlLanguage Language => XmlLanguage.GetLanguage(Text("vi-VN", "en-US"));

    public static string Controls => Text("Điều khiển", "Controls");
    public static string Sharing => Text("Chuột và bàn phím", "Mouse & keyboard");
    public static string Monitor => Text("Màn hình", "Monitor");
    public static string Detecting => Text("Đang nhận diện…", "Detecting…");
    public static string Unchecked => Text("Chưa kiểm tra", "Unchecked");
    public static string CurrentInput => Text("Nguồn hiện tại", "Current input");
    public static string Unknown => Text("Chưa xác định", "Unknown");
    public static string CheckConnection => Text("Kiểm tra kết nối", "Check connection");
    public static string Settings => Text("Cài đặt", "Settings");
    public static string TargetMonitor => Text("Màn hình điều khiển", "Target monitor");
    public static string LaptopPort => Text("Cổng HDMI của laptop", "Laptop HDMI port");
    public static string SelectPort => Text("Chọn cổng HDMI…", "Select an HDMI port…");
    public static string DisplayPortCode => Text("Mã DisplayPort", "DisplayPort code");
    public static string CustomHdmiCode => Text("Mã HDMI tùy chỉnh", "Custom HDMI code");
    public static string CustomHdmiHint => Text("Để trống để dùng mã của cổng HDMI đã chọn", "Leave blank to use the selected HDMI port's default code");
    public static string AllowUnverified => Text("Cho phép thử mã nguồn chưa xác minh", "Allow unverified input writes");
    public static string StartWithWindows => Text("Khởi động ẩn cùng Windows", "Start hidden with Windows");
    public static string Hotkeys => Text("Phím tắt toàn hệ thống", "Global hotkeys");
    public static string HotkeyExample => Text("Ví dụ Ctrl+Alt+1 hoặc Ctrl+Shift+F9", "Example: Ctrl+Alt+1 or Ctrl+Shift+F9");
    public static string SaveSettings => Text("Lưu cài đặt", "Save settings");
    public static string DesktopShortcuts => Text("Tạo shortcut Desktop", "Create desktop shortcuts");
    public static string Ready => Text("Sẵn sàng kiểm tra kết nối.", "Ready to check the connection.");
}
