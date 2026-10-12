using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NiceWP.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Provides asynchronous video frame thumbnail extraction and local disk caching for wallpaper previews.
/// Cache files are stored separately from user data under %LocalAppData%\NiceWP\cache\thumbnails
/// and are automatically invalidated when the source file size or last-write timestamp changes.
/// </summary>
public sealed class WallpaperThumbnailService
{
    public const string DefaultCacheFolderName = "cache";
    public const string DefaultThumbnailsFolderName = "thumbnails";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, CancellationToken, Task<bool>> videoFrameExtractor;

    public string CacheDirectory { get; }

    public WallpaperThumbnailService(
        string? cacheDirectory = null,
        Func<string, string, CancellationToken, Task<bool>>? videoFrameExtractor = null)
    {
        CacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? GetDefaultCacheDirectory()
            : Path.GetFullPath(cacheDirectory);
        this.videoFrameExtractor = videoFrameExtractor ?? ExtractVideoThumbnailToPngAsync;
    }

    public static string GetDefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            WallpaperLibraryService.DefaultFolderName,
            DefaultCacheFolderName,
            DefaultThumbnailsFolderName);

    /// <summary>
    /// Computes the deterministic cache file path for the current state of the specified video file on disk,
    /// or returns null if the file is missing, empty, or invalid.
    /// </summary>
    public string? GetExpectedCacheFilePath(string? videoFilePath)
    {
        if (!TryResolveCacheTokens(videoFilePath, out _, out var pathHash, out var versionHash))
        {
            return null;
        }

        return Path.Combine(CacheDirectory, $"{pathHash}_{versionHash}.png");
    }

    /// <summary>
    /// Returns the path to a cached PNG thumbnail for the specified video wallpaper, generating it
    /// asynchronously if not yet cached or if the source video file has changed on disk.
    /// Returns null if the file is missing, unsupported, or a thumbnail frame cannot be extracted.
    /// Never modifies or locks the original video file.
    /// </summary>
    public async Task<string?> GetOrCreateVideoThumbnailAsync(
        string? videoFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveCacheTokens(videoFilePath, out var fullPath, out var pathHash, out var versionHash))
        {
            return null;
        }

        var cachedFilePath = Path.Combine(CacheDirectory, $"{pathHash}_{versionHash}.png");
        if (IsValidThumbnailFile(cachedFilePath))
        {
            return cachedFilePath;
        }

        var keyLock = keyLocks.GetOrAdd(pathHash, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsValidThumbnailFile(cachedFilePath))
            {
                return cachedFilePath;
            }

            if (!File.Exists(fullPath))
            {
                return null;
            }

            Directory.CreateDirectory(CacheDirectory);
            var tempFilePath = Path.Combine(CacheDirectory, $"{pathHash}_{versionHash}_{Guid.NewGuid():N}.tmp.png");

            try
            {
                var extracted = await videoFrameExtractor(fullPath, tempFilePath, cancellationToken).ConfigureAwait(false);
                if (!extracted || !IsValidThumbnailFile(tempFilePath))
                {
                    TryDeleteFile(tempFilePath);
                    return null;
                }

                File.Move(tempFilePath, cachedFilePath, overwrite: true);
                DeleteStaleThumbnails(pathHash, cachedFilePath);
                return cachedFilePath;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempFilePath);
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error caching video thumbnail for '{fullPath}': {ex.Message}");
                TryDeleteFile(tempFilePath);
                return null;
            }
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>
    /// Extracts a frame from a local MP4 video file using Windows Media APIs and encodes it as a PNG file.
    /// Never modifies the source video file.
    /// </summary>
    public static async Task<bool> ExtractVideoThumbnailToPngAsync(
        string fullPath,
        string destinationPngPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return false;
            }

            var storageFile = await StorageFile.GetFileFromPathAsync(fullPath)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            // 1. Primary: Extract an actual video frame via MediaClip & MediaComposition
            try
            {
                var clip = await MediaClip.CreateFromFileAsync(storageFile)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                var composition = new MediaComposition();
                composition.Clips.Add(clip);

                var duration = composition.Duration;
                var timeFromStart = duration <= TimeSpan.Zero
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(Math.Min(1000.0, Math.Max(0.0, duration.TotalMilliseconds * 0.25)));

                var (scaledWidth, scaledHeight) = ResolveThumbnailDimensions(clip);

                using var frameStream = await composition.GetThumbnailAsync(
                        timeFromStart,
                        scaledWidth,
                        scaledHeight,
                        VideoFramePrecision.NearestKeyFrame)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

                if (frameStream != null && frameStream.Size > 0)
                {
                    if (await TranscodeStreamToPngFileAsync(frameStream, destinationPngPath, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception mediaEx)
            {
                Trace.WriteLine($"MediaComposition frame extraction failed for '{fullPath}': {mediaEx.Message}");
            }

            // 2. Fallback: Windows Shell / Media Foundation thumbnail provider (only if an actual image frame is returned)
            try
            {
                using var shellThumbnail = await storageFile.GetThumbnailAsync(
                        ThumbnailMode.VideosView,
                        320,
                        ThumbnailOptions.ResizeThumbnail)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

                if (shellThumbnail != null &&
                    shellThumbnail.Type == ThumbnailType.Image &&
                    shellThumbnail.Size > 0)
                {
                    if (await TranscodeStreamToPngFileAsync(shellThumbnail, destinationPngPath, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception shellEx)
            {
                Trace.WriteLine($"Shell video thumbnail fallback failed for '{fullPath}': {shellEx.Message}");
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to extract video thumbnail for '{fullPath}': {ex.Message}");
            return false;
        }
    }

    private static (int Width, int Height) ResolveThumbnailDimensions(MediaClip clip)
    {
        const int targetWidth = 320;
        const int defaultHeight = 180;

        try
        {
            var props = clip.GetVideoEncodingProperties();
            if (props != null && props.Width > 0 && props.Height > 0)
            {
                var computedHeight = (int)Math.Round((double)targetWidth * props.Height / props.Width);
                if (computedHeight < 2)
                {
                    computedHeight = 2;
                }

                if (computedHeight % 2 != 0)
                {
                    computedHeight++;
                }

                return (targetWidth, computedHeight);
            }
        }
        catch
        {
            // Fall back to default 16:9 thumbnail dimensions
        }

        return (targetWidth, defaultHeight);
    }

    private static async Task<bool> TranscodeStreamToPngFileAsync(
        IRandomAccessStream inputStream,
        string destinationPngPath,
        CancellationToken cancellationToken)
    {
        inputStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(inputStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        await using var fileStream = new FileStream(
            destinationPngPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);
        using var outputStream = fileStream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return fileStream.Length > 0;
    }

    private static bool TryResolveCacheTokens(
        string? videoFilePath,
        out string fullPath,
        out string pathHash,
        out string versionHash)
    {
        fullPath = string.Empty;
        pathHash = string.Empty;
        versionHash = string.Empty;

        if (string.IsNullOrWhiteSpace(videoFilePath))
        {
            return false;
        }

        var validation = VideoWallpaper.Validate(videoFilePath.Trim(), out fullPath);
        if (!validation.Success)
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
            {
                return false;
            }

            var normalizedUpperPath = fullPath.ToUpperInvariant();
            pathHash = ComputeSha256Hex(normalizedUpperPath, 16);
            versionHash = ComputeSha256Hex(
                $"{normalizedUpperPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}",
                16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256Hex(string input, int hexLength)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hexLength < hex.Length ? hex[..hexLength] : hex;
    }

    private static bool IsValidThumbnailFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteStaleThumbnails(string pathHash, string activeCacheFilePath)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            var candidates = Directory.GetFiles(CacheDirectory, $"{pathHash}_*.png");
            foreach (var candidate in candidates)
            {
                if (!string.Equals(candidate, activeCacheFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(candidate);
                }
            }
        }
        catch
        {
            // Ignore best-effort stale cache cleanup errors
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temporary file cleanup failures
        }
    }
}
