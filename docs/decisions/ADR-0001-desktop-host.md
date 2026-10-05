# ADR-0001: Desktop host temeli

- **Durum:** Kabul Edildi
- **Tarih:** 2026-10-06
- **Karar Verenler:** NiceWP geliştiricileri

## Bağlam ve Problem
Runtime, Explorer yeniden başladığında geçersizleşebilen masaüstü handle'larını UI veya Win32 bağımlılığını Core'a taşımadan yönetmelidir.

## Alınan Karar
Core tek bir `IDesktopHost` sözleşmesi ve keşif sonucu tanımlar. Windows implementasyonu tüm P/Invoke bildirimlerini tek dosyada tutar. Runtime servisi ödünç alınan host referansını ve çağıranın sağladığı tek pencerenin bağlantısını yönetir; pencere yaratmaz. AttachAsync pencerenin sahibi UI thread'inden çağrılır; keşif thread pool üzerinde, bağlama ise yakalanan UI bağlamında yapılır. Stop/Dispose bağlı pencerenin sahibi thread'inde çağrılmalıdır.

Keşif iki Explorer düzenini destekler: Progman altındaki SHELLDLL_DefView'in arkasında bulunan kardeş WorkerW veya ikonları barındıran üst pencerenin arkasındaki üst düzey WorkerW. Her adayın parent'ı, Explorer süreci ve ikon görünümünü barındırmaması doğrulanır. Progman'ın kendisi host olarak kullanılmaz. Desteklenmeyen hiyerarşide kontrollü sonuç döner. Belgelenmemiş 0x052C mesajı gerektiğinde Explorer kimliği başına yalnızca bir kez, 1000 ms sınırıyla gönderilir; aynı süreçteki implementasyon örnekleri bu sınırı paylaşır. Explorer bulunamaması bu kimliği sıfırlar. Timeout sonrası aynı Explorer için mesaj tekrar edilmez; sonraki keşifler mevcut yapıyı yeniden kontrol eder.

Gerçek WinUI doğrulaması nedeniyle bağlama sözleşmesi, çağıran thread'e ait normal ve sahipsiz üst düzey pencereyi kabul edecek şekilde netleştirilmiştir. İmza değişmemiştir. Platform pencerenin stilini ve konumunu saklar, WS_CHILD dönüşümünü yapar ve hata halinde geri alır. Detach önceki parent, stil ve konumu geri yükler; yinelenen cleanup güvenlidir. Geri alma hatası özgün hatayla birlikte iletilir; Runtime cleanup tekrarını mümkün kılmak için bağlantı kaydını tutar. Shell pencereleri sahiplenilmez, kapatılmaz veya yok edilmez. DPI farklarından doğan native hatalar gizlenmez.

Debug NiceWP.App açılışında sade bir WinUI test penceresi kullanılır. Pencere kendi HWND'sini Runtime'a verir; App'te P/Invoke bulunmaz. Başarılı test 90 saniye sonunda önce detach yapar, sonra pencereyi kapatır. Başarısızlıkta hata normal pencerede gösterilir. Geliştirme kayıtları `%TEMP%/NiceWP-desktop-test-<pid>.log` dosyasına yazılır. Release açılışı mevcut MainWindow'u kullanmaya devam eder.

## Sonuçlar ve Etkiler
- **Avantajlar:** Bağımlılık yönleri korunur; Runtime yaşam döngüsü gerçek masaüstüne dokunmadan test edilir.
- **Ödünleşimler:** WorkerW hiyerarşisi belgelenmiş bir Windows sözleşmesi değildir. Uyumsuz Explorer sürümleri kontrollü başarısızlıkla sonuçlanabilir. Handle kontrolü ile native işlem arasında Explorer kapanabilir; native hatalar çağırana iletilir.
- **Performans Etkisi:** Host servisinde polling, timer veya sürekli iş parçacığı yoktur. Yalnızca Debug test penceresinde tek seferlik kapanış timer'ı vardır. Keşif gerektiğinde thread pool üzerinde çalışır; mesaj beklemesi UI thread'inde yapılmaz. Attach/Detach pencerenin sahibi olan thread üzerinde açıkça çağrılır.

Native davranış referansları: [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent), [SendMessageTimeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw).
