namespace NiceWP.Core;

/// <summary>
/// Estimates the dynamic motion complexity of a wallpaper.
/// </summary>
public enum WallpaperMotionLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public static class WallpaperMotionLevelExtensions
{
    public const string MotionNone = "None";
    public const string MotionLow = "Low";
    public const string MotionMedium = "Medium";
    public const string MotionHigh = "High";

    public static string ToConfigString(this WallpaperMotionLevel level) => level switch
    {
        WallpaperMotionLevel.None => MotionNone,
        WallpaperMotionLevel.Low => MotionLow,
        WallpaperMotionLevel.Medium => MotionMedium,
        WallpaperMotionLevel.High => MotionHigh,
        _ => MotionNone
    };

    public static WallpaperMotionLevel ParseMotionLevel(string? value)
    {
        if (string.Equals(value?.Trim(), MotionLow, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperMotionLevel.Low;
        }

        if (string.Equals(value?.Trim(), MotionMedium, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperMotionLevel.Medium;
        }

        if (string.Equals(value?.Trim(), MotionHigh, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperMotionLevel.High;
        }

        return WallpaperMotionLevel.None;
    }
}
