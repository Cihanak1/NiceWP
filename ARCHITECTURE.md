# NiceWP — Mimari Dokümantasyonu

Bu belge, NiceWP projesinin temel mimari prensiplerini ve teknik temellerini tanımlar. Henüz kesinleşmemiş mimari kararlar bu aşamada yer almamaktadır; her yeni temel mimari karar `docs/decisions/` altında ADR formatında kayıt altına alınacaktır.

## 1. Projenin Amacı
NiceWP, modern Windows sistemleri için geliştirilen, yüksek performanslı, kaynak tüketimi optimize edilmiş ve akıcı bir masaüstü canlı duvar kağıdı (wallpaper) uygulamasıdır.

## 2. Windows-First Yaklaşımı
NiceWP, Windows işletim sistemi (Windows 10 ve Windows 11 x64) platformunu birincil ve yerel hedef olarak alır. Windows işletim sistemi API'leri, pencere yönetimi ve masaüstü ekosistemiyle derin, verimli ve doğrudan entegrasyonu hedefler.

## 3. Teknoloji Yığını
- **Programlama Dili:** C#
- **Platform / Çalışma Zamanı:** .NET 10 (LTS)
- **Kullanıcı Arayüzü:** WinUI 3
- **SDK:** Windows App SDK (Yalnızca Stable kanal — Preview/Experimental paketler kullanılmaz)
- **Hedef Ortam:** Windows 10/11 x64

## 4. UI ve Wallpaper Runtime Ayrımı
Uygulama, uzun vadeli kararlılık ve performans için iki temel yürütme katmanının/sürecinin ayrılması prensibiyle tasarlanacaktır:
- **Kullanıcı Arayüzü (UI):** Duvar kağıdı seçimi, ayarlar, önizleme ve katalog yönetimini yürüten WinUI 3 tabanlı kabuk. Arayüz kapalıyken sistem kaynaklarını tüketmemek adına bellekten tahliye edilebilir yapıda olacaktır.
- **Wallpaper Runtime:** Masaüstü arka planında kesintisiz, düşük kaynak tüketimiyle render veya oynatma gerçekleştiren bağımsız çalışma zamanı motoru.

Bu ayrım, arka plan iş yükünün UI yaşam döngüsünden bağımsız kalmasını, arayüz çökmelerinden veya kapanmalarından etkilenmemesini ve kaynak sızıntılarının engellenmesini sağlar.

## 5. Modüler Yapı
Kod tabanı, sorumlulukların net olarak ayrıldığı modüller halinde kurgulanacaktır:
- Çekirdek iş kuralları ve domain modelleri
- Duvar kağıdı motoru / runtime servisleri
- Platform ve Windows API etkileşim katmanları
- WinUI 3 kullanıcı arayüzü bileşenleri

## 6. Düşük RAM ve CPU Hedefi
Masaüstü duvar kağıdı uygulamasının birincil önceliği, kullanıcının ana iş yüklerini, üretkenliğini ve oyun/uygulama performansını olumsuz etkilememesidir.
- Boşta (idle) durumunda sıfıra yakın CPU kullanımı
- Düşük ve kararlı RAM ayak izi
- Tam ekran uygulama veya oyun algılandığında arka plan kaynak tüketimini askıya alma / optimize etme hedefi

## 7. Bağımlılık Yönlerinin Korunması
- Bağımlılıklar daima dış katmanlardan iç çekirdek katmanlarına (Core/Domain) doğru akmalıdır.
- Çekirdek katmanlar UI veya somut platform bağımlılıklarına sahip olmamalıdır.
- Döngüsel bağımlılıklara (circular dependencies) asla izin verilmez.
