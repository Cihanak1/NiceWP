using System.Diagnostics;
using System.Text;
using NiceWP.Core;
using NiceWP.Core.Discover;
using NiceWP.Infrastructure.Analysis;
using NiceWP.Infrastructure.Discover;
using NiceWP.Infrastructure.Transition;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class PerformanceAndPolishTests : IDisposable
{
    private readonly string _tempDir;

    public PerformanceAndPolishTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_PerfPolishTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void DiscoverThumbnailMemoryCache_CapacityAndLruEviction_Enforced()
    {
        var cache = new DiscoverThumbnailMemoryCache<string>(capacity: 10);
        Assert.Equal(10, cache.Capacity);
        Assert.Equal(0, cache.Count);

        for (int i = 1; i <= 10; i++)
        {
            cache.Set($"key_{i}", $"value_{i}");
        }
        Assert.Equal(10, cache.Count);

        // Access key_1 to make it freshly accessed
        Assert.True(cache.TryGet("key_1", out var v1));
        Assert.Equal("value_1", v1);

        // Adding 11th item should evict oldest unaccessed item (key_2)
        cache.Set("key_11", "value_11");
        Assert.Equal(10, cache.Count);

        // key_1 must still exist
        Assert.True(cache.TryGet("key_1", out _));
        // key_2 should have been evicted
        Assert.False(cache.TryGet("key_2", out _));
        // key_11 must exist
        Assert.True(cache.TryGet("key_11", out var v11));
        Assert.Equal("value_11", v11);
    }

    [Fact]
    public void DiscoverThumbnailMemoryCache_HitAndMissTracking_Accurate()
    {
        var cache = new DiscoverThumbnailMemoryCache<string>(capacity: 20);

        Assert.False(cache.TryGet("missing", out _));
        Assert.Equal(1, cache.MissCount);
        Assert.Equal(0, cache.HitCount);

        cache.Set("present", "val");
        Assert.True(cache.TryGet("present", out var found));
        Assert.Equal("val", found);
        Assert.Equal(1, cache.HitCount);
        Assert.Equal(1, cache.MissCount);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.HitCount);
        Assert.Equal(0, cache.MissCount);
    }

    [Fact]
    public async Task WallpaperDiscoverCache_PruneExpired_DeletesOldFilesAndEnforcesSizeCap()
    {
        var cacheDir = Path.Combine(_tempDir, "discover_cache");
        Directory.CreateDirectory(cacheDir);

        var cache = new WallpaperDiscoverCache(cacheDir, cacheDuration: TimeSpan.FromMinutes(1));

        // Create expired json file
        var expiredJson = Path.Combine(cacheDir, "q_old.json");
        await File.WriteAllTextAsync(expiredJson, "{}", Encoding.UTF8);
        File.SetLastWriteTimeUtc(expiredJson, DateTime.UtcNow.AddMinutes(-10));

        // Create fresh json file
        var freshJson = Path.Combine(cacheDir, "q_fresh.json");
        await File.WriteAllTextAsync(freshJson, new string('A', 2048), Encoding.UTF8);

        // Create old tmp file
        var oldTmp = Path.Combine(cacheDir, "old.tmp");
        await File.WriteAllTextAsync(oldTmp, "tmp", Encoding.UTF8);
        File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddMinutes(-15));

        // Run prune with small size cap
        int deleted = cache.PruneExpired(maxTotalBytes: 500);

        Assert.True(deleted >= 2);
        Assert.False(File.Exists(expiredJson));
        Assert.False(File.Exists(oldTmp));
    }

    [Fact]
    public async Task OnlineWallpaperDownloader_CleanOrphanTempFiles_DeletesOldTmpFiles()
    {
        var baseDir = Path.Combine(_tempDir, "online-wallpapers");
        var tempDir = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(tempDir);

        var abandonedFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(abandonedFile, "abandoned bytes");
        File.SetLastWriteTimeUtc(abandonedFile, DateTime.UtcNow.AddMinutes(-30));

        var activeFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(activeFile, "active bytes");

        int cleaned = OnlineWallpaperDownloader.CleanOrphanTempFiles(baseDir);

        Assert.Equal(1, cleaned);
        Assert.False(File.Exists(abandonedFile));
        Assert.True(File.Exists(activeFile));
    }

    [Fact]
    public void OnlineWallpaperDownloader_HasSufficientDiskSpace_Behavior()
    {
        // Path on current working drive with 10 bytes required should succeed
        bool ok = OnlineWallpaperDownloader.HasSufficientDiskSpace(_tempDir, 10);
        Assert.True(ok);

        // Requesting 10,000 Terabytes should safely return false without crashing
        bool tooHuge = OnlineWallpaperDownloader.HasSufficientDiskSpace(_tempDir, long.MaxValue - 1024);
        Assert.False(tooHuge);
    }

    [Fact]
    public void WallpaperAnalysisStore_IsDirty_TracksUnsavedMutations()
    {
        var storePath = Path.Combine(_tempDir, "analysis.json");
        var store = new WallpaperAnalysisStore(storePath);

        Assert.False(store.IsDirty);

        var meta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "id-1",
            SourceKey = "file.jpg",
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            MediaType = WallpaperMediaType.Image,
            Width = 1920,
            Height = 1080
        };

        store.Set(meta);
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void DesktopGestureInputMonitor_InvalidateBounds_RefreshesBoundsCleanly()
    {
        var initialBounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
        Assert.True(initialBounds.Width > 0);
        Assert.True(initialBounds.Height > 0);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => null,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        using var monitor = new DesktopGestureInputMonitor(coordinator, () => null);

        // Invalidate bounds should not throw and keep valid geometry
        monitor.InvalidateBounds();
        var refreshedBounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
        Assert.True(refreshedBounds.Width > 0);
    }
}
