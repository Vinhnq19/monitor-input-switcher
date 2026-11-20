# Monitor Input Switcher

[English](README.md) · [Hướng dẫn đăng lên GitHub](GITHUB-PUBLISH.vi.md)

Monitor Input Switcher điều khiển nguồn vào của màn hình từ Windows qua DDC/CI. Với MSI G255F, bạn có thể dùng DisplayPort cho PC và HDMI cho laptop. EXE portable dành cho Windows 10/11 x64, mang theo runtime .NET và chạy ngoại tuyến dưới tài khoản Windows hiện tại.

## Thiết lập trên hai máy

1. Kết nối PC vào DisplayPort và laptop ASUS vào HDMI 1 hoặc HDMI 2 trên G255F. Ghi lại số cổng HDMI đang cắm và đánh thức cả hai máy.
2. Trên mỗi máy, đặt `MonitorInputSwitcher.exe` ở một thư mục cố định rồi mở ứng dụng. Nếu dùng bản portable ZIP, giải nén trước khi chạy.
3. Dùng joystick OSD để hiển thị máy đang thiết lập. Trong **Cài đặt**, giữ **Tự động chọn MSI G255F** nếu chỉ có một G255F hoặc chọn đúng màn hình. Chọn **HDMI 1** hoặc **HDMI 2** cho laptop rồi bấm **Lưu cài đặt**. Thực hiện trên cả PC lẫn laptop. Mỗi máy lưu cấu hình riêng.
4. Trong tab **Điều khiển**, bấm **Kiểm tra kết nối**. Kết quả gồm tên màn hình, khả năng đọc DDC/CI và mã nguồn hiện tại nếu màn hình trả lời.
5. Từ máy đang hiển thị, thử nút chuyển sang máy kia. Sau đó dùng bàn phím hoặc ứng dụng của máy vừa được hiển thị để chuyển ngược lại.

| Phím tắt mặc định | Thao tác |
| --- | --- |
| `Ctrl+Alt+1` | Chuyển sang PC qua DisplayPort |
| `Ctrl+Alt+2` | Chuyển sang laptop qua cổng HDMI đã chọn |
| `Ctrl+Alt+3` | Chuyển giữa hai nguồn đã cấu hình khi đọc được nguồn hiện tại |

Phím tắt thuộc máy đang nhận thao tác bàn phím. Trong **Cài đặt**, nhập tổ hợp gồm ít nhất một phím `Ctrl`, `Alt`, `Shift` hoặc `Win`, cùng với một chữ cái A–Z, chữ số 0–9 hoặc phím F1–F24. Windows giữ riêng F12 và có thể giữ các tổ hợp khác. Bạn cũng có thể sử dụng menu ở khay hệ thống, bật khởi động cùng Windows hoặc tạo hai shortcut trên Desktop. Nếu di chuyển EXE, hãy tạo lại shortcut và bật lại tùy chọn khởi động để cập nhật đường dẫn.

Đóng hoặc thu nhỏ cửa sổ sẽ ẩn ứng dụng xuống khay. Mở lại EXE hoặc nhấp đúp biểu tượng khay để hiện cửa sổ. Dùng **Thoát** trong menu khay để ngừng chạy và giải phóng các phím tắt.

Hai bản ngôn ngữ dùng chung cấu hình và phiên chạy. Thoát bản đang mở từ menu khay trước khi đổi sang bản ngôn ngữ còn lại. Tùy chọn khởi động cùng Windows mặc định tắt.

## Kiểm tra DDC/CI và chọn mã nguồn

Mở OSD bằng joystick của màn hình. Nếu firmware có mục **DDC/CI**, đặt thành **On** và chạy lại **Kiểm tra kết nối**. Nếu OSD không có mục này, dùng kết quả kiểm tra trong ứng dụng để xem màn hình có trả lời qua kết nối hiện tại hay không.

| Cổng | Mã ban đầu |
| --- | --- |
| DisplayPort | `0x0F` |
| HDMI 1 | `0x11` |
| HDMI 2 | `0x12` |

Đây là các giá trị của tính năng MCCS Input Source (`0x60`). G255F trong phép thử đọc ban đầu đã công bố cả ba giá trị. Đối chiếu mã đang đọc được với cổng đang hiển thị trước khi dùng mã tùy chỉnh. Chỉ bật chế độ thử mã chưa xác minh khi biết mã cần thử và có thể chuyển lại bằng joystick.

Phép thử trên ASUS ROG Strix G513IH xác nhận mã của ứng dụng phát hiện một MSI G255F giữa hai màn hình đang kết nối, đọc được `0x12` và đọc được ba giá trị trong bảng. Máy kiểm tra khác mẫu G531 trong cấu hình đích. Để xác nhận đổi nguồn, thực hiện các phép thử hai chiều ở phần dưới.

## Khôi phục khi mất tín hiệu

- Nếu màn hình đen hoặc báo mất tín hiệu, đánh thức máy đích. Dùng joystick OSD để chọn lại cổng của máy đang hoạt động nếu hình chưa trở lại.
- **Đã gửi lệnh, chưa xác nhận** nghĩa là Windows đã chấp nhận lời gọi ghi nhưng ứng dụng chưa đọc lại được giá trị đích. Màn hình có thể ngừng trả lời cho máy cũ ngay khi đổi nguồn. Quan sát hình hoặc tên nguồn trong OSD để kiểm tra kết quả.
- Nếu không đọc được nguồn hiện tại, dùng nút hoặc phím tắt chỉ định đích. Toggle cần một lần đọc thành công và giá trị phải thuộc cặp nguồn đã cấu hình.
- Nếu kết nối trực tiếp vẫn không phản hồi DDC/CI, dùng joystick để chuyển nguồn. Nếu có nhiều màn hình cùng tên, chọn đúng thiết bị trong Cài đặt. Kết nối lại và chọn lại nếu Windows cấp mã định danh khác cho màn hình.
- Khi tổ hợp phím bị chiếm, đặt một tổ hợp khác. Cấu hình và nhật ký nằm tại `%LOCALAPPDATA%\MonitorInputSwitcher`.

## Kiểm tra phần cứng trên PC và laptop

| Bước | Kết quả cần ghi |
| --- | --- |
| Trên PC, đang hiển thị qua DisplayPort, bấm **Kiểm tra kết nối** | Tên thiết bị, chuỗi khả năng nếu đọc được và giá trị `0x60` |
| Từ PC, bấm phím tắt Laptop trong khi cả hai máy đang thức | Nhật ký gửi, giá trị đọc lại nếu có và màn hình thực tế hiển thị laptop hay không |
| Trên laptop, bấm phím tắt PC | Nhật ký gửi, giá trị đọc lại nếu có và màn hình thực tế hiển thị PC hay không |
| Ẩn ứng dụng xuống khay và lặp lại với Toggle | Toggle chỉ gửi đích ngược khi giá trị hiện tại thuộc cặp đã cấu hình |

Nếu việc chuyển nguồn có thể làm gián đoạn phiên làm việc, lưu công việc đang mở và giữ quyền thao tác joystick trước khi thử.

## Build và kiểm thử từ mã nguồn

Mở PowerShell tại thư mục chứa README trên Windows với .NET SDK hỗ trợ `net8.0-windows`.

```powershell
dotnet run --project .\tests\MonitorInputSwitcher.Checks -c Release
dotnet run --project .\tests\MonitorInputSwitcher.Checks -c Release -- --probe
dotnet publish .\src\MonitorInputSwitcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

Lệnh đầu chạy các kiểm tra mô phỏng. Lệnh thứ hai chỉ đọc các màn hình Windows đang thấy và VCP `0x60` trên MSI G255F. Nếu MSBuild trong môi trường bị giới hạn báo lỗi tra cứu tên nền tảng Windows trong Registry, thêm `-p:TargetPlatformDisplayName=Windows` trước `--`.


## Kết quả kiểm thử

- Bộ kiểm thử tự động qua 184 khẳng định, bao gồm Toggle hai chiều bằng dữ liệu mô phỏng, mất kết nối, lỗi ghi, cấu hình HDMI và khôi phục cài đặt.
- Phép thử chỉ đọc phần cứng phát hiện hai màn hình, trong đó có một MSI G255F. Màn hình báo nguồn `0x12` và công bố `0x11`, `0x12`, `0x0F`.
- Trong phiên desktop Windows 11, đã mở giao diện, nhận phím `Ctrl+Alt+2` ở trạng thái chưa cấu hình HDMI và ẩn rồi mở lại chính cửa sổ đang chạy.
- Bản EXE tự chứa đã chạy thử với `--tray`.
- Cần thử đổi nguồn thật theo cả hai chiều trên PC và laptop đích. Cần xác minh riêng tính năng khởi động cùng Windows, tạo shortcut và chạy trên Windows 10 nếu sử dụng hệ điều hành này.

## Chia sẻ chuột và bàn phím qua LAN

1. Chạy cùng phiên bản trên hai máy. Đặt hồ sơ mạng Windows trên mỗi máy thành **Private** nếu bạn tin cậy mạng đó. Hai địa chỉ IPv4 phải cùng subnet.
2. Trên laptop, mở **Chuột và bàn phím**, chọn vai trò **Laptop** rồi bấm **Tạo mã**. Chuyển mã trực tiếp sang PC trong vòng 5 phút.
3. Trên PC, chọn vai trò **PC**, dán mã và bấm **Ghép đôi**. Nếu không tìm thấy laptop, nhập IPv4 của laptop và thử lại. **Kiểm tra** xác nhận phiên TLS và hiển thị RTT.
4. Đặt **Vị trí laptop** trên PC và bấm **Lưu và áp dụng**. Chọn màn hình ở từng máy nếu lựa chọn tự động chưa đúng. Đẩy con trỏ qua mép ngoài hướng về laptop. Đẩy qua mép đối diện để quay về PC. Thả các phím và nút chuột trước khi đi qua mép.

Chia sẻ mặc định tắt. **Ngắt kết nối** dừng phiên và giữ quyền ghép đôi. **Kết nối lại** tiếp tục chia sẻ. **Thu hồi ghép đôi** xóa quyền trên máy hiện tại. Xóa quyền ở cả hai máy khi ghép lại từ đầu. Nếu IP laptop đổi sau khi mất Wi-Fi, giữ tùy chọn tự kết nối lại để PC tìm IP mới. Nhập IP mới nếu router chặn khám phá.

| Phím tắt chia sẻ | Thao tác |
| --- | --- |
| `Ctrl+Alt+Shift+Left` | Trả quyền điều khiển về PC |
| `Ctrl+Alt+Shift+Right` | Bàn giao quyền điều khiển sang laptop |
| `Ctrl+Alt+Shift+M` | Bật hoặc tắt chia sẻ |
| `Ctrl+Alt+Shift+Esc` | Trả quyền về máy cục bộ và tạm dừng bàn giao đến khi kết nối lại hoặc lưu tùy chọn |

Đổi phím tắt trong **Chuột và bàn phím**. Chọn tổ hợp khác phím DDC/CI. **Khóa chuyển qua mép** giữ quyền ở máy hiện tại cho đến khi chuyển chủ động. Thao tác vật lý trên laptop cũng kết thúc lần điều khiển từ xa. Dùng thiết bị vật lý trên máy đích cho cửa sổ quyền cao hoặc Secure Desktop.

Bạn có thể bật **Smart Switch cũng đổi nguồn MSI** để đổi nguồn sau khi bàn giao input. Khi máy cục bộ chưa thể gửi lệnh, máy đã ghép đôi có thể xử lý nếu đã bật quyền DDC/CI cho máy kia. Giao diện báo kết quả DDC/CI riêng. Kiểm tra hình trên MSI sau khi đổi nguồn.

### Firewall và lỗi kết nối

- Trên laptop, cho phép TCP **49351** vào đúng EXE, hồ sơ **Private**, phạm vi **LocalSubnet**. Để tự tìm máy, cho phép thêm UDP **49351** với cùng phạm vi. Tạo rule trong Windows Defender Firewall with Advanced Security nếu cần. Cập nhật đường dẫn trong rule nếu di chuyển EXE portable.
- Nếu nhập IP được nhưng tìm tự động thất bại, kiểm tra rule UDP hoặc thiết lập multicast trên router. Nếu nhập IP cũng hết thời gian, kiểm tra subnet, guest Wi-Fi, client isolation và rule TCP laptop.
- **Chẩn đoán** hiển thị RTT, jitter, thống kê hàng đợi, thời gian kết nối lại và độ trễ capture-to-ACK lấy mẫu. Phép đo trên PC bao gồm cả phản hồi nên là giới hạn trên của thời gian chuyển đến `SendInput`. Báo cáo xuất gồm số liệu và mã lỗi.

Chạy benchmark loopback bằng `dotnet run --project .\tests\MonitorInputSwitcher.Checks -c Release -- --lan-benchmark`. Tài liệu sau có kết quả mô phỏng và checklist kiểm tra PC, ASUS G531 và MSI. [Kiến trúc LAN và checklist hai máy](docs/lan-kvm.md)
