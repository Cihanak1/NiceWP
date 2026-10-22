using NiceWP.Core;

namespace NiceWP.Infrastructure.SmartScenes;

/// <summary>
/// Resolves the effective runtime Smart Scene mode by reconciling user preference overrides
/// with live adaptive desktop profiles, power state, and time of day.
/// </summary>
public static class SmartSceneContextResolver
{
    public static readonly TimeSpan DefaultNightStart = TimeSpan.FromHours(21); // 21:00
    public static readonly TimeSpan DefaultNightEnd = TimeSpan.FromHours(6);    // 06:00

    /// <summary>
    /// Computes the active scene mode. Manual selections override automatic context.
    /// In Automatic mode, resolves via active adaptive profile and battery state.
    /// </summary>
    public static SmartSceneMode ResolveEffectiveScene(
        SmartSceneMode configuredScene,
        AdaptiveDesktopProfile activeAdaptiveProfile,
        bool isOnBattery = false,
        TimeSpan? timeOfDay = null)
    {
        // Manual override (Focus, Gaming, Relax, Night, BatterySaver, or Off)
        if (configuredScene != SmartSceneMode.Automatic)
        {
            return configuredScene;
        }

        // Battery saver takes highest automatic precedence
        if (isOnBattery || activeAdaptiveProfile == AdaptiveDesktopProfile.BatterySaver)
        {
            return SmartSceneMode.BatterySaver;
        }

        return activeAdaptiveProfile switch
        {
            AdaptiveDesktopProfile.Focus or AdaptiveDesktopProfile.Presentation => SmartSceneMode.Focus,
            AdaptiveDesktopProfile.Gaming => SmartSceneMode.Gaming,
            AdaptiveDesktopProfile.Cinema => SmartSceneMode.Relax,
            _ => ResolveFromTimeOfDay(timeOfDay ?? DateTime.Now.TimeOfDay)
        };
    }

    private static SmartSceneMode ResolveFromTimeOfDay(TimeSpan time)
    {
        if (time >= DefaultNightStart || time < DefaultNightEnd)
        {
            return SmartSceneMode.Night;
        }

        return SmartSceneMode.Focus;
    }
}
