# NiceWP — AI Geliştirme Kuralları (Binding Rules)

Bu belgedeki kurallar NiceWP projesinde çalışan tüm AI ajanları için kesin ve bağlayıcıdır (binding). Görev icra edilirken bu kuralların dışına çıkılamaz.

## 1. Kapsam ve Kod Bütünlüğü
1. **Görev Kapsamı:** Görev kapsamı dışında değişiklik yapma. İstenmeyen dosyalara, konfigürasyonlara veya özelliklere müdahale etme.
2. **Mevcut Yapıyı İnceleme:** Mevcut yapıyı incelemeden yeni abstraction veya component oluşturma. Var olan mimariye saygı göster.
3. **Tekrardan Kaçınma:** Duplicate kod üretme; ortak işlevleri uygun servis veya yardımcı sınıflarda topla.
4. **Çalışan Kodu Koruma:** Mevcut çalışan kodu gerekçesiz yeniden yazma.
5. **Refactor Gerekçelendirme:** Büyük refactor yapmadan önce gerekçesini açıkça belirt.

## 2. Bağımlılık ve Paket Disiplini
6. **Gereksiz Bağımlılık Yasağı:** Gereksiz dependency ekleme.
7. **Kararlı Sürüm Kuralı:** Kesinlikle Preview, Experimental veya prerelease paket kullanma. Windows App SDK ve NuGet bağımlılıklarında yalnızca Stable kanalı tercih et.

## 3. Kod Kalitesi ve Hata Yönetimi
8. **UI ve Mantık Ayrımı:** Business logic'i UI içine (veya code-behind dosyalarına) yerleştirme. Mantık katmanını UI'dan kesin çizgilerle ayır.
9. **Sabitler ve Temiz Kod:** Magic number ve magic string kullanmaktan kaçın. Anlamlı sabitler, enum'lar veya konfigürasyon anahtarları tanımla.
10. **Hata Yönetimi:** Hataları sessizce yutma. Beklenen istisnaları uygun şekilde logla veya üst katmana fırlat.
11. **Boilerplate ve Yorum Kirliliği:** Gereksiz yorum ve boilerplate üretme. Kodun kendisinin anlattığı bariz durumları yorumlama.

## 4. Performans ve Mimari Kararlar
12. **Performans Önceliği:** Performansı etkileyen kararları özellikle işaretle ve düşük RAM/CPU hedefini koru.
13. **Mimari Karar Kayıtları (ADR):** Yeni bir mimari karar gerekiyorsa, bunu doğrudan uygulamak yerine `docs/decisions/` altında bir ADR (Architecture Decision Record) oluştur.

## 5. Doğrulama ve Teslim
14. **Derleme Doğrulaması:** Her görev sonunda build kontrolü yap (`dotnet build`). Kırık veya derlenmeyen kod teslim etme.
