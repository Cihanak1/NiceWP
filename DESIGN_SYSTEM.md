# NiceWP — Tasarım Sistemi İlkeleri

Bu doküman, NiceWP kullanıcı arayüzünün (UI) tasarım felsefesini ve temel prensiplerini belirler. Bu aşamada henüz renk paleti veya detaylı bileşen tanımları yapılmamıştır; tasarım bu ilkeler doğrultusunda ilerleyen aşamalarda somutlaştırılacaktır.

## 1. Temel Prensipler

- **Sade:** Arayüz gereksiz karmaşıklıktan, kalabalık menülerden ve dikkat dağıtıcı öğelerden arındırılmıştır. Kullanıcının aradığı işlevi en az adımda bulması hedeflenir.
- **Modern:** Windows 11 ve WinUI 3'ün yerel tasarım diliyle uyumlu, temiz ve güncel bir görsel dil benimsenir.
- **Hızlı:** Arayüz geçişleri, pencere yüklenmeleri ve etkileşimler anlık tepki vermelidir. Tasarım tercihleri render maliyetini ve gecikmeyi artırmamalıdır.
- **Wallpaper İçeriğinin Önüne Geçmeyen UI:** NiceWP'nin başrolü duvar kağıtlarıdır. Arayüz, sergilenen duvar kağıdı içeriğini baskılamamalı; içeriği öne çıkaran zarif, nötr ve işlevsel bir çerçeve işlevi görmelidir.

## 2. Sistem Kuralları

- **Tutarlı Spacing, Radius, Typography ve Animasyon Sistemi:**
  - Sayfa içi ve bileşenler arası boşluklar (spacing) standart bir ızgara (grid) sistemine sadık kalacaktır.
  - Köşe yuvarlamaları (corner radius) Windows App SDK standartları ile tutarlı olacaktır.
  - Tipografi hiyerarşisi okunabilirliği önceleyecek, sistem yazı tiplerine sadık kalacaktır.
  - Animasyonlar yalnızca anlamlı geri bildirim sağlayan hafif mikro animasyonlarla sınırlı kalacaktır.
- **Gereksiz Gradient ve Glassmorphism Kullanımından Kaçınma:**
  - Aşırı blur, ağır glassmorphism veya dikkati dağıtan gradient efektleri kullanılmayacaktır.
  - Performans ve net okunabilirlik, gösterişli görsel efektlerin her zaman önünde yer alır.
- **Erişilebilirlik ve Açık / Koyu Tema Desteği:**
  - Sistem temasına (Açık / Koyu) tam ve anında dinamik uyum sağlanacaktır.
  - Yüksek kontrast, klavye ile tam navigasyon, odak (focus) göstergeleri ve ekran okuyucu desteği tasarımın ayrılmaz parçasıdır.
