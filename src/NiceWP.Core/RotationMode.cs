namespace NiceWP.Core;

/// <summary>
/// Specifies the candidate source mode for wallpaper rotation.
/// </summary>
public enum RotationMode
{
    /// <summary>
    /// Automatic wallpaper rotation is disabled.
    /// </summary>
    Off,

    /// <summary>
    /// Rotate across all non-missing wallpapers in the library.
    /// </summary>
    All,

    /// <summary>
    /// Rotate only across non-missing wallpapers marked as favorites.
    /// </summary>
    Favorites
}

/// <summary>
/// Extension methods for <see cref="RotationMode"/>.
/// </summary>
public static class RotationModeExtensions
{
    public const string ModeOff = "Off";
    public const string ModeAll = "All";
    public const string ModeFavorites = "Favorites";

    public static string ToDisplayString(this RotationMode mode) => mode switch
    {
        RotationMode.All => "All Library",
        RotationMode.Favorites => "Favorites",
        _ => "Off"
    };

    public static string ToConfigString(this RotationMode mode) => mode switch
    {
        RotationMode.All => ModeAll,
        RotationMode.Favorites => ModeFavorites,
        _ => ModeOff
    };

    public static RotationMode ParseMode(string? value)
    {
        if (string.Equals(value?.Trim(), ModeAll, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value?.Trim(), "All Library", StringComparison.OrdinalIgnoreCase))
        {
            return RotationMode.All;
        }

        if (string.Equals(value?.Trim(), ModeFavorites, StringComparison.OrdinalIgnoreCase))
        {
            return RotationMode.Favorites;
        }

        return RotationMode.Off;
    }
}
