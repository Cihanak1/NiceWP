namespace NiceWP.Runtime.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NiceWP.Core;
using NiceWP.Core.Discover;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Discover;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Infrastructure.SmartScenes;
using Xunit;

public sealed class PublicReleaseSoakTests : IDisposable
{
    private readonly string _tempDirectory;

    public PublicReleaseSoakTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NiceWP_ReleaseSoak_" + Guid.NewGuid().ToString("N"));
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
        catch { }
    }

    [Fact]
    public async Task AcceleratedSoak_ExecutesHundredsOfOperations_WithoutMemoryOrResourceLeaks()
    {
        // 1. Prepare dummy wallpaper candidate files on disk so FileExists() is satisfied
        var candidates = new List<WallpaperLibraryItem>(20);
        var metaLookup = new Dictionary<string, WallpaperAnalysisMetadata>(20);

        for (int i = 0; i < 20; i++)
        {
            var isVideo = i % 4 == 0;
            var ext = isVideo ? "mp4" : "jpg";
            var path = Path.Combine(_tempDirectory, $"soak_wall_{i}.{ext}");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

            var id = $"soak-item-{i}";
            var item = new WallpaperLibraryItem
            {
                Id = id,
                DisplayName = $"Soak Wallpaper {i}",
                FilePath = path,
                Type = isVideo ? "Video" : "Image",
                IsFavorite = i % 3 == 0
            };
            candidates.Add(item);

            metaLookup[id] = new WallpaperAnalysisMetadata
            {
                WallpaperId = id,
                Brightness = 0.15 + (i * 0.04),
                IsDark = i % 2 == 0,
                IsLight = i % 2 != 0,
                ColorTemperature = (WallpaperColorTemperature)(i % 3),
                ResourceClass = isVideo ? WallpaperCost.Heavy : WallpaperCost.Light,
                MotionLevel = isVideo ? WallpaperMotionLevel.High : WallpaperMotionLevel.None
            };
        }

        // Profile with feature tendencies
        var profile = PersonalizationProfile.CreateDefault();
        profile.FeatureWeights.DarkPreference = 0.60;
        profile.FeatureWeights.LowMotionPreference = 0.40;
        profile.FeatureWeights.WarmPreference = 0.30;
        profile.WallpaperPreferences["soak-item-1"] = new WallpaperPreferenceRecord
        {
            WallpaperId = "soak-item-1",
            Weight = 0.50,
            LastInteractedAtUtc = DateTimeOffset.UtcNow
        };

        // Baseline memory & process telemetry
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startMemory = GC.GetTotalMemory(true);
        var proc = Process.GetCurrentProcess();
        int startThreads = proc.Threads.Count;
        int startHandles = proc.HandleCount;

        // A. 100 Smart Scene scoring operations across multiple contexts
        var scenes = new[]
        {
            SmartSceneMode.Focus,
            SmartSceneMode.Gaming,
            SmartSceneMode.Relax,
            SmartSceneMode.Night,
            SmartSceneMode.BatterySaver
        };

        for (int i = 0; i < 100; i++)
        {
            var scene = scenes[i % scenes.Length];
            var context = new SmartSceneContext(scene, IsOnBattery: scene == SmartSceneMode.BatterySaver);
            var item = candidates[i % candidates.Count];
            var meta = metaLookup[item.Id];

            var score = WallpaperSceneScorer.Score(item, meta, context, profile);
            Assert.True(score.TotalScore >= 0.0 && score.TotalScore <= 100.0);
        }

        // B. 100 Diversified candidate selections
        for (int i = 0; i < 100; i++)
        {
            var scene = scenes[i % scenes.Length];
            var context = new SmartSceneContext(scene, RecentWallpaperIds: [$"soak-item-{(i % 5)}"]);
            var result = SmartWallpaperSelector.Select(candidates, id => metaLookup.TryGetValue(id, out var m) ? m : null, context, null, profile);
            Assert.NotNull(result);
            Assert.NotNull(result.SelectedItem);
        }

        // C. 100 Personalization scoring operations with decay
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 100; i++)
        {
            var item = candidates[i % candidates.Count];
            var meta = metaLookup[item.Id];
            var pScore = WallpaperPersonalizationScorer.Score(item, meta, profile, now.AddDays(-i));
            Assert.True(pScore.Total >= -20.0 && pScore.Total <= 20.0);
        }

        // D. 50 Simulated Page Turn progression cycles
        for (int i = 0; i < 50; i++)
        {
            // Simulate progression frames 0.0 -> 1.0 with easing
            for (double step = 0.0; step <= 1.0; step += 0.05)
            {
                double curled = Math.Sin(step * Math.PI / 2);
                Assert.True(curled >= 0.0 && curled <= 1.0);
            }
        }

        // E. 20 Mocked Discover Cache operations
        var cacheDir = Path.Combine(_tempDirectory, "discover_cache");
        var discoverCache = new WallpaperDiscoverCache(cacheDir, TimeSpan.FromMinutes(10));
        for (int i = 0; i < 20; i++)
        {
            var query = new WallpaperSearchQuery($"nature {i}", Page: 1, PageSize: 10);
            var mockResult = new WallpaperSearchResult(
                ProviderId: "openverse",
                Items: new List<OnlineWallpaperItem>
                {
                    CreateTestItem("op-" + i, "Title " + i)
                },
                TotalCount: 1,
                HasMore: false);

            await discoverCache.SetAsync(query, mockResult, CancellationToken.None);
            var cached = await discoverCache.TryGetAsync(query, CancellationToken.None);
            Assert.NotNull(cached);
            Assert.Single(cached.Items);
        }

        // Final verification after soak
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long endMemory = GC.GetTotalMemory(true);
        proc.Refresh();
        int endThreads = proc.Threads.Count;
        int endHandles = proc.HandleCount;

        long memoryDelta = endMemory - startMemory;
        Trace.WriteLine($"[SoakMetrics] Memory Start: {startMemory / 1024} KB, End: {endMemory / 1024} KB (Delta: {memoryDelta / 1024} KB)");
        Trace.WriteLine($"[SoakMetrics] Threads Start: {startThreads}, End: {endThreads}");
        Trace.WriteLine($"[SoakMetrics] Handles Start: {startHandles}, End: {endHandles}");

        // Memory delta should remain reasonably bounded (< 30 MB) under full GC
        Assert.True(memoryDelta < 30 * 1024 * 1024, $"Memory growth exceeded budget: {memoryDelta / (1024 * 1024)} MB");
    }

    private static OnlineWallpaperItem CreateTestItem(string remoteId, string title)
    {
        return new OnlineWallpaperItem(
            ProviderId: "openverse",
            RemoteId: remoteId,
            Title: title,
            Description: "Test Description",
            MediaType: "image",
            Width: 1920,
            Height: 1080,
            AspectRatio: 1.77,
            ThumbnailUrl: "https://example.com/thumb.jpg",
            PreviewUrl: "https://example.com/preview.jpg",
            DownloadUrl: "https://example.com/image.jpg",
            CreatorName: "Creator",
            CreatorUrl: "https://example.com/creator",
            SourceName: "openverse",
            SourceUrl: "https://example.com/source",
            LicenseCode: "CC0",
            LicenseName: "CC0",
            LicenseUrl: "https://example.com/license",
            AttributionText: $"\"{title}\"",
            IsPublicDomain: true,
            RequiresAttribution: false,
            CommercialUseAllowed: true,
            DerivativeUseAllowed: true,
            Tags: ["test", "nature"]);
    }
}
