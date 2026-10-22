using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Analysis;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.SmartScenes;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class SmartScenesTests : IDisposable
{
    private readonly string _tempDirectory;

    public SmartScenesTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_SmartScenes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    #region 1. Analysis Tests

    [Fact]
    public void ImageAnalysis_DarkBuffer_ClassifiedCorrectly()
    {
        // 64x64 dark pixels (R=20, G=20, B=20, A=255)
        var pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20;     // R
            pixels[i + 1] = 20; // G
            pixels[i + 2] = 20; // B
            pixels[i + 3] = 255;// A
        }

        var meta = ImageWallpaperAnalyzer.ComputeFromRgbaBytes(
            wallpaperId: "dark-1",
            sourceKey: @"C:\dummy\dark.jpg",
            originalWidth: 1920,
            originalHeight: 1080,
            rgbaPixels: pixels,
            fileSizeBytes: 500_000,
            fileModifiedUtc: DateTimeOffset.UtcNow);

        Assert.True(meta.IsDark);
        Assert.False(meta.IsLight);
        Assert.True(meta.Brightness < 0.40);
        Assert.Equal(WallpaperColorTemperature.Neutral, meta.ColorTemperature);
        Assert.Equal(WallpaperMotionLevel.None, meta.MotionLevel);
        Assert.Equal(WallpaperCost.Light, meta.ResourceClass);
        Assert.False(meta.IsHighResolution);
        Assert.Equal(1.778, meta.AspectRatio, precision: 2);
    }

    [Fact]
    public void ImageAnalysis_LightWarmBuffer_ClassifiedCorrectly()
    {
        // 64x64 light warm pixels (R=240, G=210, B=150, A=255)
        var pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 240;    // R (Warm: R - B = 90 > 15)
            pixels[i + 1] = 210;// G
            pixels[i + 2] = 150;// B
            pixels[i + 3] = 255;// A
        }

        var meta = ImageWallpaperAnalyzer.ComputeFromRgbaBytes(
            wallpaperId: "light-warm-1",
            sourceKey: @"C:\dummy\light_warm.jpg",
            originalWidth: 3840,
            originalHeight: 2160,
            rgbaPixels: pixels,
            fileSizeBytes: 2_000_000,
            fileModifiedUtc: DateTimeOffset.UtcNow);

        Assert.False(meta.IsDark);
        Assert.True(meta.IsLight);
        Assert.True(meta.Brightness >= 0.60);
        Assert.Equal(WallpaperColorTemperature.Warm, meta.ColorTemperature);
        Assert.True(meta.IsHighResolution);
        Assert.Equal(WallpaperCost.Light, meta.ResourceClass);
    }

    [Fact]
    public void VideoMotionHeuristic_CategorizesCorrectly()
    {
        // High motion: 60 FPS or 4K with large file size
        var high = VideoWallpaperAnalyzer.EstimateMotionLevel(
            width: 3840, height: 2160, frameRate: 60.0, bitrate: 20_000_000, fileSizeBytes: 150 * 1024 * 1024, durationSeconds: 30.0);
        Assert.Equal(WallpaperMotionLevel.High, high);

        // Low motion: small file size <= 25MB
        var low = VideoWallpaperAnalyzer.EstimateMotionLevel(
            width: 1920, height: 1080, frameRate: 24.0, bitrate: 2_000_000, fileSizeBytes: 10 * 1024 * 1024, durationSeconds: 60.0);
        Assert.Equal(WallpaperMotionLevel.Low, low);

        // Medium motion: standard 1080p
        var med = VideoWallpaperAnalyzer.EstimateMotionLevel(
            width: 1920, height: 1080, frameRate: 30.0, bitrate: 8_000_000, fileSizeBytes: 45 * 1024 * 1024, durationSeconds: 30.0);
        Assert.Equal(WallpaperMotionLevel.Medium, med);
    }

    [Fact]
    public async Task AnalysisCache_PersistenceAndInvalidation_WorksCorrectly()
    {
        var cacheFile = Path.Combine(_tempDirectory, "analysis-cache.json");
        var store = new WallpaperAnalysisStore(cacheFile);

        var testFile = Path.Combine(_tempDirectory, "test_wallpaper.jpg");
        await File.WriteAllBytesAsync(testFile, new byte[1024]);
        var fileInfo = new FileInfo(testFile);

        var meta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "item-1",
            SourceKey = testFile,
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            MediaType = WallpaperMediaType.Image,
            Width = 1920,
            Height = 1080,
            Brightness = 0.35,
            IsDark = true,
            FileSizeBytes = fileInfo.Length,
            FileLastModifiedUtc = fileInfo.LastWriteTimeUtc
        };

        store.Set(meta);
        await store.SaveAsync();

        // Fresh load in new store instance
        var store2 = new WallpaperAnalysisStore(cacheFile);
        await store2.LoadAsync();
        Assert.True(store2.TryGet("item-1", out var loaded));
        Assert.NotNull(loaded);
        Assert.True(WallpaperAnalysisStore.IsMetadataFresh(loaded, testFile));

        // Invalidate: modify file on disk
        await Task.Delay(1100); // Ensure timestamp differs > 1 sec
        await File.WriteAllBytesAsync(testFile, new byte[2048]);
        Assert.False(WallpaperAnalysisStore.IsMetadataFresh(loaded, testFile));
    }

    #endregion

    #region 2. Smart Collections Tests

    [Fact]
    public void SmartCollections_FilterCorrectly()
    {
        var items = new List<WallpaperLibraryItem>
        {
            new() { Id = "dark-static", Type = "Image", FilePath = "f1.jpg", IsFavorite = true },
            new() { Id = "light-static", Type = "Image", FilePath = "f2.jpg", IsFavorite = false },
            new() { Id = "heavy-video", Type = "Video", FilePath = "f3.mp4", IsFavorite = true },
            new() { Id = "light-video", Type = "Video", FilePath = "f4.mp4", IsFavorite = false }
        };

        var metaMap = new Dictionary<string, WallpaperAnalysisMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["dark-static"] = new() { WallpaperId = "dark-static", IsDark = true, IsLight = false, ResourceClass = WallpaperCost.Light, MotionLevel = WallpaperMotionLevel.None, IsHighResolution = false },
            ["light-static"] = new() { WallpaperId = "light-static", IsDark = false, IsLight = true, ResourceClass = WallpaperCost.Light, MotionLevel = WallpaperMotionLevel.None, IsHighResolution = true },
            ["heavy-video"] = new() { WallpaperId = "heavy-video", IsDark = true, IsLight = false, ResourceClass = WallpaperCost.Heavy, MotionLevel = WallpaperMotionLevel.High, IsHighResolution = true },
            ["light-video"] = new() { WallpaperId = "light-video", IsDark = false, IsLight = true, ResourceClass = WallpaperCost.Light, MotionLevel = WallpaperMotionLevel.Low, IsHighResolution = false }
        };

        WallpaperAnalysisMetadata? Lookup(string id) => metaMap.TryGetValue(id, out var m) ? m : null;

        // Dark filter
        var dark = SmartCollectionQuery.Apply(items, SmartCollectionFilter.Dark, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(2, dark.Count);
        Assert.Contains(dark, i => i.Id == "dark-static");
        Assert.Contains(dark, i => i.Id == "heavy-video");

        // Light filter
        var light = SmartCollectionQuery.Apply(items, SmartCollectionFilter.Light, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(2, light.Count);
        Assert.Contains(light, i => i.Id == "light-static");
        Assert.Contains(light, i => i.Id == "light-video");

        // Low resource filter
        var lowRes = SmartCollectionQuery.Apply(items, SmartCollectionFilter.LowResource, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(3, lowRes.Count);
        Assert.DoesNotContain(lowRes, i => i.Id == "heavy-video");

        // High resolution filter
        var highRes = SmartCollectionQuery.Apply(items, SmartCollectionFilter.HighResolution, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(2, highRes.Count);
        Assert.Contains(highRes, i => i.Id == "light-static");
        Assert.Contains(highRes, i => i.Id == "heavy-video");

        // Static filter
        var statics = SmartCollectionQuery.Apply(items, SmartCollectionFilter.Static, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(2, statics.Count);
        Assert.All(statics, i => Assert.Equal("Image", i.Type));

        // Video filter
        var videos = SmartCollectionQuery.Apply(items, SmartCollectionFilter.Video, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(2, videos.Count);
        Assert.All(videos, i => Assert.Equal("Video", i.Type));

        // Low motion filter
        var lowMotion = SmartCollectionQuery.Apply(items, SmartCollectionFilter.LowMotion, i => i.Id, i => i.Type == "Image", i => i.IsFavorite, Lookup).ToList();
        Assert.Equal(3, lowMotion.Count);
        Assert.DoesNotContain(lowMotion, i => i.Id == "heavy-video");
    }

    #endregion

    #region 3. Smart Scene Scoring Tests

    [Fact]
    public void FocusScene_PrefersDarkLowMotionLightResource()
    {
        var darkImage = new WallpaperLibraryItem { Id = "dark-img", Type = "Image", FilePath = "d.jpg" };
        var darkMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "dark-img",
            Brightness = 0.25,
            MotionLevel = WallpaperMotionLevel.None,
            ResourceClass = WallpaperCost.Light
        };

        var brightHeavyVideo = new WallpaperLibraryItem { Id = "bright-heavy", Type = "Video", FilePath = "b.mp4" };
        var heavyMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "bright-heavy",
            Brightness = 0.80,
            MotionLevel = WallpaperMotionLevel.High,
            ResourceClass = WallpaperCost.Heavy
        };

        var ctx = new SmartSceneContext(SmartSceneMode.Focus);

        var scoreDark = WallpaperSceneScorer.Score(darkImage, darkMeta, ctx);
        var scoreHeavy = WallpaperSceneScorer.Score(brightHeavyVideo, heavyMeta, ctx);

        Assert.True(scoreDark.TotalScore > scoreHeavy.TotalScore);
        Assert.True(scoreDark.TotalScore >= 80.0);
        Assert.True(scoreHeavy.TotalScore <= 40.0);
    }

    [Fact]
    public void GamingScene_StronglyPenalizesHeavyVideo()
    {
        var staticImage = new WallpaperLibraryItem { Id = "static-img", Type = "Image", FilePath = "s.jpg" };
        var staticMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "static-img",
            ResourceClass = WallpaperCost.Light,
            MotionLevel = WallpaperMotionLevel.None
        };

        var heavyVideo = new WallpaperLibraryItem { Id = "heavy-vid", Type = "Video", FilePath = "h.mp4" };
        var heavyMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "heavy-vid",
            ResourceClass = WallpaperCost.Heavy,
            MotionLevel = WallpaperMotionLevel.High
        };

        var ctx = new SmartSceneContext(SmartSceneMode.Gaming);

        var scoreStatic = WallpaperSceneScorer.Score(staticImage, staticMeta, ctx);
        var scoreHeavy = WallpaperSceneScorer.Score(heavyVideo, heavyMeta, ctx);

        Assert.True(scoreStatic.TotalScore > scoreHeavy.TotalScore + 40.0);
        Assert.True(scoreHeavy.ResourceScore <= -50.0);
    }

    [Fact]
    public void BatterySaver_StronglyPrefersStaticLight()
    {
        var lightImage = new WallpaperLibraryItem { Id = "battery-friendly", Type = "Image", FilePath = "bf.jpg", IsFavorite = false };
        var imageMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "battery-friendly",
            ResourceClass = WallpaperCost.Light,
            MotionLevel = WallpaperMotionLevel.None
        };

        var heavyFavoriteVideo = new WallpaperLibraryItem { Id = "heavy-fav", Type = "Video", FilePath = "fav.mp4", IsFavorite = true };
        var videoMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "heavy-fav",
            ResourceClass = WallpaperCost.Heavy,
            MotionLevel = WallpaperMotionLevel.High
        };

        var ctx = new SmartSceneContext(SmartSceneMode.BatterySaver, PreferFavorites: true);

        var scoreImage = WallpaperSceneScorer.Score(lightImage, imageMeta, ctx);
        var scoreVideo = WallpaperSceneScorer.Score(heavyFavoriteVideo, videoMeta, ctx);

        // Even with FavoriteBonus (+10), heavy video must LOSE in BatterySaver!
        Assert.True(scoreImage.TotalScore > scoreVideo.TotalScore);
        Assert.True(scoreVideo.TotalScore <= 20.0);
    }

    [Fact]
    public void NightScene_PrefersDarkWallpapers()
    {
        var dark = new WallpaperLibraryItem { Id = "night-dark", Type = "Image", FilePath = "nd.jpg" };
        var darkMeta = new WallpaperAnalysisMetadata { WallpaperId = "night-dark", Brightness = 0.20, MotionLevel = WallpaperMotionLevel.None };

        var bright = new WallpaperLibraryItem { Id = "night-bright", Type = "Image", FilePath = "nb.jpg" };
        var brightMeta = new WallpaperAnalysisMetadata { WallpaperId = "night-bright", Brightness = 0.85, MotionLevel = WallpaperMotionLevel.None };

        var ctx = new SmartSceneContext(SmartSceneMode.Night);

        var scoreDark = WallpaperSceneScorer.Score(dark, darkMeta, ctx);
        var scoreBright = WallpaperSceneScorer.Score(bright, brightMeta, ctx);

        Assert.True(scoreDark.TotalScore > scoreBright.TotalScore + 30.0);
    }

    #endregion

    #region 4. Selection & Diversity Tests

    [Fact]
    public void SmartWallpaperSelector_AppliesRecentPenaltyAndSelectsWinner()
    {
        var fileA = Path.Combine(_tempDirectory, "wall_a.jpg");
        var fileB = Path.Combine(_tempDirectory, "wall_b.jpg");
        File.WriteAllBytes(fileA, [1, 2, 3]);
        File.WriteAllBytes(fileB, [1, 2, 3]);

        var itemA = new WallpaperLibraryItem { Id = "item-a", Type = "Image", FilePath = fileA };
        var itemB = new WallpaperLibraryItem { Id = "item-b", Type = "Image", FilePath = fileB };

        var metaA = new WallpaperAnalysisMetadata { WallpaperId = "item-a", Brightness = 0.30, ResourceClass = WallpaperCost.Light };
        var metaB = new WallpaperAnalysisMetadata { WallpaperId = "item-b", Brightness = 0.30, ResourceClass = WallpaperCost.Light };

        // Item A was immediately recently used!
        var ctx = new SmartSceneContext(
            EffectiveScene: SmartSceneMode.Focus,
            RecentWallpaperIds: ["item-a"],
            AvoidRecent: true);

        var result = SmartWallpaperSelector.Select(
            [itemA, itemB],
            id => id == "item-a" ? metaA : metaB,
            ctx);

        Assert.NotNull(result);
        Assert.Equal("item-b", result.SelectedItem.Id);
    }

    [Fact]
    public void SmartWallpaperSelector_MissingFiles_AreSafelySkipped()
    {
        var missingItem = new WallpaperLibraryItem { Id = "missing", Type = "Image", FilePath = @"C:\NonExistent\Ghost.jpg" };
        var ctx = new SmartSceneContext(SmartSceneMode.Focus);

        var result = SmartWallpaperSelector.Select([missingItem], _ => null, ctx);
        Assert.Null(result);
    }

    #endregion

    #region 5. Context Resolution Tests

    [Theory]
    [InlineData(SmartSceneMode.Focus, AdaptiveDesktopProfile.Normal, false, SmartSceneMode.Focus)]
    [InlineData(SmartSceneMode.Gaming, AdaptiveDesktopProfile.Normal, false, SmartSceneMode.Gaming)]
    [InlineData(SmartSceneMode.Night, AdaptiveDesktopProfile.Normal, false, SmartSceneMode.Night)]
    [InlineData(SmartSceneMode.Off, AdaptiveDesktopProfile.Gaming, false, SmartSceneMode.Off)]
    public void ContextResolver_ManualSetting_OverridesAutomatic(
        SmartSceneMode configured,
        AdaptiveDesktopProfile adaptive,
        bool onBattery,
        SmartSceneMode expected)
    {
        var resolved = SmartSceneContextResolver.ResolveEffectiveScene(configured, adaptive, onBattery);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ContextResolver_Automatic_MapsAdaptiveProfiles()
    {
        // Gaming -> Gaming
        Assert.Equal(SmartSceneMode.Gaming, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Gaming));

        // BatterySaver -> BatterySaver
        Assert.Equal(SmartSceneMode.BatterySaver, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.BatterySaver));

        // On battery -> BatterySaver
        Assert.Equal(SmartSceneMode.BatterySaver, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Normal, isOnBattery: true));

        // Cinema -> Relax
        Assert.Equal(SmartSceneMode.Relax, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Cinema));

        // Focus & Presentation -> Focus
        Assert.Equal(SmartSceneMode.Focus, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Focus));
        Assert.Equal(SmartSceneMode.Focus, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Presentation));

        // Night hours (23:00) in Normal profile -> Night
        Assert.Equal(SmartSceneMode.Night, SmartSceneContextResolver.ResolveEffectiveScene(SmartSceneMode.Automatic, AdaptiveDesktopProfile.Normal, timeOfDay: TimeSpan.FromHours(23)));
    }

    #endregion

    #region 6. Settings Compatibility Tests

    [Fact]
    public void Settings_CompatibilityWith0018_HasSafeDefaults()
    {
        // Old 0.0.18 settings json without smart scenes fields
        var jsonOld = """
        {
            "RestoreLastWallpaper": true,
            "RotationMode": "All",
            "RotationOrder": "Sequential"
        }
        """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(jsonOld);
        Assert.NotNull(settings);

        Assert.False(settings.SmartScenesEnabled);
        Assert.Equal(SmartSceneMode.Automatic, settings.EffectiveSmartScene);
        Assert.False(settings.SmartRotation);
        Assert.True(settings.SmartScenesAvoidRecent);
        Assert.True(settings.SmartScenesPreferFavorites);

        // Normalize preserves valid state
        var normalized = settings.Normalize();
        Assert.False(normalized.SmartScenesEnabled);
        Assert.Null(normalized.SmartScene);
    }

    #endregion

    #region 7. Localization Tests

    [Fact]
    public void Localization_SmartScenesKeys_PresentInBothLanguages()
    {
        var service = new LocalizationService();

        service.SetLanguage(EffectiveLanguage.Turkish);
        Assert.Equal("Akıllı Sahneler", service.GetString(LocalizationKeys.SetSmartScenesHeader));
        Assert.Equal("Akıllı Döngü", service.GetString(LocalizationKeys.SetSmartRotation));
        Assert.Equal("Odak", service.GetString(LocalizationKeys.SmartSceneFocus));
        Assert.Equal("Koyu", service.GetString(LocalizationKeys.FilterDark));
        Assert.Equal("Hafif kaynak kullanımı", service.GetString(LocalizationKeys.ActiveReasonLightResource));

        service.SetLanguage(EffectiveLanguage.English);
        Assert.Equal("Smart Scenes", service.GetString(LocalizationKeys.SetSmartScenesHeader));
        Assert.Equal("Smart Rotation", service.GetString(LocalizationKeys.SetSmartRotation));
        Assert.Equal("Focus", service.GetString(LocalizationKeys.SmartSceneFocus));
        Assert.Equal("Dark", service.GetString(LocalizationKeys.FilterDark));
        Assert.Equal("Light resource usage", service.GetString(LocalizationKeys.ActiveReasonLightResource));
    }

    #endregion
}
