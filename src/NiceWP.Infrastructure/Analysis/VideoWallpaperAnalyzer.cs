using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using Windows.Storage;

namespace NiceWP.Infrastructure.Analysis;

/// <summary>
/// Provides lightweight video wallpaper metadata extraction and heuristic motion/resource classification
/// without incurring heavy frame-by-frame decode overhead.
/// </summary>
public static class VideoWallpaperAnalyzer
{
    /// <summary>
    /// Analyzes a video file on disk asynchronously using Windows Shell Media properties and existing thumbnail cache.
    /// </summary>
    public static async Task<WallpaperAnalysisMetadata?> AnalyzeAsync(
        string wallpaperId,
        string filePath,
        WallpaperThumbnailService? thumbnailService = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSizeBytes = fileInfo.Length;
            var fileModifiedUtc = fileInfo.LastWriteTimeUtc;

            var storageFile = await StorageFile.GetFileFromPathAsync(filePath)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var videoProps = await storageFile.Properties.GetVideoPropertiesAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            int width = (int)videoProps.Width;
            int height = (int)videoProps.Height;
            double durationSeconds = videoProps.Duration.TotalSeconds;
            long? bitrate = videoProps.Bitrate > 0 ? (long)videoProps.Bitrate : null;

            // Default fallback if metadata reader returned zeros
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;

            double aspectRatio = height > 0
                ? Math.Round((double)width / height, 3)
                : 1.778;

            bool isHighRes = width >= 2560 || height >= 1440;
            var resourceClass = WallpaperCostClassifier.Classify(
                IpcConstants.WallpaperTypeVideo,
                (uint)width,
                (uint)height,
                fileSizeBytes);

            var motionLevel = EstimateMotionLevel(
                width: width,
                height: height,
                frameRate: null,
                bitrate: bitrate,
                fileSizeBytes: fileSizeBytes,
                durationSeconds: durationSeconds);

            // Check if preview thumbnail is available to sample brightness and dominant chromatic temperature
            double brightness = 0.5;
            string dominantHex = "#333333";
            var temperature = WallpaperColorTemperature.Neutral;

            if (thumbnailService != null)
            {
                var cachedThumb = thumbnailService.GetExpectedCacheFilePath(filePath);
                if (!string.IsNullOrWhiteSpace(cachedThumb) && File.Exists(cachedThumb))
                {
                    var thumbMeta = await ImageWallpaperAnalyzer.AnalyzeAsync(wallpaperId, cachedThumb, cancellationToken).ConfigureAwait(false);
                    if (thumbMeta != null)
                    {
                        brightness = thumbMeta.Brightness;
                        dominantHex = thumbMeta.DominantColorHex;
                        temperature = thumbMeta.ColorTemperature;
                    }
                }
            }

            bool isDark = brightness < 0.40;
            bool isLight = brightness >= 0.60;

            return new WallpaperAnalysisMetadata
            {
                WallpaperId = wallpaperId,
                SourceKey = filePath,
                AnalyzedAtUtc = DateTimeOffset.UtcNow,
                MediaType = WallpaperMediaType.Video,
                Width = width,
                Height = height,
                AspectRatio = aspectRatio,
                Brightness = Math.Round(brightness, 3),
                DominantColorHex = dominantHex,
                ColorTemperature = temperature,
                MotionLevel = motionLevel,
                ResourceClass = resourceClass,
                IsDark = isDark,
                IsLight = isLight,
                IsHighResolution = isHighRes,
                DurationSeconds = durationSeconds > 0 ? Math.Round(durationSeconds, 2) : null,
                FrameRate = null,
                Bitrate = bitrate,
                FileSizeBytes = fileSizeBytes,
                FileLastModifiedUtc = fileModifiedUtc
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoWallpaperAnalyzer] Failed to analyze '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Evaluates motion level using bitrate, file size, duration, and resolution heuristics.
    /// </summary>
    public static WallpaperMotionLevel EstimateMotionLevel(
        int width,
        int height,
        double? frameRate,
        long? bitrate,
        long fileSizeBytes,
        double durationSeconds)
    {
        // High motion: 60fps+, or bitrate >= 15 Mbps, or 4K file >= 100MB
        if ((frameRate.HasValue && frameRate.Value >= 50.0) ||
            (bitrate.HasValue && bitrate.Value >= 15_000_000) ||
            (width >= 3840 && fileSizeBytes >= 100 * 1024 * 1024))
        {
            return WallpaperMotionLevel.High;
        }

        // Low motion: small file size (<= 25 MB) or very low bitrate (<= 3.5 Mbps)
        if (fileSizeBytes <= 25 * 1024 * 1024 ||
            (bitrate.HasValue && bitrate.Value <= 3_500_000))
        {
            return WallpaperMotionLevel.Low;
        }

        // Default moderate motion
        return WallpaperMotionLevel.Medium;
    }
}
