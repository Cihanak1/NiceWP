using System.Globalization;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Localization;

namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Centralized formatter for runtime performance snapshots and statistics.
/// Converts point-in-time metrics, states, and composite health strings into natural localized UI text.
/// </summary>
public static class PerformanceSnapshotFormatter
{
    public static string FormatHealth(string? rawHealth, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (string.IsNullOrWhiteSpace(rawHealth))
        {
            return isTr ? "Bilinmiyor" : "Unknown";
        }

        return rawHealth switch
        {
            "Healthy" => isTr ? "Sağlıklı" : "Healthy",
            "Degraded" => isTr ? "Kısıtlı" : "Degraded",
            "SafeStopped" or "Safe Stopped" => isTr ? "Güvenli Durduruldu" : "Safe Stopped",
            "Awaiting status" => isTr ? "Durum bekleniyor" : "Awaiting status",
            "Unavailable" => isTr ? "Kullanılamıyor" : "Unavailable",
            _ => rawHealth
        };
    }

    public static string FormatEfficiencyProfile(string? rawProfile, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (string.IsNullOrWhiteSpace(rawProfile))
        {
            return isTr ? "Dengeli" : "Balanced";
        }

        return rawProfile switch
        {
            "Balanced" => isTr ? "Dengeli" : "Balanced",
            "Eco" => isTr ? "Tasarruf" : "Eco",
            "Unrestricted" => isTr ? "Sınırsız" : "Unrestricted",
            "Awaiting status" => isTr ? "Durum bekleniyor" : "Awaiting status",
            "Unavailable" => isTr ? "Kullanılamıyor" : "Unavailable",
            _ => rawProfile
        };
    }

    public static string FormatCurrentAction(string? rawAction, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            return isTr ? "Normal çalışıyor" : "Operating normally";
        }

        if (string.Equals(rawAction, "Idle: No active wallpaper", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Boşta: Etkin duvar kağıdı yok" : "Idle: No active wallpaper";

        if (string.Equals(rawAction, "Operating normally", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawAction, "Operating normally.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawAction, "Running normally", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Normal çalışıyor" : "Operating normally";

        if (string.Equals(rawAction, "Running normally: Video wallpaper playing", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Normal çalışıyor: Video duvar kağıdı oynatılıyor" : "Running normally: Video wallpaper playing";

        if (string.Equals(rawAction, "Running normally: Image wallpaper active", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Normal çalışıyor: Resim duvar kağıdı aktif" : "Running normally: Image wallpaper active";

        if (string.Equals(rawAction, "Safe Stopped: Circuit breaker tripped", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Güvenli Durduruldu: Devre kesici tetiklendi" : "Safe Stopped: Circuit breaker tripped";

        if (string.Equals(rawAction, "Runtime host disconnected", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Çalışma motoru bağlantısı yok" : "Runtime host disconnected";

        if (string.Equals(rawAction, "Awaiting status", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Durum bekleniyor" : "Awaiting status";

        if (rawAction.StartsWith("Paused: ", StringComparison.OrdinalIgnoreCase))
        {
            var reasonsPart = rawAction["Paused: ".Length..];
            if (!isTr) return rawAction;
            var parts = reasonsPart.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var trParts = parts.Select(r => r switch
            {
                "Manual" => "Manuel",
                "Fullscreen" => "Tam Ekran",
                "Battery Saver" => "Pil Tasarrufu",
                "Session Locked" => "Oturum Kilitli",
                "Display Off" => "Ekran Kapalı",
                _ => r
            });
            return $"Duraklatıldı: {string.Join(", ", trParts)}";
        }

        if (string.Equals(rawAction, "Paused", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Duraklatıldı" : "Paused";

        return rawAction;
    }

    public static string FormatHostStatusTitle(bool isConnected, bool isUnavailable = false, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (isUnavailable)
            return isTr ? "Çalışma Motoru: Kullanılamıyor" : "Runtime Host: Unavailable";
        return isConnected
            ? (isTr ? "Çalışma Motoru: Bağlı" : "Runtime Host: Connected")
            : (isTr ? "Çalışma Motoru: Bağlantı Yok" : "Runtime Host: Disconnected");
    }

    public static string FormatHostDetails(int pid, TimeSpan uptime, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var uptimeStr = FormatTimeSpan(uptime);
        return loc.Format(LocalizationKeys.PerfRuntimeHostPidUptimeTemplate, pid, uptimeStr);
    }

    public static string FormatLiveIndicator(ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        return loc.GetString(LocalizationKeys.PerfLive);
    }

    public static string FormatAvgPeak(string avg, string peak, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        return loc.Format(LocalizationKeys.PerfAvgPeakTemplate, avg, peak);
    }

    public static string FormatPeak(object peak, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        return loc.Format(LocalizationKeys.PerfPeakTemplate, peak);
    }

    public static string FormatSampleCount(int count, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        return loc.Format(LocalizationKeys.PerfSamplesTemplate, count);
    }

    public static string FormatWallpaperTypeState(string type, string state, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        var trType = type switch
        {
            IpcConstants.WallpaperTypeImage => isTr ? "Resim" : "Image",
            IpcConstants.WallpaperTypeVideo => isTr ? "Video" : "Video",
            _ => isTr ? "Yok" : "None"
        };
        var trState = state switch
        {
            IpcConstants.PlaybackStateRunning => isTr ? "Çalışıyor" : "Running",
            IpcConstants.PlaybackStatePaused => isTr ? "Duraklatıldı" : "Paused",
            "Stopped" => isTr ? "Durduruldu" : "Stopped",
            _ => isTr ? "Etkin Değil" : "Inactive"
        };
        return $"{trType} • {trState}";
    }

    public static string FormatRendererStatus(bool isAlive, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isAlive
            ? (isTr ? "Canlı (Geçerli HWND)" : "Alive (HWND valid)")
            : (isTr ? "Etkin Değil" : "Inactive");
    }

    public static string FormatDesktopHostState(bool isConnected, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isConnected
            ? (isTr ? "Bağlı (Geçerli WorkerW)" : "Connected (WorkerW valid)")
            : (isTr ? "Bağlantı Yok" : "Disconnected");
    }

    public static string FormatPauseReasons(string? reasons, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (string.IsNullOrWhiteSpace(reasons) || string.Equals(reasons, "None", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Yok" : "None";
        if (string.Equals(reasons, "Unknown", StringComparison.OrdinalIgnoreCase))
            return isTr ? "Bilinmiyor" : "Unknown";

        if (!isTr) return reasons;
        var parts = reasons.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var trParts = parts.Select(r => r switch
        {
            "Manual" => "Manuel",
            "Fullscreen" => "Tam Ekran",
            "Battery Saver" or "Battery" => "Pil Tasarrufu",
            "Session Locked" or "SessionLock" => "Oturum Kilitli",
            "Display Off" or "Display" => "Ekran Kapalı",
            _ => r
        });
        return string.Join(", ", trParts);
    }

    public static string FormatFullscreenPolicy(bool isActive, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isActive
            ? (isTr ? "Etkin (Tam ekran penceresi nedeniyle duraklatıldı)" : "Active (Auto-paused by fullscreen window)")
            : (isTr ? "Etkin Değil (Masaüstü görünür)" : "Inactive (Desktop visible)");
    }

    public static string FormatPowerPolicy(bool isActive, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isActive
            ? (isTr ? "Etkin (Güç ilkesi nedeniyle duraklatıldı)" : "Active (Auto-paused by power policy)")
            : (isTr ? "Etkin Değil (Prizde veya ilke uygun)" : "Inactive (AC or policy satisfied)");
    }

    public static string FormatSessionLockPolicy(bool isActive, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isActive
            ? (isTr ? "Etkin (Oturum kilidi nedeniyle duraklatıldı)" : "Active (Auto-paused by session lock)")
            : (isTr ? "Etkin Değil (Oturum açık)" : "Inactive (Session unlocked)");
    }

    public static string FormatDisplayPowerPolicy(bool isActive, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        return isActive
            ? (isTr ? "Etkin (Ekran kapalı olduğu için duraklatıldı)" : "Active (Auto-paused by display off)")
            : (isTr ? "Etkin Değil (Ekran açık)" : "Inactive (Display active)");
    }

    public static string FormatEfficiencyStatus(RuntimePerformanceSnapshot snap, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (snap.HasActiveWallpaper)
        {
            if (snap.IsPaused)
            {
                if (snap.FullscreenPauseActive || snap.PowerPauseActive || snap.SessionLockedPauseActive || snap.DisplayOffPauseActive)
                {
                    return isTr ? "Verimlilik: Kaynak tasarrufu için duraklatıldı" : "Efficiency: Paused to save resources";
                }
                return isTr ? "Verimlilik: Duraklatıldı (Manuel)" : "Efficiency: Paused (Manual)";
            }
            return isTr ? "Verimlilik: Aktif" : "Efficiency: Active";
        }
        return isTr ? "Verimlilik: Boşta (Duvar kağıdı yok)" : "Efficiency: Idle (No wallpaper)";
    }

    public static string FormatRecentEventsCount(int count, int total, ILocalizationService? loc = null)
    {
        loc ??= LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (total == 0) return isTr ? "0 olay" : "0 events";
        return isTr ? $"Son {count} / {total} olay" : $"Latest {count} of {total}";
    }

    public static string FormatTimeSpan(TimeSpan? ts) =>
        ts.HasValue ? $"{(int)ts.Value.TotalHours:D2}:{ts.Value.Minutes:D2}:{ts.Value.Seconds:D2}" : "--:--:--";
}
