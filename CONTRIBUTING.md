# NiceWP — Katkı Rehberi (Contributing Guide)

Bu rehber, NiceWP projesine katkıda bulunurken uyulması gereken temel standartları açıklar.

## 1. Branch Yaklaşımı
- Geliştirmeler doğrudan `main` branch üzerinde yapılmaz.
- Her çalışma için odaklı bir dal açılır:
  - `feature/<özellik-adi>`: Yeni özellikler
  - `fix/<hata-adi>`: Hata düzeltmeleri
  - `refactor/<kapsam>`: Kod iyileştirmeleri
  - `docs/<kapsam>`: Dokümantasyon güncellemeleri

## 2. Küçük ve Odaklı Commit'ler
- Commit'ler atomik ve tek bir amaca yönelik olmalıdır.
- Açıklayıcı ve standart commit mesajları yazılmalıdır.
- İlgisiz değişiklikler aynı commit içerisinde birleştirilmemelidir.

## 3. Build Zorunluluğu
- Kod tabanına eklenen her değişiklik yerel ortamda başarıyla derlenmelidir (`dotnet build`).
- Derleme hatası (build failure) barındıran değişiklikler kabul edilmez.

## 4. Kod Formatlama
- Standart C# kodlama konvansiyonlarına ve projedeki `.editorconfig` kurallarına uyulmalıdır.
- Tutarlı girintileme, adlandırma ve temiz kod prensipleri benimsenmelidir.

## 5. Yeni Dependency Kuralları
- Harici paket eklemeden önce gerekliliği açıkça ortaya konmalıdır.
- Sadece resmi ve **Stable** kanaldan NuGet paketleri kullanılabilir.
- Preview, Experimental veya test paketleri eklenemez.

## 6. PR Öncesi Kontrol Listesi
Bir Pull Request açmadan önce şu adımlar tamamlanmalıdır:
- [ ] Kod yerel ortamda hatasız derleniyor mu?
- [ ] Değişiklikler yalnızca görev kapsamındaki dosyaları mı içeriyor?
- [ ] Gereksiz kod, log veya geçici dosyalar temizlendi mi?
- [ ] Yeni mimari bir karar varsa `docs/decisions/` altında ADR eklendi mi?
- [ ] `AI_RULES.md` kurallarına uyuldu mu?
