# ADR-0003: Minimum video wallpaper prototipi

- **Durum:** Prototip için kabul edildi; WorkerW video sunumu gerçek ortam doğrulamasına tabi.
- **Tarih:** 2026-10-06

Rendering, MediaPlayer'ın ve MediaSource'un sahibi olur. MediaPlayerElement yalnızca
görüntüyü sunar. Yerel MP4/H.264, sessiz döngü ve UniformToFill desteklenir.
Encoding profile kontrolü içeriği sınırlar; başarılı yükleme ayrıca MediaOpened bekler.
Oynatma yalnızca mevcut DesktopHostService attach işleminden sonra Show ile başlar.

ImageWallpaperService adı ve image üreticisi korunur; isteğe bağlı video üreticisi
aynı tek aktif wallpaper akışına katılır. Ayrı video servisi oluşturulmaz.
Stop bekleyen video renderer'ını da dispose eder; video renderer açılış işlemlerini
iptal eder ve callback aboneliklerini kaldırır. Image renderer'ın mevcut yükleme
davranışı değişmez. Yeni içerik hazırlanamazsa mevcut wallpaper korunur; eski
wallpaper kapatıldıktan sonraki attach/show hatasında geri dönüş garantisi yoktur.

Core ve host sözleşmeleri değişmez. WorkerW/Progman kodu değişmez. Kontrol
penceresi kapanınca Runtime durur; bağımsız süreç bu prototipin kapsamında değildir.

Gerçek testte mevcut Windows.Storage.Pickers çağrısı seçimden sonra 0x80004005
ile başarısız oldu. Aynı düğme/filtre/sonuç akışı, mevcut Windows App SDK içindeki
Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id) ile kullanılır.
Bu masaüstü picker'ı StorageFile yerine doğrudan dosya yolu döndürür; ek paket yoktur.

Performans: uygulama kare kopyalama, frame-server, polling veya render timer'ı
eklemez. Medya açılışına 30 saniye sınır konur. Donanım decode ve kesintisiz loop
garanti edilmez; benchmark/optimizasyon bu kapsamda değildir. Yeni paket eklenmez.

Kaynak: https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/play-audio-and-video-with-mediaplayer
