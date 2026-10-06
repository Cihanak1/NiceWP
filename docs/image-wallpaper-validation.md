# 0.0.3 — Image wallpaper doğrulaması

Tarih: 2026-10-06 (Europe/Istanbul). Windows x64, WinUI Debug.

## Otomatik kontroller

- `dotnet restore NiceWallpaper.sln`: başarılı.
- `dotnet build NiceWallpaper.sln -p:Platform=x64`: başarılı, 0 uyarı / 0 hata.
- `dotnet test NiceWallpaper.sln`: 37 başarılı, 0 başarısız.
- Core: PNG/JPG/JPEG uzantıları, desteklenmeyen uzantılar, geçersiz ve bulunamayan dosya yolları.
- Runtime: load/attach/show sırası, aynı dosya için renderer tekrar kullanım, değiştirmede detach/dispose sırası, bozuk içerik sonucunda mevcut wallpaper'ın korunması, yükleme sırasında stop, host kaybı, attach hatası ve tekrar denenebilir cleanup hataları.
- Platform.Windows host implementasyonu değiştirilmedi. Yeni üçüncü parti paket eklenmedi; Rendering mevcut Windows App SDK 2.5.1 paketini kullanır.

## Gerçek ortam

- Uygulama çalıştırıldı; Select Image ile Windows dosya seçici açıldı. PickerHost otomasyon tarafından hedeflenemediğinden seçim kullanıcı tarafından yapıldı.
- İlk denemede PNG ve JPG decode ve WorkerW attach başarılıydı, ancak attach sonrasında `Window.AppWindow` null döndü. Runtime kontrollü hata sundu; detach ve renderer disposal tamamlandı.
- Düzeltme: AppWindow referansı HWND hâlâ top-level iken saklanır ve gösterim bu referansla yapılır.
- Düzeltilmiş sürümde görsel/Fill/ikon etkileşimi, değiştirme, kapanış ve CPU kontrolü henüz tamamlanmadı.

## Tekrarlama

1. Debug NiceWP.App çalıştırılır; Select Image ile 4:3 oranlı bir PNG seçilir. Geniş ekranda üst/alt kırpılır, merkezdeki daire yuvarlak kalır.
2. Masaüstü ikonunun seçilebildiği ve wallpaper için ayrı taskbar girdisi olmadığı kontrol edilir.
3. Aynı dosya yeniden seçilir: logda ikinci load/attach olmamalıdır.
4. Farklı JPG seçilir: önceki pencere detach/dispose edilir ve yeni görsel gösterilir.
5. Bozuk PNG seçilir: kısa hata gösterilir, önceki görsel korunur.
6. Boşta işlem CPU süresi örneklenir; kontrol penceresi kapatılır. Logda cleanup ve süreç çıkışı kontrol edilir.

Debug kayıtları `%TEMP%/NiceWP-image-test-<pid>.log` konumundadır. Timer veya otomatik kapanış yoktur. Explorer kaybı kontrol penceresi etkinleştiğinde veya seçim sırasında doğrulanır; periyodik polling/recovery bulunmaz.

## Dikey merkezleme düzeltmesi

Kullanıcının sağladığı 1600×1200 PNG ve 1919×1079 masaüstü görüntüsü karşılaştırıldı. Kaynak merkez çizgileri (800,600), masaüstünde (960,720) konumundaydı. 1920×1080 monitörde 1,2 ölçek doğrudur; merkezli Fill için 1920×1440 çıktının üstünden ve altından 180'er piksel kırpılması, merkezin (960,540) olması gerekir. Ekran görüntüsü dikey merkezleme eksikliğini doğrular; gerçek HWND/client ve XAML layout boyutları ölçülmediğinden layout kaynaklı kök neden henüz çalışma zamanında kanıtlanmamıştır.

Renderer'ın doğrudan kök Image kullanımı, pencereye yayılan Grid arka planındaki merkezli UniformToFill ImageBrush ile değiştirildi. Görsel artık viewport'un doğal ölçüm boyutunu büyütmez. Brush kaynağı ve Grid arka planı Dispose sırasında temizlenir. WorkerW/Progman ve Runtime kodları değiştirilmedi.

Düzeltme sonrası x64 build 0 uyarı/0 hata ile tamamlandı; mevcut 37 test geçti. Bu testler Runtime lifecycle/değiştirme akışını doğrular, WinUI layout piksel testi değildir. Düzeltilmiş uygulama başlatıldı; yeni masaüstü Fill, resize, ikon etkileşimi, değiştirme ve cleanup doğrulaması henüz tamamlanmadı. Yerel HWND/XAML karşılaştırma programı, önceki geçici tanılama yasağı nedeniyle otomatik onay denetiminde reddedildi ve oluşturulmadı; kullanıcı izni bekleniyor.
