using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Deterministic central resolver for desktop intelligence.
/// Consumes external context, power status, foreground process, and policies to produce
/// an immutable <see cref="EffectiveDesktopState"/>.
/// </summary>
public static class AdaptiveDesktopStateResolver
{
    /// <summary>
    /// Resolves the effective operational desktop state deterministically.
    /// Does not mutate any external state or maintain hidden hysteresis.
    /// </summary>
    public static EffectiveDesktopState Resolve(AdaptiveStateResolverInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Resolve Effective Resource Budget
        var budget = ResolveResourceBudget(input.ConfiguredBudget, input.IsOnBattery, input.IsBatterySaverActive, input.IsResourcePressureSustained);

        // 2. Resolve Profile & Primary Driver
        // If a profile other than Automatic was explicitly configured by the user, it governs baseline expectations.
        var targetProfile = input.ConfiguredProfile;

        // If Automatic, evaluate matched app rule or foreground context
        if (targetProfile == AdaptiveDesktopProfile.Automatic)
        {
            if (input.MatchedAppRuleProfile.HasValue && input.MatchedAppRuleProfile.Value != AdaptiveDesktopProfile.Automatic)
            {
                targetProfile = input.MatchedAppRuleProfile.Value;
            }
            else if (input.IsBatterySaverActive)
            {
                targetProfile = AdaptiveDesktopProfile.BatterySaver;
            }
            else if (input.IsFullscreenActive)
            {
                targetProfile = AdaptiveDesktopProfile.Gaming;
            }
            else
            {
                targetProfile = AdaptiveDesktopProfile.Normal;
            }
        }

        var isVideo = string.Equals(input.CurrentWallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

        return new EffectiveDesktopState
        {
            EffectiveProfile = targetProfile,
            ResourceBudget = budget,
            AllowRotation = true,
            AllowScheduleApply = true,
            ShouldPause = false,
            ShouldDeepFreeze = false,
            ActiveProcessName = input.ForegroundProcessName,
            Reason = "Operating normally"
        };
    }

    private static ResourceBudget ResolveResourceBudget(
        ResourceBudget configured,
        bool isOnBattery,
        bool isBatterySaver,
        bool isPressureSustained)
    {
        if (configured != ResourceBudget.Automatic)
        {
            return configured;
        }

        if (isBatterySaver || isPressureSustained)
        {
            return ResourceBudget.Low;
        }

        if (isOnBattery)
        {
            return ResourceBudget.Balanced;
        }

        return ResourceBudget.Balanced;
    }
}
