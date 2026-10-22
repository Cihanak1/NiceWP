namespace NiceWP.Core;

/// <summary>
/// Categorizes the visual chromatic temperature of a wallpaper based on color balance analysis.
/// </summary>
public enum WallpaperColorTemperature
{
    Cool = 0,
    Neutral = 1,
    Warm = 2
}

public static class WallpaperColorTemperatureExtensions
{
    public const string TemperatureCool = "Cool";
    public const string TemperatureNeutral = "Neutral";
    public const string TemperatureWarm = "Warm";

    public static string ToConfigString(this WallpaperColorTemperature temp) => temp switch
    {
        WallpaperColorTemperature.Cool => TemperatureCool,
        WallpaperColorTemperature.Warm => TemperatureWarm,
        _ => TemperatureNeutral
    };

    public static WallpaperColorTemperature ParseTemperature(string? value)
    {
        if (string.Equals(value?.Trim(), TemperatureCool, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperColorTemperature.Cool;
        }

        if (string.Equals(value?.Trim(), TemperatureWarm, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperColorTemperature.Warm;
        }

        return WallpaperColorTemperature.Neutral;
    }
}
