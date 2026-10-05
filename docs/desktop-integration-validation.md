# 0.0.2 — Gerçek Explorer doğrulaması

Tarih: 2026-10-06 (Europe/Istanbul). Mevcut Windows oturumu, x64 Debug WinUI uygulaması.

## Sonuç

- Host: Progman `0x10158` altında ikon görünümü `0x1015C`; onun arkasındaki WorkerW `0x1086C` çözüldü.
- Attach: son testte HWND `0x408A4`, WorkerW `0x1086C` altına bağlandı; native parent eşitliği doğrulandı.
- Görsel katman ve ikon etkileşimi: kullanıcı test yazısının ikonların arkasında göründüğünü ve ikonun seçilebildiğini doğruladı. Otomasyon aracı masaüstü child penceresini hedefleyemediği için bu iki sonuç kullanıcı gözlemine dayanır.
- Cleanup: 90 saniye sonunda parent `0x0`, `styleRestored=True`; Runtime referansı temizlendi ve test süreci normal kapandı.
- Explorer: test öncesi/sonrası PID `1908`, başlangıç zamanı değişmedi, `Responding=True`. Explorer sonlandırılmadı veya yeniden başlatılmadı; sistem ayarları değiştirilmedi.
- Kanıt: son testin `%TEMP%/NiceWP-desktop-test-23968.log` kaydı. Handle/PID değerleri yalnızca bu oturuma aittir; kodda sabitlenmemiştir.

## Gerçek testte düzeltilen noktalar

1. İlk keşif Progman altındaki WorkerW düzenini desteklemiyordu. `WorkerUnavailable` hatası normal pencerede gösterildi; süreç çökmedi. Child WorkerW keşfi eklendi.
2. İlk kapanış denemesinde WS_CHILD henüz geri alınmamışken `SetParent(NULL)` sonrası parent kontrolü yanlış hata üretti. Kontrol stil geri yüklemesinden sonraya taşındı. Hatalı test süreci kapatıldı; son sürümde normal cleanup ve süreç çıkışı tekrar doğrulandı.

## Tekrarlama

`dotnet build NiceWallpaper.sln -p:Platform=x64` ardından Debug NiceWP.App çalıştırılır. App normal WinUI penceresinin HWND'sini Runtime'a verir. Başarılı test 90 saniye sonunda kendini temizleyerek kapanır; başarısızlık normal pencerede görünür. Release açılışı değişmez.

Bu doğrulama mevcut oturumu kapsar. Explorer restart, farklı DPI/monitör düzenleri ve diğer Windows sürümleri bu gerçek ortam testinde denenmemiştir.
