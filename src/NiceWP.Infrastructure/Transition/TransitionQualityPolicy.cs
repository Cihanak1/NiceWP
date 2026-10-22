using NiceWP.Core;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Evaluates visual quality parameters, wave scaling, and animation frame timing
/// based on <see cref="TransitionQuality"/> and active <see cref="ResourceBudget"/>.
/// </summary>
public static class TransitionQualityPolicy
{
    /// <summary>
    /// Resolves the effective quality from configured setting and active resource budget.
    /// </summary>
    public static TransitionQuality ResolveQuality(TransitionQuality configured, ResourceBudget budget) =>
        configured.ResolveEffective(budget);

    /// <summary>
    /// Determines whether paper wave ripple is permissible.
    /// Always returns false for Low quality or when user setting is disabled.
    /// </summary>
    public static bool IsWavePermitted(TransitionQuality quality, bool userSettingEnabled)
    {
        if (!userSettingEnabled) return false;
        return quality != TransitionQuality.Low;
    }

    /// <summary>
    /// Returns the wave amplitude multiplier for the given quality tier.
    /// </summary>
    public static double GetWaveScale(TransitionQuality quality) => quality switch
    {
        TransitionQuality.High => 1.0,
        TransitionQuality.Balanced => 0.65,
        TransitionQuality.Low or _ => 0.0
    };

    /// <summary>
    /// Returns maximum shadow opacity for the given quality tier.
    /// </summary>
    public static double GetMaxShadowOpacity(TransitionQuality quality) => quality switch
    {
        TransitionQuality.High => 0.65,
        TransitionQuality.Balanced => 0.50,
        TransitionQuality.Low or _ => 0.30
    };

    /// <summary>
    /// Returns the recommended animation tick delay in milliseconds for the given quality tier.
    /// </summary>
    public static int GetAnimationStepIntervalMs(TransitionQuality quality) => quality switch
    {
        TransitionQuality.Low => 25, // ~40 fps step to conserve CPU on constrained devices
        TransitionQuality.Balanced => 16, // ~60 fps
        TransitionQuality.High or _ => 16
    };
}
