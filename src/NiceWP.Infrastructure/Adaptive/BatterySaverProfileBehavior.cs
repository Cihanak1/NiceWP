using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements behavioral policies and energy conservation rules for Battery Saver Profile.
/// When Windows Battery Saver is active or when explicitly chosen, video playback enters Deep Freeze
/// immediately to release hardware decoders and halt timer ticks, preserving maximum battery life.
/// </summary>
public static class BatterySaverProfileBehavior
{
    /// <summary>
    /// Determines whether Battery Saver rules mandate an immediate Deep Freeze for the given wallpaper type.
    /// </summary>
    public static bool ShouldImmediatelyDeepFreeze(bool isBatterySaverActive, string? wallpaperType)
    {
        if (!isBatterySaverActive) return false;
        return string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves recommended timer interval in milliseconds for background ticks under battery conditions.
    /// </summary>
    public static int GetRecommendedTimerIntervalMs(bool isBatterySaverActive, bool isOnBattery)
    {
        if (isBatterySaverActive)
        {
            return 5000; // 5s low frequency tick
        }

        if (isOnBattery)
        {
            return 2000; // 2s balanced tick
        }

        return 1000; // 1s normal tick on AC power
    }
}
