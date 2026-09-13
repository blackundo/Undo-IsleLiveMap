# Kế hoạch triển khai Isle Live Map sau 1.5.2

Ngày lập: 2026-09-12
Trạng thái: đang triển khai, chưa phát hành release

## Mục tiêu

1. Khôi phục stats ổn định trên server không được IslePilot hỗ trợ, bắt đầu bằng Gacha.
2. Tích hợp bộ Việt hóa đã bàn giao và một sổ tay Mutation native, an toàn.
3. Làm thao tác xóa mốc trên bản đồ lớn rõ ràng và đáng tin cậy.
4. Cho phép đổi phím tắt, phát hiện xung đột và phục hồi an toàn.
5. Cho phép bật/tắt riêng Map, Status, Team và Prime, đồng thời ghi nhớ lựa chọn.
6. Cập nhật modal giới thiệu đúng các tính năng thực sự đã hoàn thành.
7. Thêm modal dịch vụ KLong.dev riêng, dành cho cả Free và Pro, chỉ đóng sau 5 giây.

## Quy ước thao tác không được nhầm

- `Ctrl + Shift + O`: vào/thoát **Edit Mode** để di chuyển, resize và bật/tắt từng block.
- `Alt + P`: ẩn/hiện **toàn bộ HUD** tạm thời; không đổi layout hoặc lựa chọn block.
- `Alt + M`: mở bản đồ lớn và đặt/xóa mốc (Pro).
- `Alt + U`: mở sổ tay Mutation mặc định; đi qua hệ thống remap mới.
- Phím tắt hiển thị trong UI/hướng dẫn phải lấy từ cấu hình runtime, không sao chép chuỗi cứng.

## Kiến trúc và thứ tự triển khai

### P0 — Input an toàn và xóa mốc

- Luôn bật click-through khi khởi động, kể cả khi đăng ký `Ctrl + Shift + O` thất bại.
- Tạo model cấu hình phím tắt có version, bộ đăng ký hotkey test được và thao tác lưu kiểu transaction/rollback.
- Phát hiện trùng trong ứng dụng, trùng với phím hệ thống và báo lỗi cụ thể; dùng `MOD_NOREPEAT`.
- Thêm màn hình `PHÍM TẮT` dùng chung tại Home và toolbar Edit Mode.
- Thêm nút `XÓA MỐC`, phím `Delete` và menu chuột phải cho mốc có quyền xóa.
- Giữ lỗi xóa hiển thị bền, không để lần render kế tiếp ghi đè; phân biệt rõ mốc đồng đội không thuộc quyền sở hữu.
- Không nuốt lỗi lưu local; xóa thành công phải còn đúng sau khi mở lại ứng dụng.

Tiêu chí nghiệm thu:

- Xung đột hotkey không làm overlay bắt chuột toàn màn hình.
- `Ctrl + Shift + O` và `Alt + P` luôn là hai action độc lập.
- Xóa mốc local tồn tại qua restart; xóa ping của chủ sở hữu đồng bộ tới peer; non-owner thấy trạng thái khóa.

### P1 — Việt hóa và sổ tay Mutation

- Dùng artifact locres đã xác minh từ handoff làm đầu vào; bản phát hành hiện tại
  được rebrand bằng `tools/rebrand_localization.py` thành credit **Phát triển bởi
  Isle Live Map**. Handoff 1.1.4 được giữ nguyên để bảo toàn provenance/checksum.
- Client WPF có selector `English | Tiếng Việt`; không dùng copy “cài/gỡ” nhập nhằng.
- Chỉ thay resource khi game và bootstrap đã dừng.
- Bắt buộc HTTPS allowlist, kiểm SHA-256 ZIP và từng file, chặn ZIP traversal, giới hạn đúng 10 target, backup và rollback nguyên tử.
- Backend ASP.NET Core hiện tại cung cấp manifest `no-store` và ZIP immutable; không tạo service/DB mới.
- Mutation guide là cửa sổ native, mở thủ công bằng hotkey tùy chỉnh; tự đóng khi game thoát/disconnect.
- Chưa tự hiện theo tab Mutation. Chỉ bật auto-show sau capture A/B chứng minh được signature ổn định.

Tiêu chí nghiệm thu:

- Artifact hợp lệ cài được và đổi culture; hash sai/path lạ/file thừa đều bị chặn và rollback.
- Game đang chạy thì client không sửa resource.
- Sổ tay có đủ 43 Mutation, tìm kiếm được và không cần inject/read memory.

### P2 — Bật/tắt từng block và ghi nhớ

- Nâng layout schema từ v4, thêm preference cho Map, Status, Team và Prime.
- Công thức hiển thị: `HUD tổng && người dùng bật && (có dữ liệu || đang Edit Mode)`.
- Edit toolbar luôn còn đường phục hồi block đã ẩn và có `HIỆN TẤT CẢ`.
- Reset layout không reset preference hiển thị.
- `Alt + P` chỉ là master visibility tạm thời, không ghi đè preference.

Tiêu chí nghiệm thu:

- Vị trí, scale, shape và visibility trở lại đúng sau restart.
- Team/Prime không có dữ liệu vẫn hiện placeholder trong Edit Mode để người dùng thao tác.

### P3 — Modal thông báo và quảng cáo

- Release highlights chia trang theo nhóm tính năng đã ship, gắn nhãn Free/Pro và dùng screenshot UI thật khi phù hợp.
- Trang phím tắt đặt song song `CTRL+SHIFT+O / EDIT MODE` và `ALT+P / TOÀN HUD` để loại bỏ nhầm lẫn.
- Modal dịch vụ KLong.dev là modal riêng, không nằm trong release highlights.
- Hiển thị một lần mỗi lần chạy app cho cả Free/Pro; countdown dựa trên thời gian monotonic, khóa X/Escape/Alt+F4 đủ 5 giây.
- Video dùng YouTube privacy-enhanced embed, không autoplay âm thanh, lazy-load, whitelist navigation và dispose WebView2 khi đóng.
- CTA chính tới Facebook; hiển thị Zalo/Phone `0705878781`; game examples dùng asset local có ghi nguồn hoặc monogram không vi phạm artwork.

Tiêu chí nghiệm thu:

- Không thể đóng quảng cáo trước 5 giây, nhưng không gây crash nếu thiếu WebView2/network.
- Không lặp modal quảng cáo khi chỉ quay lại Home trong cùng process.
- Release modal không quảng cáo dịch vụ và chỉ mô tả tính năng đã qua test.

### P4 — Stats đa nguồn, bắt đầu với Gacha

- Không sao chép token, cookie, protocol handler hoặc API riêng của Gacha.
- Tạo abstraction provider theo session:
  1. IslePilot khi `HasData=true`.
  2. Adapter server chính thức nếu chủ server cung cấp API/documentation.
  3. Iris/GAS inbound local làm fallback có confidence và provenance.
- Cache khóa bởi game PID + endpoint + self actor handle; sparse/null đơn lẻ không xóa stats đã xác minh.
- Reset khi process, endpoint, respawn hoặc actor handle đổi.
- Inbound chạy lane bounded riêng, không chặn outbound GPS hoặc UI.
- Không hiển thị float suy đoán; từng chỉ số có freshness/source/confidence riêng.

Tiêu chí nghiệm thu:

- IslePilot-supported vẫn không regression.
- Server Gacha không còn bị gắn nhãn “lỗi login IslePilot”; UI nói rõ đang dùng nguồn nào/đang chờ sync gì.
- Fallback chỉ bật sau fixture từ capture thật và A/B validation; health/stamina/hunger/water không dùng max cache của dino cũ.

### P5 — Kiểm thử và RC

- Chạy toàn bộ unit/integration tests của public client, Pro client và backend.
- Smoke test WPF: startup Free/Pro, hết hạn Pro, hotkey conflict, map launch, disconnect/reconnect.
- Benchmark CPU/frame-time khi overlay khóa và trong Edit Mode.
- Kiểm thử localization trên bản game build khớp manifest và rollback cưỡng bức.
- Capture stats tối thiểu trên IslePilot-supported và Gacha.
- Chỉ tạo release khi developer nghiệm thu và yêu cầu phát hành riêng.

## Phối hợp với developer

- AI tự triển khai phần code, unit/integration tests và mô phỏng lỗi có thể tái tạo.
- Developer chỉ cần hỗ trợ các bước cần trạng thái game thật: vào Gacha, báo thời điểm spawn/respawn/đổi dino và thực hiện chuỗi thao tác Mutation khi được yêu cầu.
- Mỗi capture phải ghi rõ PID, endpoint, mốc thời gian, action và nguồn dữ liệu để tránh suy diễn từ ảnh màn hình.
- Nếu bằng chứng làm sai giả thuyết hiện tại, kế hoạch được cập nhật; không cố giữ decoder/layout đã đoán sai.

## Ranh giới phát hành

- Không tăng version, không tạo tag, không push release và không deploy VPS trong giai đoạn này.
- Chỉ merge/ship phần nào đã đạt tiêu chí nghiệm thu tương ứng.
