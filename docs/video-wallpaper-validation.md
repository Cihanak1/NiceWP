# 0.0.4 — Minimum video wallpaper prototipi

Tarih: 2026-10-06 (Europe/Istanbul). Gerçek WorkerW video oynatımı henüz doğrulanmadı.

## Uygulama

- Rendering MediaPlayer/MediaSource sahibidir; MediaPlayerElement yalnızca sunum yapar.
- MP4 uzantısı ve Windows encoding profile üzerinden MPEG4/H264 kontrol edilir.
- MediaOpened beklenir; açılış hata/iptal veya 30 saniye timeout ile sonuçlanabilir.
- AutoPlay kapalıdır; mevcut desktop attach sonrasında Show/Play çağrılır.
- IsMuted ve IsLoopingEnabled açık, Stretch UniformToFill'dir.
- Stop bekleyen video renderer'ını dispose eder; callback abonelikleri, player,
  source ve pencere temizlenir. Cleanup hatası Runtime tarafından tekrar denenebilir.
- MediaFailed açılışta kontrollü sonuç, oynatma başladıktan sonra tanılama kaydı üretir;
  otomatik recovery bu prototipte yoktur.
- ImageWallpaperRenderer ve WorkerW/Progman implementasyonu değiştirilmedi.

## Otomatik doğrulama

- `dotnet restore NiceWallpaper.sln`: başarılı.
- `dotnet build NiceWallpaper.sln -p:Platform=x64`: 0 uyarı / 0 hata.
- `dotnet build NiceWallpaper.sln -c Release -p:Platform=x64`: 0 uyarı / 0 hata.
- `dotnet test NiceWallpaper.sln -c Release`: 58 başarılı, 0 başarısız (23 Core, 35 Runtime).
  - Core: ImageWallpaper ve VideoWallpaper için uzantı doğrulamaları (.png, .jpg, .jpeg, .mp4), büyük/küçük harf duyarsızlığı, geçersiz yollar ve eksik dosyalar.
  - Runtime: Image→Video→Image, Video→Video, başarısız video açılışında mevcut wallpaper'ın korunması, açılış sırasında Stop'un bekleyen renderer'ı iptal edip serbest bırakması ve lifecycle akışı.
  - VideoProfileTests: `sample-5s.mp4` ve `sample-10s.mp4` profilleri, null metadata, MP4 olmayan container, video track içermeyen dosya, H.264 harici video codec'i ve bozuk/boş dosyalar.
- Release test paketi `.artifacts/dist/NiceWP-0.0.4-win-x64/` klasörüne yayınlandı. XAML ve PRI kaynakları (`NiceWP.App.pri`, `.xbf`), runtime bağımlılıkları eksiksiz sağlandı.
- Sahte renderer testleri gerçek Windows decoder/pencere davranışını doğrulamaz.

## Codec ve Container Doğrulaması Düzeltmesi

- **Kök Neden:** Windows Media Foundation MP4 container için `MPEG4` (büyük harf) döndürürken, SDK sabiti `MediaEncodingSubtypes.Mpeg4` `"Mpeg4"` değerindedir. C# `!=` işleci büyük/küçük harf duyarlı karşılaştırma yaptığından tüm geçerli MP4/H.264 dosyaları `The file content must be MP4 with H.264 video.` hatasıyla reddediliyordu.
- **Düzeltme:**
  1. Container kontrolü `string.Equals(..., StringComparison.OrdinalIgnoreCase)` ile yapıldı (MPEG4 ve MP4 kabul edilir).
  2. Video track varlığı (`profile.Video == null`) ve video codec (`H264` / `AVC1`) kontrolleri ayrıldı; her duruma özel açıklayıcı hata mesajları tanımlandı.
  3. Metadata okuma hataları (bozuk dosya) `MediaEncodingProfile.CreateFromFileAsync` aşamasında yakalanarak `RendererFailed` ile raporlandı.

## Gerçek masaüstü doğrulaması

Kullanıcı ortamında doğrulanmış durumlar:
- Statik görsel (Image) wallpaper çalışıyor.
- MP4/H.264 video wallpaper çalışıyor.
- WorkerW altında video masaüstüne yerleşiyor ve arka planda oynatılıyor.
- Masaüstü ikonları görünür ve kullanılabilir/etkileşime açık kalıyor.

Kullanıcı tarafından henüz ayrıca doğrulanmamış durumlar:
- Kesintisiz video döngüsü (loop).
- Image → Video → Image ve Video → Video geçişleri.
- Donanım decode performansı ve ses izolasyonunun uzun süreli davranışı.

## Tekrarlama

Örnekler https://samplelib.com/sample-mp4.html sayfasından indirildi:

- https://samplelib.com/mp4/sample-5s.mp4 (2.848.208 bayt)
- https://samplelib.com/mp4/sample-10s.mp4 (5.485.935 bayt)

Yerel örnekler repo içindeki git tarafından yok sayılan
`.system_generated/video-validation/` klasöründedir. Bunlar uygulamaya gömülmez.
Kaynakların H.264 uygunluğu henüz gerçek renderer tarafından doğrulanmadı.

1. `.artifacts/dist/NiceWP-0.0.4-win-x64/NiceWP.App.exe` test çıktısını çalıştırın.
2. Select Image / MP4 ile sample-5s.mp4 seçin; MediaOpened/attach/Play kayıtlarını kontrol edin.
3. İkonların arkasında hareket, ikon seçimi ve sessizlik kontrolü yapın; birkaç döngü izleyin.
4. Farklı en/boy oranındaki H.264 MP4 ile merkezli UniformToFill kırpmasını kontrol edin.
   16:9 örnek tek başına farklı oranlı kırpmayı kanıtlamaz.
5. sample-10s.mp4 seçin, ardından image/video geçişi ve bozuk MP4 seçimi deneyin.
6. Kontrol penceresini kapatın; detach/player/source/window cleanup ve süreç çıkışını kontrol edin.

Mevcut tanılama dosyası adı korunur: `%TEMP%/NiceWP-image-test-<pid>.log`.
