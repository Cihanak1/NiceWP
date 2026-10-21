using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Deterministic central resolver for desktop intelligence.
/// Consumes external context, power status, foreground process, and policies to produce
/// an immutable <see cref="EffectiveDesktopState"/> based on strict priority hierarchy:
/// 1. DisplayOff
/// 2. SessionLocked
/// 3. Manual Pause
/// 4. Presentation
/// 5. Gaming
/// 6. Battery Saver
/// 7. Resource Guard
/// 8. Focus
/// 9. Cinema
/// 10. Schedule / Rotation
/// 11. Normal
/// </summary>
public static class AdaptiveDesktopStateResolver
{
    public static EffectiveDesktopState Resolve(AdaptiveStateResolverInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var budget = ResolveResourceBudget(
            input.ConfiguredBudget,
            input.IsOnBattery,
            input.IsBatterySaverActive,
            input.IsResourcePressureSustained);

        var isVideo = string.Equals(input.CurrentWallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

        // Priority 1: Display Off
        if (input.IsDisplayOff)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = ResolveEffectiveProfile(input),
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = "Display is turned off"
            };
        }

        // Priority 2: Session Locked
        if (input.IsSessionLocked)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = ResolveEffectiveProfile(input),
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = "Session is locked"
            };
        }

        // Priority 3: Manual Pause
        if (input.IsManualPauseActive)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = ResolveEffectiveProfile(input),
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = "Manual pause active"
            };
        }

        // Determine context-driven target profile
        var effectiveProfile = ResolveEffectiveProfile(input);

        // Priority 4: Presentation
        if (effectiveProfile == AdaptiveDesktopProfile.Presentation)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = AdaptiveDesktopProfile.Presentation,
                ResourceBudget = budget,
                ShouldPause = isVideo,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                PreferredWallpaperId = input.MatchedAppRuleWallpaperId ?? input.PresentationWallpaperId,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = input.MatchedAppRuleProfile == AdaptiveDesktopProfile.Presentation
                    ? $"Presentation rule active for {input.ForegroundProcessName}"
                    : "Presentation profile active"
            };
        }

        // Priority 5: Gaming
        if (effectiveProfile == AdaptiveDesktopProfile.Gaming)
        {
            var isRule = input.MatchedAppRuleProfile == AdaptiveDesktopProfile.Gaming;
            return new EffectiveDesktopState
            {
                EffectiveProfile = AdaptiveDesktopProfile.Gaming,
                ResourceBudget = budget,
                ShouldPause = isVideo,
                ShouldDeepFreeze = false, // Immediate pause; Deep Freeze follows grace period via policy
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = isRule
                    ? $"Gaming rule active for {input.ForegroundProcessName}"
                    : (input.IsFullscreenActive ? "Fullscreen application active" : "Gaming profile active")
            };
        }

        // Priority 6: Battery Saver
        if (effectiveProfile == AdaptiveDesktopProfile.BatterySaver || input.IsBatterySaverActive)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = AdaptiveDesktopProfile.BatterySaver,
                ResourceBudget = budget,
                ShouldPause = isVideo,
                ShouldDeepFreeze = isVideo,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = "Battery Saver active"
            };
        }

        // Priority 7: Resource Guard
        if (input.IsResourcePressureSustained && isVideo && budget != ResourceBudget.High)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = effectiveProfile,
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = true,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = "Resource Guard active due to sustained system pressure"
            };
        }

        // Priority 8: Focus
        if (effectiveProfile == AdaptiveDesktopProfile.Focus)
        {
            var isRule = input.MatchedAppRuleProfile == AdaptiveDesktopProfile.Focus;
            return new EffectiveDesktopState
            {
                EffectiveProfile = AdaptiveDesktopProfile.Focus,
                ResourceBudget = budget,
                ShouldPause = isVideo,
                ShouldDeepFreeze = false,
                AllowRotation = false, // Postpone automated rotation
                AllowScheduleApply = true,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = isRule
                    ? $"Focus rule active for {input.ForegroundProcessName}"
                    : "Focus profile active"
            };
        }

        // Priority 9: Cinema
        if (effectiveProfile == AdaptiveDesktopProfile.Cinema)
        {
            var isRule = input.MatchedAppRuleProfile == AdaptiveDesktopProfile.Cinema;
            return new EffectiveDesktopState
            {
                EffectiveProfile = AdaptiveDesktopProfile.Cinema,
                ResourceBudget = budget,
                ShouldPause = isVideo,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = isRule
                    ? $"Cinema rule active for {input.ForegroundProcessName}"
                    : "Cinema profile active"
            };
        }

        // Priority 10: App Rule direct actions (Pause or DeepFreeze)
        if (input.MatchedAppRuleDeepFreeze && isVideo)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = effectiveProfile,
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = true,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = $"Deep freeze rule active for {input.ForegroundProcessName}"
            };
        }

        if (input.MatchedAppRulePause && isVideo)
        {
            return new EffectiveDesktopState
            {
                EffectiveProfile = effectiveProfile,
                ResourceBudget = budget,
                ShouldPause = true,
                ShouldDeepFreeze = false,
                AllowRotation = false,
                AllowScheduleApply = false,
                ActiveProcessName = input.ForegroundProcessName,
                Reason = $"Pause rule active for {input.ForegroundProcessName}"
            };
        }

        // Priority 11: Normal
        return new EffectiveDesktopState
        {
            EffectiveProfile = AdaptiveDesktopProfile.Normal,
            ResourceBudget = budget,
            ShouldPause = false,
            ShouldDeepFreeze = false,
            AllowRotation = true,
            AllowScheduleApply = true,
            PreferredWallpaperId = input.MatchedAppRuleWallpaperId,
            ActiveProcessName = input.ForegroundProcessName,
            Reason = "Operating normally"
        };
    }

    private static AdaptiveDesktopProfile ResolveEffectiveProfile(AdaptiveStateResolverInput input)
    {
        // Explicit manual selection takes precedence over automatic heuristics
        if (input.ConfiguredProfile != AdaptiveDesktopProfile.Automatic)
        {
            return input.ConfiguredProfile;
        }

        // Under Automatic mode, App Rules take first precedence
        if (input.MatchedAppRuleProfile.HasValue && input.MatchedAppRuleProfile.Value != AdaptiveDesktopProfile.Automatic)
        {
            return input.MatchedAppRuleProfile.Value;
        }

        // Then Battery Saver
        if (input.IsBatterySaverActive)
        {
            return AdaptiveDesktopProfile.BatterySaver;
        }

        // Then Fullscreen -> Gaming
        if (input.IsFullscreenActive)
        {
            return AdaptiveDesktopProfile.Gaming;
        }

        return AdaptiveDesktopProfile.Normal;
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
