namespace NiceWP.Core;

/// <summary>
/// Categorizes the projected hardware resource footprint (CPU, GPU decode, RAM, and battery drain)
/// required to render a wallpaper.
/// </summary>
public enum WallpaperCost
{
    /// <summary>
    /// Near-zero runtime cost. Static images rendered once to DirectX surface with 0% ongoing CPU/GPU decode overhead.
    /// </summary>
    Light = 0,

    /// <summary>
    /// Moderate cost. Standard HD/1080p video playback at standard bitrates with low decoder pressure.
    /// </summary>
    Moderate = 1,

    /// <summary>
    /// Heavy cost. High-resolution (4K/QHD) or high-framerate/bitrate video playback requiring substantial decoder bandwidth.
    /// </summary>
    Heavy = 2
}

public static class WallpaperCostExtensions
{
    public const string CostLight = "Light";
    public const string CostModerate = "Moderate";
    public const string CostHeavy = "Heavy";

    public static string ToDisplayString(this WallpaperCost cost) => cost switch
    {
        WallpaperCost.Light => CostLight,
        WallpaperCost.Moderate => CostModerate,
        WallpaperCost.Heavy => CostHeavy,
        _ => CostModerate
    };
}
