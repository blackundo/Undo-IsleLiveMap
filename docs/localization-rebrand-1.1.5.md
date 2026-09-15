# Việt hóa game — rebrand credit 1.1.5

## Thay đổi

Entry `2E0D8DC24EE8F1B6312F19A279F948C0` trong `Game.locres` hiện hiển thị:

```text
Chào mừng đến với THE ISLE
Phát triển bởi Isle Live Map
```

Đã cập nhật đồng thời hai locale `vi` và `vi-VN`. Chuỗi Discord/ThePrimalVN
không còn trong hai resource này; các chuỗi Discord chung của game không bị
đụng tới.

## Artifact đã kiểm tra

- ZIP: `output/localization-release-1.1.5/release/isle-live-map-vi-1.1.5.zip`
- SHA-256: `D94FE9862022AC527D4CE8B4F70326226CC32BA76905CFF983C7DE0F8A803875`
- Manifest: `output/localization-release-1.1.5/release/isle-live-map-vi-1.1.5-latest.json`
- 10/10 resource và từng hash trong manifest đều khớp.

Bản cũ 1.1.4 trong handoff được giữ nguyên để bảo toàn checksum/provenance.
Công cụ tạo lại bản mới là `tools/rebrand_localization.py`.

## Máy kiểm thử

Hai file local đã được cài sau khi game được xác nhận đã tắt:

`D:\Steams\steamapps\common\The Isle\TheIsle\Content\Localization\Game\vi\Game.locres`

`D:\Steams\steamapps\common\The Isle\TheIsle\Content\Localization\Game\vi-VN\Game.locres`

Backup trước khi cài nằm trong `output/localization-release-1.1.5/local-install-backup-*`.

## Phân phối

Seed backend đã có artifact 1.1.5 và cấu hình filename/manifest tương ứng. Chưa
push, deploy VPS hoặc phát hành công khai trong lượt này. Khi nghiệm thu xong,
triển khai artifact mới bằng filename bất biến; không ghi đè ZIP 1.1.4 và cần
kiểm tra volume `isle_live_map_localization` trước khi restart backend.
