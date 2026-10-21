namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Registers the full Turkish (tr-TR) localization dictionary.
/// Provides natural, idiomatic, and non-literal translations for all UI surfaces and status messages.
/// </summary>
internal static class TurkishResources
{
    public static void Register(LocalizationService service)
    {
        // Navigation
        service.RegisterTurkish(LocalizationKeys.NavLibrary, "Kütüphane");
        service.RegisterTurkish(LocalizationKeys.NavActiveWallpaper, "Aktif Duvar Kağıdı");
        service.RegisterTurkish(LocalizationKeys.NavPerformance, "Performans");
        service.RegisterTurkish(LocalizationKeys.NavSettings, "Ayarlar");

        // Library Controls
        service.RegisterTurkish(LocalizationKeys.LibAddWallpaper, "Duvar Kağıdı Ekle");
        service.RegisterTurkish(LocalizationKeys.LibAddFromUrl, "URL'den Ekle");
        service.RegisterTurkish(LocalizationKeys.LibAddFolder, "Klasör Ekle");
        service.RegisterTurkish(LocalizationKeys.LibSelect, "Seç");
        service.RegisterTurkish(LocalizationKeys.LibSelectAll, "Tümünü Seç");
        service.RegisterTurkish(LocalizationKeys.LibClear, "Temizle");
        service.RegisterTurkish(LocalizationKeys.LibFavorite, "Favori");
        service.RegisterTurkish(LocalizationKeys.LibUnfavorite, "Favoriden Çıkar");
        service.RegisterTurkish(LocalizationKeys.LibRemove, "Kaldır");
        service.RegisterTurkish(LocalizationKeys.LibDone, "Tamam");
        service.RegisterTurkish(LocalizationKeys.LibSearchPlaceholder, "Duvar kağıtlarında ara...");
        service.RegisterTurkish(LocalizationKeys.LibFilter, "Filtrele");
        service.RegisterTurkish(LocalizationKeys.LibSort, "Sırala");

        // Library Filters & Sort
        service.RegisterTurkish(LocalizationKeys.FilterAll, "Tümü");
        service.RegisterTurkish(LocalizationKeys.FilterImages, "Resimler");
        service.RegisterTurkish(LocalizationKeys.FilterVideos, "Videolar");
        service.RegisterTurkish(LocalizationKeys.FilterFavorites, "Favoriler");
        service.RegisterTurkish(LocalizationKeys.FilterRecent, "Son Eklenenler");
        service.RegisterTurkish(LocalizationKeys.FilterMissing, "Eksik Dosyalar");
        service.RegisterTurkish(LocalizationKeys.FilterRemote, "Uzak Kaynaklar");

        service.RegisterTurkish(LocalizationKeys.SortRecent, "Son Eklenenler");
        service.RegisterTurkish(LocalizationKeys.SortName, "İsim (A–Z)");
        service.RegisterTurkish(LocalizationKeys.SortType, "Tür");
        service.RegisterTurkish(LocalizationKeys.SortFavorites, "Önce Favoriler");

        // Library Context / Item Actions
        service.RegisterTurkish(LocalizationKeys.ItemActive, "AKTİF");
        service.RegisterTurkish(LocalizationKeys.ItemApply, "Uygula");
        service.RegisterTurkish(LocalizationKeys.ItemReapply, "Yeniden Uygula");
        service.RegisterTurkish(LocalizationKeys.ItemRename, "Yeniden Adlandır");
        service.RegisterTurkish(LocalizationKeys.ItemOpenFileLocation, "Dosya konumunu aç");
        service.RegisterTurkish(LocalizationKeys.ItemLocateFile, "Dosyayı bul...");
        service.RegisterTurkish(LocalizationKeys.ItemSelectedCount, "{0} seçildi");
        service.RegisterTurkish(LocalizationKeys.ItemEmptyTitle, "Kütüphaneniz boş");
        service.RegisterTurkish(LocalizationKeys.ItemEmptySubtitle, "Yerel dosyalardan, klasörden veya URL bağlantısından ilk duvar kağıdınızı ekleyin.");

        // Active Wallpaper Controls & Status
        service.RegisterTurkish(LocalizationKeys.ActivePrevious, "Önceki");
        service.RegisterTurkish(LocalizationKeys.ActivePause, "Duraklat");
        service.RegisterTurkish(LocalizationKeys.ActiveResume, "Devam Et");
        service.RegisterTurkish(LocalizationKeys.ActiveNext, "Sonraki");
        service.RegisterTurkish(LocalizationKeys.ActiveStop, "Durdur");
        service.RegisterTurkish(LocalizationKeys.ActiveRotation, "Döngü");
        service.RegisterTurkish(LocalizationKeys.ActivePosition, "Konum");
        service.RegisterTurkish(LocalizationKeys.ActiveNextChange, "Sonraki değişim");
        service.RegisterTurkish(LocalizationKeys.ActiveOverdue, "Gecikmiş");
        service.RegisterTurkish(LocalizationKeys.ActiveType, "Tür");
        service.RegisterTurkish(LocalizationKeys.ActiveState, "Durum");
        service.RegisterTurkish(LocalizationKeys.ActiveEfficiency, "Verimlilik");
        service.RegisterTurkish(LocalizationKeys.ActiveImage, "Resim");
        service.RegisterTurkish(LocalizationKeys.ActiveVideo, "Video");
        service.RegisterTurkish(LocalizationKeys.ActiveRunning, "Çalışıyor");
        service.RegisterTurkish(LocalizationKeys.ActivePaused, "Duraklatıldı");
        service.RegisterTurkish(LocalizationKeys.ActiveNoWallpaper, "Aktif duvar kağıdı yok.");
        service.RegisterTurkish(LocalizationKeys.ActiveSchedule, "Program");
        service.RegisterTurkish(LocalizationKeys.ActiveScheduleNext, "Sonraki");
        service.RegisterTurkish(LocalizationKeys.ActiveScheduleDay, "Gündüz");
        service.RegisterTurkish(LocalizationKeys.ActiveScheduleNight, "Gece");

        // Countdown Formats
        service.RegisterTurkish(LocalizationKeys.FormatCountdownMin, "Sonraki değişime {0} dk");
        service.RegisterTurkish(LocalizationKeys.FormatCountdownSec, "Sonraki değişime {0} sn");
        service.RegisterTurkish(LocalizationKeys.FormatScheduleAt, "{0}, {1}");

        // Settings Interface
        service.RegisterTurkish(LocalizationKeys.SetAppearance, "Görünüm");
        service.RegisterTurkish(LocalizationKeys.SetTheme, "Tema");
        service.RegisterTurkish(LocalizationKeys.SetThemeSystem, "Sistem");
        service.RegisterTurkish(LocalizationKeys.SetThemeLight, "Açık");
        service.RegisterTurkish(LocalizationKeys.SetThemeDark, "Koyu");
        service.RegisterTurkish(LocalizationKeys.SetLanguage, "Dil");
        service.RegisterTurkish(LocalizationKeys.SetLanguageDescription, "Kullanmak istediğiniz arayüz dilini seçin.");
        service.RegisterTurkish(LocalizationKeys.SetLanguageRestartNotice, "Dil değişikliğini uygulamak için NiceWP'yi yeniden başlatın.");

        service.RegisterTurkish(LocalizationKeys.SetEfficiency, "Verimlilik Profili");
        service.RegisterTurkish(LocalizationKeys.SetEfficiencyBalanced, "Dengeli");
        service.RegisterTurkish(LocalizationKeys.SetEfficiencyEco, "Eko");
        service.RegisterTurkish(LocalizationKeys.SetEfficiencyUnrestricted, "Kısıtlamasız");

        service.RegisterTurkish(LocalizationKeys.SetStartup, "Başlangıç");
        service.RegisterTurkish(LocalizationKeys.SetStartupLaunch, "Windows başlangıcında NiceWP'yi başlat");
        service.RegisterTurkish(LocalizationKeys.SetWallpaperBehavior, "Duvar Kağıdı Davranışı");
        service.RegisterTurkish(LocalizationKeys.SetRestoreLast, "Başlangıçta son duvar kağıdını geri yükle");

        service.RegisterTurkish(LocalizationKeys.SetWallpaperRotation, "Duvar Kağıdı Döngüsü");
        service.RegisterTurkish(LocalizationKeys.SetRotationMode, "Döngü Modu");
        service.RegisterTurkish(LocalizationKeys.SetRotationOrder, "Sıralama");
        service.RegisterTurkish(LocalizationKeys.SetRotationSequential, "Sıralı");
        service.RegisterTurkish(LocalizationKeys.SetRotationShuffle, "Karışık");
        service.RegisterTurkish(LocalizationKeys.SetRotationInterval, "Değişim aralığı");
        service.RegisterTurkish(LocalizationKeys.SetChangeEvery, "Değiştirilme sıklığı");

        service.RegisterTurkish(LocalizationKeys.SetWallpaperSchedule, "Duvar Kağıdı Programı");
        service.RegisterTurkish(LocalizationKeys.SetScheduleMode, "Program Modu");
        service.RegisterTurkish(LocalizationKeys.SetScheduleOff, "Kapalı");
        service.RegisterTurkish(LocalizationKeys.SetScheduleDayNight, "Gündüz ve Gece");
        service.RegisterTurkish(LocalizationKeys.SetDayWallpaper, "Gündüz duvar kağıdı");
        service.RegisterTurkish(LocalizationKeys.SetNightWallpaper, "Gece duvar kağıdı");
        service.RegisterTurkish(LocalizationKeys.SetDayStarts, "Gündüz başlangıcı");
        service.RegisterTurkish(LocalizationKeys.SetNightStarts, "Gece başlangıcı");
        service.RegisterTurkish(LocalizationKeys.SetChooseWallpaper, "Duvar kağıdı seç...");
        service.RegisterTurkish(LocalizationKeys.SetNoWallpaperSelected, "(Seçilmedi)");

        service.RegisterTurkish(LocalizationKeys.SetStorageTitle, "Yerel Depolama ve Veri");
        service.RegisterTurkish(LocalizationKeys.SetOpenStorageFolder, "Veri Klasörünü Aç");

        // Tray Menu
        service.RegisterTurkish(LocalizationKeys.TrayOpen, "NiceWP'yi Aç");
        service.RegisterTurkish(LocalizationKeys.TrayStopWallpaper, "Duvar Kağıdını Durdur");
        service.RegisterTurkish(LocalizationKeys.TrayExit, "NiceWP'den Çıkış");

        // Performance Interface
        service.RegisterTurkish(LocalizationKeys.PerfHealth, "Sağlık");
        service.RegisterTurkish(LocalizationKeys.PerfEfficiency, "Verimlilik");
        service.RegisterTurkish(LocalizationKeys.PerfCurrentAction, "Geçerli Eylem");
        service.RegisterTurkish(LocalizationKeys.PerfCpu, "CPU Kullanımı");
        service.RegisterTurkish(LocalizationKeys.PerfWorkingSet, "Çalışma Belleği (RAM)");
        service.RegisterTurkish(LocalizationKeys.PerfPrivateMemory, "Özel Bellek");
        service.RegisterTurkish(LocalizationKeys.PerfThreads, "İş Parçacıkları");
        service.RegisterTurkish(LocalizationKeys.PerfHandles, "Tutamaçlar");
        service.RegisterTurkish(LocalizationKeys.PerfUptime, "Çalışma Süresi");
        service.RegisterTurkish(LocalizationKeys.PerfRecentActivity, "Son Etkinlikler");
        service.RegisterTurkish(LocalizationKeys.PerfExport, "Raporu Dışa Aktar");
        service.RegisterTurkish(LocalizationKeys.PerfHostConnected, "Çalışma Motoru: Bağlı");
        service.RegisterTurkish(LocalizationKeys.PerfHostDisconnected, "Çalışma Motoru: Bağlantı Yok");
        service.RegisterTurkish(LocalizationKeys.PerfRuntimeHostPidUptimeTemplate, "PID: {0} • Çalışma Süresi: {1}");
        service.RegisterTurkish(LocalizationKeys.PerfLive, "CANLI");
        service.RegisterTurkish(LocalizationKeys.PerfAvgPeakTemplate, "Ort.: {0} • En Yüksek: {1}");
        service.RegisterTurkish(LocalizationKeys.PerfPeakTemplate, "En Yüksek: {0}");
        service.RegisterTurkish(LocalizationKeys.PerfSamplesTemplate, "{0} örnek (~{0} sn)");

        // Status Messages & Feedback
        service.RegisterTurkish(LocalizationKeys.StatusRuntimeRecovered, "Çalışma zamanı başarıyla kurtarıldı.");
        service.RegisterTurkish(LocalizationKeys.StatusRestoreFailed, "Önceki duvar kağıdı geri yüklenemedi.");
        service.RegisterTurkish(LocalizationKeys.StatusWallpaperFileMissing, "Duvar kağıdı dosyası diskte bulunamadı.");
        service.RegisterTurkish(LocalizationKeys.StatusSafeStopped, "Art arda hatalar nedeniyle güvenli şekilde durduruldu.");
        service.RegisterTurkish(LocalizationKeys.StatusImportCancelled, "İçe aktarma işlemi iptal edildi.");
        service.RegisterTurkish(LocalizationKeys.StatusUnsupportedFile, "Desteklenmeyen dosya biçimi.");
        service.RegisterTurkish(LocalizationKeys.StatusAlreadyInLibrary, "Dosya kütüphanenizde zaten mevcut.");
        service.RegisterTurkish(LocalizationKeys.StatusRotationPaused, "Döngü duraklatıldı.");
        service.RegisterTurkish(LocalizationKeys.StatusRotationStopped, "Döngü durduruldu.");
        service.RegisterTurkish(LocalizationKeys.StatusWallpaperApplied, "Duvar kağıdı uygulandı: {0}");
        service.RegisterTurkish(LocalizationKeys.StatusWallpaperStopped, "Duvar kağıdı durduruldu.");
        service.RegisterTurkish(LocalizationKeys.StatusImportAdded, "{0} duvar kağıdı eklendi.");
        service.RegisterTurkish(LocalizationKeys.StatusImportDuplicates, "{0} öğe kütüphanede zaten mevcut.");
        service.RegisterTurkish(LocalizationKeys.StatusConfirmRemove, "Seçili duvar kağıtlarını kütüphanenizden kaldırmak istediğinize emin misiniz? (Fiziksel dosyalar diskten silinmez)");
        service.RegisterTurkish(LocalizationKeys.StatusConfirmRemoveTitle, "Kaldırmayı Onayla");
        service.RegisterTurkish(LocalizationKeys.StatusCancel, "İptal");
        service.RegisterTurkish(LocalizationKeys.StatusRemoveButton, "Kaldır");
    }
}
