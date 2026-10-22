using System.Reflection;
using NiceWP.Core;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Observatory;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class LocalizationServiceTests
{
    private sealed class MockLanguageDetector : ILanguageDetector
    {
        private readonly string _tag;
        public MockLanguageDetector(string tag) => _tag = tag;
        public string GetSystemDisplayLanguage() => _tag;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFromSystemTag_NullOrWhitespace_ReturnsEnglish(string? tag)
    {
        var result = LanguageResolver.ResolveFromSystemTag(tag);
        Assert.Equal(EffectiveLanguage.English, result);
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("tr-TR")]
    [InlineData("tr-Cy")]
    [InlineData("TR-tr")]
    [InlineData("tr_TR")]
    public void ResolveFromSystemTag_TurkishTags_ReturnsTurkish(string tag)
    {
        var result = LanguageResolver.ResolveFromSystemTag(tag);
        Assert.Equal(EffectiveLanguage.Turkish, result);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("zh-CN")]
    public void ResolveFromSystemTag_NonTurkishTags_FallbackToEnglish(string tag)
    {
        var result = LanguageResolver.ResolveFromSystemTag(tag);
        Assert.Equal(EffectiveLanguage.English, result);
    }

    [Fact]
    public void Resolve_ExplicitTurkish_OverridesSystemDetector()
    {
        var detector = new MockLanguageDetector("en-US");
        var resolver = new LanguageResolver(detector);

        var result = resolver.Resolve(AppLanguage.Turkish);
        Assert.Equal(EffectiveLanguage.Turkish, result);
    }

    [Fact]
    public void Resolve_ExplicitEnglish_OverridesSystemDetector()
    {
        var detector = new MockLanguageDetector("tr-TR");
        var resolver = new LanguageResolver(detector);

        var result = resolver.Resolve(AppLanguage.English);
        Assert.Equal(EffectiveLanguage.English, result);
    }

    [Fact]
    public void Resolve_SystemPreference_FollowsDetector()
    {
        var trDetector = new MockLanguageDetector("tr-TR");
        var trResolver = new LanguageResolver(trDetector);
        Assert.Equal(EffectiveLanguage.Turkish, trResolver.Resolve(AppLanguage.System));

        var enDetector = new MockLanguageDetector("en-US");
        var enResolver = new LanguageResolver(enDetector);
        Assert.Equal(EffectiveLanguage.English, enResolver.Resolve(AppLanguage.System));
    }

    [Fact]
    public void FormatSelectionLabel_ReturnsAccurateLabels()
    {
        Assert.Equal("Türkçe", LanguageResolver.FormatSelectionLabel(AppLanguage.Turkish, EffectiveLanguage.Turkish, EffectiveLanguage.English));
        Assert.Equal("English", LanguageResolver.FormatSelectionLabel(AppLanguage.English, EffectiveLanguage.English, EffectiveLanguage.Turkish));

        Assert.Equal("System Default (Türkçe)", LanguageResolver.FormatSelectionLabel(AppLanguage.System, EffectiveLanguage.Turkish, EffectiveLanguage.English));
        Assert.Equal("Sistem Varsayılanı (Türkçe)", LanguageResolver.FormatSelectionLabel(AppLanguage.System, EffectiveLanguage.Turkish, EffectiveLanguage.Turkish));
        Assert.Equal("System Default (English)", LanguageResolver.FormatSelectionLabel(AppLanguage.System, EffectiveLanguage.English, EffectiveLanguage.English));
        Assert.Equal("Sistem Varsayılanı (English)", LanguageResolver.FormatSelectionLabel(AppLanguage.System, EffectiveLanguage.English, EffectiveLanguage.Turkish));
    }

    [Fact]
    public void LocalizationService_SetLanguage_FiresLanguageChanged()
    {
        var service = new LocalizationService();
        service.SetLanguage(EffectiveLanguage.English);

        EffectiveLanguage? reported = null;
        service.LanguageChanged += lang => reported = lang;

        service.SetLanguage(EffectiveLanguage.Turkish);
        Assert.Equal(EffectiveLanguage.Turkish, reported);
        Assert.Equal(EffectiveLanguage.Turkish, service.CurrentLanguage);

        reported = null;
        // Setting same language should not fire event
        service.SetLanguage(EffectiveLanguage.Turkish);
        Assert.Null(reported);
    }

    [Fact]
    public void LocalizationService_GetString_SwitchesAccuratelyBetweenLanguages()
    {
        var service = new LocalizationService();

        service.SetLanguage(EffectiveLanguage.English);
        Assert.Equal("Library", service.GetString(LocalizationKeys.NavLibrary));
        Assert.Equal("Settings", service.GetString(LocalizationKeys.NavSettings));

        service.SetLanguage(EffectiveLanguage.Turkish);
        Assert.Equal("Kütüphane", service.GetString(LocalizationKeys.NavLibrary));
        Assert.Equal("Ayarlar", service.GetString(LocalizationKeys.NavSettings));
    }

    [Fact]
    public void LocalizationService_GetString_FallsBackToEnglish_WhenKeyMissingInTurkish()
    {
        var service = new LocalizationService();
        service.RegisterEnglish("Test.OnlyEn", "English Only Value");

        service.SetLanguage(EffectiveLanguage.Turkish);
        Assert.Equal("English Only Value", service.GetString("Test.OnlyEn"));
    }

    [Fact]
    public void LocalizationService_GetString_UnknownKey_ReturnsKeyItself()
    {
        var service = new LocalizationService();
        Assert.Equal("NonExistentKey123", service.GetString("NonExistentKey123"));
    }

    [Fact]
    public void LocalizationService_Format_InterpolatesCorrectly()
    {
        var service = new LocalizationService();
        service.SetLanguage(EffectiveLanguage.English);

        var resultEn = service.Format(LocalizationKeys.FormatCountdownSec, 42);
        Assert.Equal("Next change in 42 sec", resultEn);

        service.SetLanguage(EffectiveLanguage.Turkish);
        var resultTr = service.Format(LocalizationKeys.FormatCountdownSec, 42);
        Assert.Equal("Sonraki değişime 42 sn", resultTr);
    }

    [Fact]
    public void LocalizationService_Format_Malformed_ReturnsTemplateSafely()
    {
        var service = new LocalizationService();
        service.RegisterEnglish("BadTemplate", "Hello {invalid} {99}");

        var formatted = service.Format("BadTemplate", "arg1");
        Assert.Equal("Hello {invalid} {99}", formatted);
    }

    [Fact]
    public async Task LocalizationService_ThreadSafety_ConcurrentReadsAndWrites()
    {
        var service = new LocalizationService();
        var tasks = new List<Task>();

        for (int i = 0; i < 20; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    if (j % 2 == 0)
                    {
                        service.SetLanguage(EffectiveLanguage.Turkish);
                        var s = service.GetString(LocalizationKeys.NavLibrary);
                        Assert.False(string.IsNullOrEmpty(s));
                    }
                    else
                    {
                        service.SetLanguage(EffectiveLanguage.English);
                        var s = service.GetString(LocalizationKeys.NavLibrary);
                        Assert.False(string.IsNullOrEmpty(s));
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void ResourceCompleteness_AllLocalizationKeysHaveEnglishAndTurkishTranslations()
    {
        var service = new LocalizationService();
        var fields = typeof(LocalizationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        Assert.NotEmpty(fields);

        // Check English
        service.SetLanguage(EffectiveLanguage.English);
        foreach (var field in fields)
        {
            var key = (string)field.GetValue(null)!;
            var translation = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(translation), $"Key '{key}' has missing English translation");
            Assert.NotEqual(key, translation);
        }

        // Check Turkish
        service.SetLanguage(EffectiveLanguage.Turkish);
        foreach (var field in fields)
        {
            var key = (string)field.GetValue(null)!;
            var translation = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(translation), $"Key '{key}' has missing Turkish translation");
            Assert.NotEqual(key, translation);
        }
    }

    [Fact]
    public void TurkishResources_DoNotContainMojibakeSequences()
    {
        var service = new LocalizationService();
        service.SetLanguage(EffectiveLanguage.Turkish);

        var fields = typeof(LocalizationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToList();

        var suspiciousPatterns = new[] { "Ã", "Ä", "Å", "Â", "ï¿½", "\uFFFD" };

        foreach (var field in fields)
        {
            var key = (string)field.GetValue(null)!;
            var translation = service.GetString(key);

            foreach (var pattern in suspiciousPatterns)
            {
                Assert.False(translation.Contains(pattern),
                    $"Turkish resource '{key}' contains mojibake sequence '{pattern}': '{translation}'");
            }
        }
    }

    [Theory]
    [InlineData(LocalizationKeys.LibAddWallpaper, "Duvar Kağıdı Ekle")]
    [InlineData(LocalizationKeys.LibAddFolder, "Klasör Ekle")]
    [InlineData(LocalizationKeys.ItemEmptyTitle, "Kütüphaneniz boş")]
    [InlineData(LocalizationKeys.PerfWorkingSet, "Çalışma Belleği (RAM)")]
    [InlineData(LocalizationKeys.PerfUptime, "Çalışma Süresi")]
    [InlineData(LocalizationKeys.PerfHostConnected, "Çalışma Motoru: Bağlı")]
    [InlineData(LocalizationKeys.NavLibrary, "Kütüphane")]
    [InlineData(LocalizationKeys.NavActiveWallpaper, "Aktif Duvar Kağıdı")]
    [InlineData(LocalizationKeys.NavPerformance, "Performans")]
    public void CriticalTurkishStrings_AreAccuratelyLocalized(string key, string expected)
    {
        var service = new LocalizationService();
        service.SetLanguage(EffectiveLanguage.Turkish);

        var actual = service.GetString(key);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PerformanceSnapshotFormatter_TurkishStrings_DoNotContainMojibakeAndMatchExpected()
    {
        var service = new LocalizationService();
        service.SetLanguage(EffectiveLanguage.Turkish);

        Assert.Equal("Boşta: Etkin duvar kağıdı yok", PerformanceSnapshotFormatter.FormatCurrentAction("Idle: No active wallpaper", service));
        Assert.Equal("Çalışma Motoru: Bağlı", PerformanceSnapshotFormatter.FormatHostStatusTitle(true, false, service));
        Assert.Equal("Çalışma Motoru: Bağlantı Yok", PerformanceSnapshotFormatter.FormatHostStatusTitle(false, false, service));
        Assert.Equal("Dengeli", PerformanceSnapshotFormatter.FormatEfficiencyProfile("Balanced", service));
    }

    [Fact]
    public void SourceFiles_WhenRepoRootAccessible_DoNotContainMojibake()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(current);
        string? repoRoot = null;

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NiceWallpaper.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        if (repoRoot == null) return;

        var filesToCheck = new[]
        {
            Path.Combine(repoRoot, "src", "NiceWP.App", "DesktopTestWindow.xaml"),
            Path.Combine(repoRoot, "src", "NiceWP.App", "DesktopTestWindow.xaml.cs"),
            Path.Combine(repoRoot, "src", "NiceWP.Infrastructure", "Localization", "TurkishResources.cs")
        };

        var suspicious = new[] { "Ã¼", "Ã¶", "Ã§", "ÄŸ", "Ä±", "ÅŸ", "Ä°", "Ã‡", "Ã–", "Ãœ", "Äž", "Åž", "â€¦", "â€¢", "Â·", "ï¿½", "\uFFFD" };

        foreach (var file in filesToCheck)
        {
            if (!File.Exists(file)) continue;
            var content = File.ReadAllText(file, System.Text.Encoding.UTF8);
            foreach (var bad in suspicious)
            {
                Assert.DoesNotContain(bad, content);
            }
        }
    }
}
