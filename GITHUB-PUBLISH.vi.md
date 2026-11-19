# Đăng Monitor Input Switcher lên GitHub

## Đưa mã nguồn lên repository

1. Giải nén `MonitorInputSwitcher-source.zip` vào thư mục riêng. Mở PowerShell tại thư mục chứa `README.md`, `src` và `tests`.
2. Tạo repository trống trên GitHub, ví dụ `monitor-input-switcher`. Nếu GitHub hỏi có tạo README hoặc .gitignore hay không, để trống vì gói nguồn đã có các tệp này. Chọn chế độ công khai hoặc riêng tư theo ý bạn.
3. Chạy các lệnh sau. Thay `YOUR-USERNAME` bằng tài khoản hoặc tổ chức sở hữu repository và đổi tên repository nếu bạn đã chọn tên khác. Đăng nhập khi Git yêu cầu.

```powershell
git init -b main
git add .
git diff --cached --stat
git commit -m "Initial release of Monitor Input Switcher"
git remote add origin https://github.com/YOUR-USERNAME/monitor-input-switcher.git
git push -u origin main
```

`README.md` sẽ hiển thị hướng dẫn tiếng Anh ở trang chính. Liên kết **Tiếng Việt** mở `README.vi.md`. Chỉ đưa mã nguồn vào Git. Đính kèm các ZIP portable trong GitHub Releases.

## Mô tả ngắn cho mục About

> Switch your monitor between a DisplayPort PC and an HDMI laptop using DDC/CI, global hotkeys, the Windows system tray, or desktop shortcuts.

Chủ đề gợi ý `windows` · `ddc-ci` · `wpf` · `monitor` · `hotkeys`

## Tạo bản phát hành

Trong repository, mở **Releases**, tạo bản phát hành mới với tag `v1.0.0` trỏ tới nhánh `main`, rồi đính kèm:

- `MonitorInputSwitcher-portable-win-x64.zip` cho giao diện tiếng Việt.
- `MonitorInputSwitcher-en-portable-win-x64.zip` cho giao diện tiếng Anh.

**Tiêu đề** — Monitor Input Switcher v1.0.0

**Nội dung phát hành**

> Portable Windows x64 builds with English and Vietnamese interfaces. Switch between a DisplayPort PC and an HDMI laptop using global hotkeys, the tray menu, or desktop shortcuts.
>
> Extract the ZIP and run the EXE. In Settings, select the laptop’s HDMI port, then use Check connection before attempting the first switch. DDC/CI input switching depends on your monitor and connection. The MSI G255F read-only check detected and read the advertised input codes.

Sau khi đăng, mở lại trang repository để xác nhận README hiển thị đúng và thử tải cả hai ZIP từ bản phát hành.
