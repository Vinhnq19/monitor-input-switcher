# Kiến trúc và kiểm thử KVM qua LAN

Thiết bị đích gồm PC Windows kết nối Ethernet, bàn phím và chuột USB, màn hình MSI G255F qua DisplayPort, cùng ASUS ROG Strix G531 kết nối Wi-Fi và có màn hình tích hợp. Laptop có thể dùng thêm MSI G255F qua HDMI. Cả hai máy dùng cùng ứng dụng Windows x64.

## Các quyết định thiết kế

- PC cung cấp input vật lý. Laptop nhận input khi được chọn. Mép màn hình và phím tắt đổi máy đang nhận input. Trạng thái kết nối, quyền điều khiển và nguồn vật lý của MSI được quản lý riêng.
- Laptop tạo mã mời 5 phút dùng một lần. Mã chứa dấu vân tay chứng chỉ laptop và bí mật 128 bit. PC kiểm tra dấu vân tay trước khi gửi bí mật qua TLS. Laptop chỉ ghi nhận chứng chỉ PC sau khi kiểm tra mã. Các phiên điều khiển tiếp theo cần chứng chỉ đã ghép nối ở cả hai đầu. [Microsoft SslStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-8.0)
- Kết nối chỉ dùng IPv4 cùng subnet trên mạng Windows Private. UDP khám phá máy và địa chỉ sau khi DHCP thay đổi. Input đi qua một luồng TCP với `TCP_NODELAY` và khung nhị phân giới hạn kích thước. Đây là cấu hình cần đo đầu tiên. UDP cho chuyển động sẽ làm phát sinh yêu cầu đồng bộ vị trí với các nút chuột đi qua TCP. [Microsoft TcpClient.NoDelay](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.tcpclient.nodelay?view=net-8.0)
- Hook Win32 có message loop riêng. Callback kiểm tra trạng thái, ghi sự kiện vào hàng đợi giới hạn và trả về. Raw Input cung cấp chuyển động tương đối của chuột USB. Một worker ở laptop kiểm tra thứ tự, gọi `SendInput` và quản lý các phím, nút đã inject. Công việc mạng, log và DDC/CI chạy ngoài callback. [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)
- Hai báo cáo PowerToys cung cấp ca hồi quy. Issue 51328 mô tả ký tự đảo thứ tự và đưa ra giả thuyết về hai đường inject đồng thời. Issue 45471 mô tả chuột treo hoặc giật khi đi qua mép, ban đầu với thiết bị giống bảng vẽ. Các báo cáo này chưa xác định nguyên nhân sự cố Device Manager trên PC đích. [PowerToys #51328](https://github.com/microsoft/PowerToys/issues/51328), [PowerToys #45471](https://github.com/microsoft/PowerToys/issues/45471)
- Nếu phiên bị gián đoạn, các máy hủy quyền điều khiển từ xa và giải phóng những trạng thái input thuộc phiên. Sau khi reconnect, PC giữ quyền điều khiển cho đến lần bàn giao mới. Khi Windows hạn chế injection, ứng dụng báo lỗi và phục hồi theo các quyền của tiến trình đang chạy. [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

## Checklist trên hai máy

Ghi phiên bản EXE và Windows, cách kết nối mạng, độ phân giải, tỉ lệ DPI và màn hình nào đang nhận nguồn trên MSI. Dùng văn bản thử vô hại khi gõ. Sau mỗi ca, xác nhận PC vẫn dùng được bàn phím và chuột, kiểm tra các modifier và nút chuột đã nhả, rồi kiểm tra có thể thoát ứng dụng từ tray.

| Ca kiểm thử | Cách thực hiện và tiêu chí |
| --- | --- |
| Ghép nối | Thử khám phá và nhập IP thủ công. Mã đã dùng và mã hết hạn phải bị từ chối. Thu hồi trên laptop rồi thử kết nối lại. |
| Bốn hướng và DPI | Thử trái, phải, trên, dưới với laptop ở một tỉ lệ DPI khác PC. Con trỏ đến đúng cạnh và đúng vị trí tương đối. Mép nối với màn hình Windows khác và vùng bảo vệ phải xử lý đúng. |
| Gõ nhanh | Gõ cùng một đoạn dài ở PC và khi điều khiển laptop. So sánh ký tự, khoảng trắng và vị trí con trỏ. Thử Backspace, Delete, Enter, Tab, phím mũi tên, tổ hợp Ctrl và giữ phím để lặp. |
| Tiếng Việt | Trên laptop, thử UniKey với Telex và VNI, rồi thử Microsoft Vietnamese IME. Ghi lại bộ gõ, bố cục Windows và ứng dụng nhận input. Kiểm tra Caps Lock và Num Lock ở cả hai máy. |
| Chuột | Thử click, double click, chuột giữa, cuộn dọc/ngang và kéo thả trong từng máy. Giữ nút chuột trong khi đẩy đến mép. Thử chế độ khóa mép và tạm dừng toàn màn hình. |
| Mất Wi-Fi | Giữ một modifier hoặc nút chuột khi đang điều khiển laptop rồi ngắt Wi-Fi. Kiểm tra PC tự lấy lại input và laptop nhả trạng thái phiên. Khôi phục Wi-Fi, thử lại với IP DHCP mới. |
| Windows và quyền cao | Sau khi chuyển qua lại, mở Device Manager, Task Manager, Settings và đổi cửa sổ liên tục. Thử foreground quyền cao và hộp thoại UAC. Xác nhận đường thoát hệ thống và phím khẩn cấp còn dùng được. |
| Sleep, thoát và crash | Lặp lại với khóa màn hình, sleep/wake, thoát ứng dụng, dừng tiến trình và khởi động lại. Thử lần lượt từng máy khi đang điều khiển laptop. Kiểm tra kết quả một lần nữa sau phiên chạy nhiều giờ. |
| DDC/CI và topology | Thử Ctrl+Alt+1/2/3, Smart Switch hai chiều, và rút/cắm HDMI laptop. Ghi tách biệt lệnh gửi, VCP đọc lại và hình quan sát được trên MSI. Thử tình huống PC mất màn hình đang hiển thị. |

Lặp lại ca Windows và mất Wi-Fi ít nhất ba lượt. Giữ joystick OSD của MSI sẵn dùng trong ca DDC/CI.

## Ghi kết quả benchmark

| Phép đo | Phương pháp |
| --- | --- |
| TCP NoDelay | Dùng cùng kích thước payload, nhịp gửi và số mẫu để so sánh bật/tắt trong cùng điều kiện. Ghi p50, p95, p99 và mẫu lớn nhất. |
| Input delivery | Đo từ timestamp capture đến khi lệnh inject được chấp nhận. Báo sai số đồng hồ hoặc dùng capture đến ACK làm giới hạn trên. Tách số đo này khỏi thời gian hiển thị con trỏ thực tế. |
| Tải và phục hồi | Ghi RTT, jitter, độ sâu hàng đợi lớn nhất, số sự kiện gom, lỗi thứ tự, số lần khôi phục và thời gian reconnect. Đo CPU/RAM lúc rảnh và khi di chuyển nhanh. |

Baseline tại commit `03bb50f` có 184 khẳng định thành công và chưa có đường truyền LAN để đo. Đánh dấu kết quả loopback và lỗi mạng nhân tạo là **mô phỏng**. Mục tiêu trên hai máy thật là p95 dưới 25 ms trong điều kiện LAN/Wi-Fi tốt. Chỉ ghi đạt sau khi có kết quả từ PC và ASUS G531.
