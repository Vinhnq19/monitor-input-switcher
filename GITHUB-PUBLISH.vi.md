# Đưa bản LAN File Transfer lên GitHub

## Đẩy mã nguồn

Mở PowerShell tại `D:\MonitorInputSwitcher-source` và chạy:

```powershell
git status -sb
git push -u origin codex/lan-file-transfer
```

Mở trang so sánh trên GitHub và tạo pull request từ `codex/lan-file-transfer` vào `main`. Chọn **Create draft pull request** nếu vẫn đang kiểm tra trên PC và laptop. [Tạo pull request](https://github.com/Vinhnq19/monitor-input-switcher/compare/main...codex%2Flan-file-transfer?expand=1)

## Kiểm tra trên PC và ASUS

Chạy bản mới trên cả hai máy đã ghép đôi. Trong File Explorer, thử Ctrl+C và Ctrl+X với tệp nhỏ, thư mục có thư mục rỗng, tên tiếng Việt và nhiều mục cùng lúc. Dán qua cả hai chiều, đổi tab Explorer ngay trước khi dán, kiểm tra một mục trùng tên và thử phục hồi bản gốc đã cắt từ Thùng rác.

Thử một tệp lớn trong khi vẫn dùng chuột, bàn phím qua KVM. Ngắt Wi-Fi giữa một lượt cắt thử để xác nhận bản gốc còn trên máy gửi. Xem `%LOCALAPPDATA%\MonitorInputSwitcher\monitor.log` trên hai máy nếu có lỗi và đối chiếu độ trễ KVM khi rảnh với lúc đang truyền.

## Sửa mục About

Mở repository và bấm bánh răng cạnh **About**. Dán mô tả rồi chọn **Save changes**.

> Windows LAN KVM with keyboard, mouse, and file sharing between two PCs, plus DDC/CI monitor input switching.

## Tạo bản phát hành

1. Sau khi xác nhận trên hai máy và merge pull request, mở **Releases → Draft a new release**.
2. Tạo tag `v1.2.0-beta.1` trỏ tới commit trên `main` chứa tính năng. Đặt tiêu đề **Monitor Input Switcher v1.2.0 Beta 1**.
3. Đính kèm hai ZIP trong `publish\release-v1.2.0-beta.1`, rồi chọn **This is a pre-release**. ZIP portable được tải lên dưới dạng tài sản của Release. Git chứa mã nguồn, tài liệu và logo.
4. Dán nội dung bên dưới, kiểm tra lại tag và hai ZIP rồi bấm **Publish release**. [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)

> Copy or move files and folders between paired computers with Ctrl+C or Ctrl+X, then Ctrl+V in File Explorer. Supports multiple items and up to 10 GiB per transfer. Includes a new Windows app icon and portable x64 builds in Vietnamese and English. Update both computers to use file transfer.
