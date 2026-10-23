namespace NiceWP.Core.Personalization;

/// <summary>
/// Record storing explicit and implicit user interaction signals for a single wallpaper item.
/// </summary>
public sealed class WallpaperPreferenceRecord
{
    public string WallpaperId { get; set; } = string.Empty;

    public WallpaperPreferenceLevel Level { get; set; } = WallpaperPreferenceLevel.Neutral;

    /// <summary>
    /// Continuous preference score [-1.0 .. +1.0] used for scoring contribution.
    /// </summary>
    public double Weight { get; set; }

    public DateTimeOffset LastInteractedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int ManualApplyCount { get; set; }

    public bool IsExcluded { get; set; }

    public static WallpaperPreferenceRecord Create(string wallpaperId, double initialWeight = 0.0) => new()
    {
        WallpaperId = wallpaperId,
        Weight = Math.Clamp(initialWeight, -1.0, 1.0),
        Level = ResolveLevel(initialWeight),
        LastInteractedAtUtc = DateTimeOffset.UtcNow
    };

    public static WallpaperPreferenceLevel ResolveLevel(double weight) => weight switch
    {
        >= 0.60 => WallpaperPreferenceLevel.StrongPositive,
        >= 0.15 => WallpaperPreferenceLevel.Positive,
        <= -0.60 => WallpaperPreferenceLevel.StrongNegative,
        <= -0.15 => WallpaperPreferenceLevel.Negative,
        _ => WallpaperPreferenceLevel.Neutral
    };
}
