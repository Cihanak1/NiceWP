using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Classifies wallpapers into Light, Moderate, or Heavy resource tiers based on media type,
/// frame resolution, and file dimensions.
/// </summary>
public static class WallpaperCostClassifier
{
    public const long HeavyFileSizeThresholdBytes = 200 * 1024 * 1024; // 200 MB

    /// <summary>
    /// Analyzes wallpaper parameters to determine its runtime resource tier.
    /// </summary>
    public static WallpaperCost Classify(
        string? wallpaperType,
        uint? width = null,
        uint? height = null,
        long? fileSizeBytes = null)
    {
        // Static images have virtually zero ongoing decoding overhead
        if (string.Equals(wallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperCost.Light;
        }

        // 4K UHD or QHD resolutions require substantial hardware decoder bandwidth
        if (width >= 2560 || height >= 1440)
        {
            return WallpaperCost.Heavy;
        }

        // Exceptionally large video files imply very high bitrate encoding
        if (fileSizeBytes.HasValue && fileSizeBytes.Value >= HeavyFileSizeThresholdBytes)
        {
            return WallpaperCost.Heavy;
        }

        // Standard 720p / 1080p video playback
        return WallpaperCost.Moderate;
    }
}
