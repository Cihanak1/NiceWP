# ADR-0002: Statik görsel wallpaper

- **Durum:** Kabul Edildi
- **Tarih:** 2026-10-06

Core dosya doğrulaması, kontrollü sonuç ve tek renderer sözleşmesini tanımlar. Rendering mevcut stable Windows App SDK sürümünü kullanarak sahipsiz WinUI pencere ve SoftwareBitmapSource oluşturur. Pencerenin client alanına yayılan kök Grid'in arka planı, iki eksende merkezli UniformToFill ImageBrush ile çizilir. Arka plan görseli layout ölçümüne doğal boyutunu katmaz; viewport pencere boyutuyla belirlenir ve yeniden boyutlandırmada WinUI tarafından güncellenir. Windows decoder yalnızca PNG/JPEG içeriklerini kabul eder; uzantı değiştirmek diğer formatları desteklemez. UniformToFill oranı koruyarak ortadan kırpar. Tek birincil ekran hedeflenir.

Runtime UI thread üzerinde çağrılır; tek renderer üretici delegesi testlerde sahte renderer kullanımını sağlar. Yeni içerik hazırlandıktan sonra önceki bağlantı kaldırılır ve renderer kapatılır. Aynı normalize yol sağlıklı host ile yeniden seçilirse renderer tekrar oluşturulmaz. Başlatma sürerken Stop nesil kontrolüyle geç gelen sonuçları reddeder. Cleanup hataları sonuç olarak iletilir ve yeniden denenebilir.

App kontrol penceresi masaüstüne bağlanan renderer penceresinden ayrıdır. Dosya seçimi ve hata sunumu App'tedir. 0.0.2 DesktopHostService ve WindowsDesktopHost bağlama davranışı korunur. Host seçimde ve kontrol penceresi etkinleştiğinde doğrulanır; Explorer kaybında kontrollü stop uygulanır. Otomatik Explorer recovery bu kapsamda değildir.

Performans: dosya stream'i ve geçici bitmap yüklemeden sonra kapatılır. Aktif kaynak değiştirme/stop sırasında bırakılır. Timer, polling, sürekli repaint veya cache yoktur. WinUI layout yeniden boyutlandırmayı işler. Test ekranının 90 saniyelik timer'ı kaldırılır. Bu aşamada kontrol penceresinin kapanması runtime'ı durdurur; ayrı runtime süreci eklenmez.
