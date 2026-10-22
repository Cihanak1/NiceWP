namespace NiceWP.Core;

/// <summary>
/// Rendering fidelity and shader/wave complexity for interactive wallpaper transitions.
/// </summary>
public enum TransitionQuality
{
    Automatic,
    Low,
    Balanced,
    High
}

public static class TransitionQualityExtensions
{
    public const string QualityAutomatic = "Automatic";
    public const string QualityLow = "Low";
    public const string QualityBalanced = "Balanced";
    public const string QualityHigh = "High";

    public static string ToConfigString(this TransitionQuality quality) => quality switch
    {
        TransitionQuality.Low => QualityLow,
        TransitionQuality.Balanced => QualityBalanced,
        TransitionQuality.High => QualityHigh,
        _ => QualityAutomatic
    };

    public static TransitionQuality ParseQuality(string? value)
    {
        if (Enum.TryParse<TransitionQuality>(value?.Trim(), true, out var parsed))
        {
            return parsed;
        }

        return TransitionQuality.Automatic;
    }

    /// <summary>
    /// Resolves the effective rendering quality against the active resource budget when set to Automatic.
    /// </summary>
    public static TransitionQuality ResolveEffective(this TransitionQuality quality, ResourceBudget resourceBudget)
    {
        if (quality != TransitionQuality.Automatic)
        {
            return quality;
        }

        return resourceBudget switch
        {
            ResourceBudget.Low => TransitionQuality.Low,
            ResourceBudget.High => TransitionQuality.High,
            _ => TransitionQuality.Balanced
        };
    }
}
