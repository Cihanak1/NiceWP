using System;
using System.Globalization;
using NiceWP.Core;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Observatory;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class PerformanceSnapshotFormatterTests
{
    private sealed class StubLocalizationService : ILocalizationService
    {
        public EffectiveLanguage CurrentLanguage { get; private set; }
        public CultureInfo Culture => CurrentLanguage == EffectiveLanguage.Turkish
            ? new CultureInfo("tr-TR")
            : CultureInfo.InvariantCulture;

        public event Action<EffectiveLanguage>? LanguageChanged;

        public StubLocalizationService(EffectiveLanguage lang)
        {
            CurrentLanguage = lang;
        }

        public void SetLanguage(EffectiveLanguage language)
        {
            CurrentLanguage = language;
            LanguageChanged?.Invoke(language);
        }

        public string GetString(string key)
        {
            if (CurrentLanguage == EffectiveLanguage.Turkish)
            {
                return key switch
                {
                    LocalizationKeys.PerfLive => "CANLI",
                    LocalizationKeys.PerfRuntimeHostPidUptimeTemplate => "PID: {0} • Çalışma Süresi: {1}",
                    LocalizationKeys.PerfAvgPeakTemplate => "Ort.: {0} • En Yüksek: {1}",
                    LocalizationKeys.PerfPeakTemplate => "En Yüksek: {0}",
                    LocalizationKeys.PerfSamplesTemplate => "{0} örnek",
                    _ => key
                };
            }

            return key switch
            {
                LocalizationKeys.PerfLive => "LIVE",
                LocalizationKeys.PerfRuntimeHostPidUptimeTemplate => "PID: {0} • Uptime: {1}",
                LocalizationKeys.PerfAvgPeakTemplate => "Avg: {0} • Peak: {1}",
                LocalizationKeys.PerfPeakTemplate => "Peak: {0}",
                LocalizationKeys.PerfSamplesTemplate => "{0} samples",
                _ => key
            };
        }

        public string Format(string key, params object[] args)
        {
            var pattern = GetString(key);
            return string.Format(Culture, pattern, args);
        }
    }

    [Theory]
    [InlineData("Healthy", EffectiveLanguage.Turkish, "Sağlıklı")]
    [InlineData("Healthy", EffectiveLanguage.English, "Healthy")]
    [InlineData("Degraded", EffectiveLanguage.Turkish, "Kısıtlı")]
    [InlineData("Degraded", EffectiveLanguage.English, "Degraded")]
    [InlineData("SafeStopped", EffectiveLanguage.Turkish, "Güvenli Durduruldu")]
    [InlineData("Safe Stopped", EffectiveLanguage.English, "Safe Stopped")]
    [InlineData("Unavailable", EffectiveLanguage.Turkish, "Kullanılamıyor")]
    [InlineData(null, EffectiveLanguage.Turkish, "Bilinmiyor")]
    [InlineData(null, EffectiveLanguage.English, "Unknown")]
    public void FormatHealth_ReturnsExpectedLocalization(string? raw, EffectiveLanguage lang, string expected)
    {
        var stub = new StubLocalizationService(lang);
        var result = PerformanceSnapshotFormatter.FormatHealth(raw, stub);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Balanced", EffectiveLanguage.Turkish, "Dengeli")]
    [InlineData("Balanced", EffectiveLanguage.English, "Balanced")]
    [InlineData("Eco", EffectiveLanguage.Turkish, "Tasarruf")]
    [InlineData("Eco", EffectiveLanguage.English, "Eco")]
    [InlineData("Unrestricted", EffectiveLanguage.Turkish, "Sınırsız")]
    [InlineData(null, EffectiveLanguage.Turkish, "Dengeli")]
    public void FormatEfficiencyProfile_ReturnsExpectedLocalization(string? raw, EffectiveLanguage lang, string expected)
    {
        var stub = new StubLocalizationService(lang);
        var result = PerformanceSnapshotFormatter.FormatEfficiencyProfile(raw, stub);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Idle: No active wallpaper", EffectiveLanguage.Turkish, "Boşta: Etkin duvar kağıdı yok")]
    [InlineData("Idle: No active wallpaper", EffectiveLanguage.English, "Idle: No active wallpaper")]
    [InlineData("Operating normally", EffectiveLanguage.Turkish, "Normal çalışıyor")]
    [InlineData("Operating normally", EffectiveLanguage.English, "Operating normally")]
    [InlineData("Running normally: Video wallpaper playing", EffectiveLanguage.Turkish, "Normal çalışıyor: Video duvar kağıdı oynatılıyor")]
    [InlineData("Safe Stopped: Circuit breaker tripped", EffectiveLanguage.Turkish, "Güvenli Durduruldu: Devre kesici tetiklendi")]
    [InlineData("Paused: Fullscreen", EffectiveLanguage.Turkish, "Duraklatıldı: Tam Ekran")]
    [InlineData("Paused: Fullscreen, Battery Saver", EffectiveLanguage.Turkish, "Duraklatıldı: Tam Ekran, Pil Tasarrufu")]
    [InlineData("Paused: Fullscreen", EffectiveLanguage.English, "Paused: Fullscreen")]
    public void FormatCurrentAction_ReturnsExpectedLocalization(string? raw, EffectiveLanguage lang, string expected)
    {
        var stub = new StubLocalizationService(lang);
        var result = PerformanceSnapshotFormatter.FormatCurrentAction(raw, stub);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, false, EffectiveLanguage.Turkish, "Çalışma Motoru: Bağlı")]
    [InlineData(true, false, EffectiveLanguage.English, "Runtime Host: Connected")]
    [InlineData(false, false, EffectiveLanguage.Turkish, "Çalışma Motoru: Bağlantı Yok")]
    [InlineData(false, false, EffectiveLanguage.English, "Runtime Host: Disconnected")]
    [InlineData(false, true, EffectiveLanguage.Turkish, "Çalışma Motoru: Kullanılamıyor")]
    [InlineData(false, true, EffectiveLanguage.English, "Runtime Host: Unavailable")]
    public void FormatHostStatusTitle_ReturnsExpectedLocalization(bool connected, bool unavail, EffectiveLanguage lang, string expected)
    {
        var stub = new StubLocalizationService(lang);
        var result = PerformanceSnapshotFormatter.FormatHostStatusTitle(connected, unavail, stub);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatLiveIndicator_ReturnsExpected()
    {
        var trStub = new StubLocalizationService(EffectiveLanguage.Turkish);
        var enStub = new StubLocalizationService(EffectiveLanguage.English);

        Assert.Equal("CANLI", PerformanceSnapshotFormatter.FormatLiveIndicator(trStub));
        Assert.Equal("LIVE", PerformanceSnapshotFormatter.FormatLiveIndicator(enStub));
    }

    [Fact]
    public void FormatAvgPeak_TurkishAndEnglish_FormatsCorrectly()
    {
        var trStub = new StubLocalizationService(EffectiveLanguage.Turkish);
        var enStub = new StubLocalizationService(EffectiveLanguage.English);

        var trResult = PerformanceSnapshotFormatter.FormatAvgPeak("0,6%", "1,4%", trStub);
        var enResult = PerformanceSnapshotFormatter.FormatAvgPeak("0.6%", "1.4%", enStub);

        Assert.Equal("Ort.: 0,6% • En Yüksek: 1,4%", trResult);
        Assert.Equal("Avg: 0.6% • Peak: 1.4%", enResult);
    }

    [Fact]
    public void FormatHostDetails_FormatsPidAndUptime()
    {
        var trStub = new StubLocalizationService(EffectiveLanguage.Turkish);
        var enStub = new StubLocalizationService(EffectiveLanguage.English);

        var trResult = PerformanceSnapshotFormatter.FormatHostDetails(1234, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(12), trStub);
        var enResult = PerformanceSnapshotFormatter.FormatHostDetails(1234, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(12), enStub);

        Assert.Equal("PID: 1234 • Çalışma Süresi: 00:05:12", trResult);
        Assert.Equal("PID: 1234 • Uptime: 00:05:12", enResult);
    }

    [Fact]
    public void FormatTimeSpan_UnderOneHour_ReturnsHhMmSs()
    {
        var span = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(45);
        Assert.Equal("00:02:45", PerformanceSnapshotFormatter.FormatTimeSpan(span));
    }

    [Fact]
    public void FormatTimeSpan_OverOneHour_ReturnsHhMmSs()
    {
        var span = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(5);
        Assert.Equal("03:14:05", PerformanceSnapshotFormatter.FormatTimeSpan(span));
    }
}
