using System.Diagnostics;
using NiceWP.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NiceWP.Infrastructure.Analysis;

/// <summary>
/// Provides lightweight, non-blocking image wallpaper analysis by downsampling to a small (64x64)
/// sample grid to extract brightness, chromatic temperature, dominant color, and geometry without UI thread stalls.
/// </summary>
public static class ImageWallpaperAnalyzer
{
    public const int SampleDimension = 64;

    /// <summary>
    /// Analyzes an image file on disk asynchronously.
    /// </summary>
    public static async Task<WallpaperAnalysisMetadata?> AnalyzeAsync(
        string wallpaperId,
        string filePath,
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

            await using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            using var randomAccessStream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken).ConfigureAwait(false);

            var origWidth = (int)decoder.PixelWidth;
            var origHeight = (int)decoder.PixelHeight;

            var transform = new BitmapTransform
            {
                ScaledWidth = SampleDimension,
                ScaledHeight = SampleDimension,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelDataProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            var pixels = pixelDataProvider.DetachPixelData();

            return ComputeFromRgbaBytes(
                wallpaperId: wallpaperId,
                sourceKey: filePath,
                originalWidth: origWidth,
                originalHeight: origHeight,
                rgbaPixels: pixels,
                fileSizeBytes: fileSizeBytes,
                fileModifiedUtc: fileModifiedUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ImageWallpaperAnalyzer] Failed to analyze '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes deterministic metadata from an RGBA byte buffer (e.g. 64x64 sample).
    /// </summary>
    public static WallpaperAnalysisMetadata ComputeFromRgbaBytes(
        string wallpaperId,
        string sourceKey,
        int originalWidth,
        int originalHeight,
        ReadOnlySpan<byte> rgbaPixels,
        long fileSizeBytes,
        DateTimeOffset fileModifiedUtc)
    {
        double totalBrightness = 0.0;
        long totalR = 0;
        long totalG = 0;
        long totalB = 0;
        int validPixelCount = 0;

        for (int i = 0; i + 3 < rgbaPixels.Length; i += 4)
        {
            byte r = rgbaPixels[i];
            byte g = rgbaPixels[i + 1];
            byte b = rgbaPixels[i + 2];
            byte a = rgbaPixels[i + 3];

            if (a < 32)
            {
                continue; // Skip transparent pixels
            }

            // Standard ITU-R BT.601 perceived luma formula
            double luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            totalBrightness += luma;
            totalR += r;
            totalG += g;
            totalB += b;
            validPixelCount++;
        }

        double brightness = validPixelCount > 0 ? Math.Clamp(totalBrightness / validPixelCount, 0.0, 1.0) : 0.5;
        int avgR = validPixelCount > 0 ? (int)(totalR / validPixelCount) : 0;
        int avgG = validPixelCount > 0 ? (int)(totalG / validPixelCount) : 0;
        int avgB = validPixelCount > 0 ? (int)(totalB / validPixelCount) : 0;

        string dominantHex = $"#{avgR:X2}{avgG:X2}{avgB:X2}";

        WallpaperColorTemperature temperature;
        if (avgR - avgB > 15)
        {
            temperature = WallpaperColorTemperature.Warm;
        }
        else if (avgB - avgR > 15)
        {
            temperature = WallpaperColorTemperature.Cool;
        }
        else
        {
            temperature = WallpaperColorTemperature.Neutral;
        }

        double aspectRatio = originalHeight > 0
            ? Math.Round((double)originalWidth / originalHeight, 3)
            : 1.0;

        bool isHighRes = originalWidth >= 2560 || originalHeight >= 1440;
        bool isDark = brightness < 0.40;
        bool isLight = brightness >= 0.60;

        return new WallpaperAnalysisMetadata
        {
            WallpaperId = wallpaperId,
            SourceKey = sourceKey,
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            MediaType = WallpaperMediaType.Image,
            Width = originalWidth,
            Height = originalHeight,
            AspectRatio = aspectRatio,
            Brightness = Math.Round(brightness, 3),
            DominantColorHex = dominantHex,
            ColorTemperature = temperature,
            MotionLevel = WallpaperMotionLevel.None,
            ResourceClass = WallpaperCost.Light,
            IsDark = isDark,
            IsLight = isLight,
            IsHighResolution = isHighRes,
            FileSizeBytes = fileSizeBytes,
            FileLastModifiedUtc = fileModifiedUtc
        };
    }
}
