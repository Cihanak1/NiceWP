namespace NiceWP.Core;

/// <summary>
/// Drag distance threshold and velocity sensitivity for interactive corner gesture.
/// </summary>
public enum TransitionSensitivity
{
    Low,
    Normal,
    High
}

public static class TransitionSensitivityExtensions
{
    public const string SensitivityLow = "Low";
    public const string SensitivityNormal = "Normal";
    public const string SensitivityHigh = "High";

    public static string ToConfigString(this TransitionSensitivity sensitivity) => sensitivity switch
    {
        TransitionSensitivity.Low => SensitivityLow,
        TransitionSensitivity.High => SensitivityHigh,
        _ => SensitivityNormal
    };

    public static TransitionSensitivity ParseSensitivity(string? value)
    {
        if (Enum.TryParse<TransitionSensitivity>(value?.Trim(), true, out var parsed))
        {
            return parsed;
        }

        return TransitionSensitivity.Normal;
    }

    /// <summary>
    /// Gets the drag distance in pixels required to reach 1.0 progress.
    /// </summary>
    public static double GetDragDistanceThreshold(this TransitionSensitivity sensitivity) => sensitivity switch
    {
        TransitionSensitivity.Low => 500.0,
        TransitionSensitivity.High => 250.0,
        _ => 360.0
    };
}
