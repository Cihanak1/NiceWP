namespace NiceWP.Core;

/// <summary>
/// Defines user-selected runtime efficiency profiles.
/// </summary>
public enum EfficiencyProfile
{
    /// <summary>
    /// Default profile. Pauses when wallpaper is not visible or useful.
    /// Balanced resource savings with seamless desktop experience.
    /// </summary>
    Balanced = 0,

    /// <summary>
    /// Aggressive power and CPU savings. Minimum watchdog wake-ups and immediate background throttling.
    /// </summary>
    Eco = 1,

    /// <summary>
    /// Keeps wallpaper playing continuously wherever practical (bypasses fullscreen and battery pauses).
    /// Still pauses when workstation is locked or monitor is powered off to prevent wasted hardware load.
    /// </summary>
    Unrestricted = 2
}

/// <summary>
/// Centralized evaluation matrix separating user efficiency profiles from physical runtime conditions.
/// </summary>
public static class EfficiencyPolicyMatrix
{
    public static bool ShouldPauseForFullscreen(EfficiencyProfile profile, bool isFullscreen)
    {
        if (!isFullscreen) return false;
        return profile switch
        {
            EfficiencyProfile.Eco => true,
            EfficiencyProfile.Balanced => true,
            EfficiencyProfile.Unrestricted => false,
            _ => true
        };
    }

    public static bool ShouldPauseForPower(EfficiencyProfile profile, in PowerStatus powerStatus)
    {
        if (profile == EfficiencyProfile.Unrestricted)
        {
            return false;
        }

        if (profile == EfficiencyProfile.Eco)
        {
            return !powerStatus.IsAcPowerConnected || powerStatus.ShouldPauseWallpaper();
        }

        return powerStatus.ShouldPauseWallpaper();
    }

    public static bool ShouldPauseForSessionLock(EfficiencyProfile profile, bool isLocked)
    {
        // All profiles pause when the workstation is locked: the desktop surface is invisible
        // behind the secure Winlogon station, so continued rendering has 0 visual benefit and wastes energy.
        return isLocked;
    }

    public static bool ShouldPauseForDisplayOff(EfficiencyProfile profile, bool isDisplayOff)
    {
        // All profiles pause when the monitor is physically asleep / powered down.
        return isDisplayOff;
    }

    public static EfficiencyProfile ParseProfile(string? value, EfficiencyProfile fallback = EfficiencyProfile.Balanced)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<EfficiencyProfile>(value.Trim(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(typeof(EfficiencyProfile), parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static string ToDisplayString(this EfficiencyProfile profile) => profile switch
    {
        EfficiencyProfile.Eco => "Eco",
        EfficiencyProfile.Balanced => "Balanced",
        EfficiencyProfile.Unrestricted => "Unrestricted",
        _ => "Balanced"
    };

    public static string GetDescription(this EfficiencyProfile profile) => profile switch
    {
        EfficiencyProfile.Eco => "Maximum background savings. Aggressive throttling when inactive.",
        EfficiencyProfile.Balanced => "Recommended. Pauses automatically when wallpaper is not useful.",
        EfficiencyProfile.Unrestricted => "Keeps wallpaper running whenever possible (ignores fullscreen & battery limits).",
        _ => "Recommended. Pauses automatically when wallpaper is not useful."
    };
}
