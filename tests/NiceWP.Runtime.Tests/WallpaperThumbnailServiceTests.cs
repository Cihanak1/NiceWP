using NiceWP.Infrastructure.Library;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperThumbnailServiceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string userDataDirectory;
    private readonly string cacheDirectory;

    public WallpaperThumbnailServiceTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_ThumbnailTests_{Guid.NewGuid():N}");
        userDataDirectory = Path.Combine(tempDirectory, "userdata");
        cacheDirectory = Path.Combine(tempDirectory, "cache", "thumbnails");
        Directory.CreateDirectory(userDataDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore temporary directory cleanup errors in tests
        }
    }

    [Fact]
    public void DefaultCacheDirectory_IsSeparateFromLibraryAndSettingsFiles()
    {
        var defaultCacheDir = WallpaperThumbnailService.GetDefaultCacheDirectory();
        var defaultLibraryPath = WallpaperLibraryService.GetDefaultLibraryPath();
        var libraryParentDir = Path.GetDirectoryName(defaultLibraryPath)!;

        Assert.NotEqual(
            Path.GetFullPath(libraryParentDir),
            Path.GetFullPath(defaultCacheDir),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cache", defaultCacheDir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thumbnails", defaultCacheDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_WithValidMp4Video_GeneratesDecodablePngAndPreservesOriginalVideo()
    {
        var sampleDir = VideoProfileTests.ResolveValidationVideoDirectory();
        var sampleVideo = Path.Combine(sampleDir, "sample-5s.mp4");
        await VideoProfileTests.EnsureValidH264SampleVideoAsync(
            sampleVideo,
            TimeSpan.FromSeconds(5),
            Color.FromArgb(255, 36, 98, 178));

        var userVideoPath = Path.Combine(userDataDirectory, "wallpaper_clip.mp4");
        File.Copy(sampleVideo, userVideoPath, overwrite: true);
        var originalBytes = await File.ReadAllBytesAsync(userVideoPath);
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(userVideoPath);

        var service = new WallpaperThumbnailService(cacheDirectory);

        var thumbnailPath = await service.GetOrCreateVideoThumbnailAsync(userVideoPath);

        Assert.NotNull(thumbnailPath);
        Assert.True(File.Exists(thumbnailPath), $"Expected cached thumbnail at: {thumbnailPath}");
        Assert.StartsWith(
            Path.GetFullPath(cacheDirectory),
            Path.GetFullPath(thumbnailPath!),
            StringComparison.OrdinalIgnoreCase);

        // Verify the generated file is a valid decodable PNG image
        var storageFile = await StorageFile.GetFileFromPathAsync(thumbnailPath);
        using (var stream = await storageFile.OpenAsync(FileAccessMode.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId);
            Assert.True(decoder.PixelWidth > 0);
            Assert.True(decoder.PixelHeight > 0);
        }

        // Verify original user video file was neither modified nor left locked
        var afterBytes = await File.ReadAllBytesAsync(userVideoPath);
        Assert.Equal(originalBytes, afterBytes);
        Assert.Equal(originalWriteTimeUtc, File.GetLastWriteTimeUtc(userVideoPath));

        using var exclusiveCheck = new FileStream(userVideoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusiveCheck.Length > 0);
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_RepeatedCallsForUnchangedVideo_ReusesCacheWithoutRegenerating()
    {
        var videoPath = Path.Combine(userDataDirectory, "cached_loop.mp4");
        await File.WriteAllBytesAsync(videoPath, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);

        var extractionCount = 0;
        var service = new WallpaperThumbnailService(
            cacheDirectory,
            async (_, destinationPngPath, ct) =>
            {
                Interlocked.Increment(ref extractionCount);
                await File.WriteAllBytesAsync(destinationPngPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], ct);
                return true;
            });

        var firstPath = await service.GetOrCreateVideoThumbnailAsync(videoPath);
        var secondPath = await service.GetOrCreateVideoThumbnailAsync(videoPath);

        Assert.NotNull(firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.Equal(1, extractionCount);
        Assert.Single(Directory.GetFiles(cacheDirectory, "*.png"));
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_WhenSourceVideoIsModified_InvalidatesOldCacheAndGeneratesNewThumbnail()
    {
        var videoPath = Path.Combine(userDataDirectory, "mutable_video.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(videoPath, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        var extractionCount = 0;
        var service = new WallpaperThumbnailService(
            cacheDirectory,
            async (_, destinationPngPath, ct) =>
            {
                var callIndex = Interlocked.Increment(ref extractionCount);
                await File.WriteAllBytesAsync(destinationPngPath, [0x89, 0x50, 0x4E, 0x47, (byte)callIndex], ct);
                return true;
            });

        var firstThumbnail = await service.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.NotNull(firstThumbnail);
        Assert.True(File.Exists(firstThumbnail));
        Assert.Equal(1, extractionCount);

        // Modify source video content and timestamp on disk
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        File.SetLastWriteTimeUtc(videoPath, new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        var secondThumbnail = await service.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.NotNull(secondThumbnail);
        Assert.Equal(2, extractionCount);
        Assert.NotEqual(firstThumbnail, secondThumbnail);
        Assert.True(File.Exists(secondThumbnail));

        // Stale cache file for the same video path should be cleaned up automatically
        Assert.False(File.Exists(firstThumbnail));
        Assert.Single(Directory.GetFiles(cacheDirectory, "*.png"));
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_WhenVideoFileIsDeleted_ReturnsNullAndPreservesMissingLibraryState()
    {
        var libraryPath = Path.Combine(userDataDirectory, "library.json");
        var libraryService = new WallpaperLibraryService(libraryPath);
        var controller = new WallpaperLibraryController(libraryService);

        var videoPath = Path.Combine(userDataDirectory, "deletable_video.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4, 5, 6]);

        var addResult = await controller.AddFileAsync(videoPath);
        Assert.True(addResult.Success);
        Assert.False(controller.Items[0].IsMissing);

        var thumbnailService = new WallpaperThumbnailService(
            cacheDirectory,
            async (_, destinationPngPath, ct) =>
            {
                await File.WriteAllBytesAsync(destinationPngPath, [0x89, 0x50, 0x4E, 0x47], ct);
                return true;
            });

        var initialThumb = await thumbnailService.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.NotNull(initialThumb);

        // User deletes the source video file from disk
        File.Delete(videoPath);

        var thumbAfterDelete = await thumbnailService.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.Null(thumbAfterDelete);

        var reloadedItems = await controller.LoadAsync();
        Assert.Single(reloadedItems);
        Assert.True(reloadedItems[0].IsMissing);
        Assert.False(reloadedItems[0].CanApply);
        Assert.Equal("Missing", reloadedItems[0].StatusText);
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_WhenVideoIsCorrupt_ReturnsNullGracefullyWithoutThrowing()
    {
        var corruptVideoPath = Path.Combine(userDataDirectory, "corrupt_video.mp4");
        await File.WriteAllBytesAsync(corruptVideoPath, [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0xFF, 0xFE]);

        var service = new WallpaperThumbnailService(cacheDirectory);

        var thumbnailPath = await service.GetOrCreateVideoThumbnailAsync(corruptVideoPath);

        Assert.Null(thumbnailPath);
        if (Directory.Exists(cacheDirectory))
        {
            Assert.Empty(Directory.GetFiles(cacheDirectory, "*.tmp.png"));
        }
    }

    [Fact]
    public async Task GetOrCreateVideoThumbnailAsync_ConcurrentRequestsForSameVideo_ExtractsOnlyOnce()
    {
        var videoPath = Path.Combine(userDataDirectory, "concurrent_video.mp4");
        await File.WriteAllBytesAsync(videoPath, [10, 20, 30, 40, 50]);

        var extractionCount = 0;
        var service = new WallpaperThumbnailService(
            cacheDirectory,
            async (_, destinationPngPath, ct) =>
            {
                Interlocked.Increment(ref extractionCount);
                await Task.Delay(40, ct);
                await File.WriteAllBytesAsync(destinationPngPath, [0x89, 0x50, 0x4E, 0x47], ct);
                return true;
            });

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => service.GetOrCreateVideoThumbnailAsync(videoPath)));

        Assert.Equal(1, extractionCount);
        Assert.All(results, path =>
        {
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        });
    }
}
