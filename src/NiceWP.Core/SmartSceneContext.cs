namespace NiceWP.Core;

/// <summary>
/// Captures the contextual environment under which Smart Scene scoring and wallpaper selection occurs.
/// </summary>
public sealed record SmartSceneContext(
    SmartSceneMode EffectiveScene,
    AdaptiveDesktopProfile AdaptiveProfile = AdaptiveDesktopProfile.Normal,
    bool IsOnBattery = false,
    TimeSpan? TimeOfDay = null,
    IReadOnlyList<string>? RecentWallpaperIds = null,
    bool PreferFavorites = true,
    bool AvoidRecent = true);
