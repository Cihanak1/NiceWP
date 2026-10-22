using NiceWP.Core;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Enforces adaptive state gating for interactive corner transitions, ensuring page turn gestures
/// are strictly inhibited during Gaming, Presentation, Deep Freeze, Manual Pause, and Display Off/Lock states.
/// </summary>
public static class AdaptiveTransitionGate
{
    /// <summary>
    /// Checks whether an interactive corner gesture is permitted under current adaptive conditions.
    /// </summary>
    public static bool IsTransitionPermitted(
        EffectiveDesktopState? state,
        bool isManualPaused = false,
        bool isDisplayOffOrLocked = false)
    {
        if (isDisplayOffOrLocked)
        {
            return false;
        }

        if (isManualPaused)
        {
            return false;
        }

        if (state != null)
        {
            if (state.EffectiveProfile == AdaptiveDesktopProfile.Gaming)
            {
                return false;
            }

            if (state.EffectiveProfile == AdaptiveDesktopProfile.Presentation)
            {
                return false;
            }

            if (state.ShouldDeepFreeze)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the inhibition reason key, or null if transitions are permitted.
    /// </summary>
    public static string? GetInhibitionReason(
        EffectiveDesktopState? state,
        bool isManualPaused = false,
        bool isDisplayOffOrLocked = false)
    {
        if (isDisplayOffOrLocked) return "DisplayOffOrLocked";
        if (isManualPaused) return "ManualPaused";
        if (state == null) return null;
        if (state.EffectiveProfile == AdaptiveDesktopProfile.Gaming) return "GamingActive";
        if (state.EffectiveProfile == AdaptiveDesktopProfile.Presentation) return "PresentationActive";
        if (state.ShouldDeepFreeze) return "DeepFreezeActive";
        return null;
    }
}
