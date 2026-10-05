# Mimari Karar Kayıtları (Architecture Decision Records - ADR)

Bu dizin, NiceWP projesinde alınan önemli teknik ve mimari kararların tarihsel kaydını tutar.

## ADR Dosya Formatı

Yeni kararlar `ADR-XXXX-<kisa-baslik>.md` şeklinde adlandırılır (örn: `ADR-0001-ui-runtime-process-ayrimi.md`).

### Şablon

```markdown
# ADR-XXXX: [Karar Başlığı]

- **Durum:** [Önerildi | Kabul Edildi | Reddedildi | Yürürlükten Kaldırıldı]
- **Tarih:** YYYY-MM-DD
- **Karar Verenler:** [İsim / Ekip]

## Bağlam ve Problem
Kararın alınmasını gerektiren teknik gereksinim, kısıtlar ve problem tanımı.

## Alınan Karar
Seçilen çözüm, teknik yaklaşım ve gerekçeleri.

## Sonuçlar ve Etkiler
- **Avantajlar:** ...
- **Dezavantajlar / Ödünleşimler (Trade-offs):** ...
- **Performans Etkisi:** (RAM, CPU ve gecikme değerlendirmesi)
```
