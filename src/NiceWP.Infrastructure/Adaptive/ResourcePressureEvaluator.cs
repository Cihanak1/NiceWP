namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Evaluates instantaneous system load against established high-pressure and recovery thresholds.
/// Used by Resource Guard to detect severe CPU contention without reacting to normal transient bursts.
/// </summary>
public sealed class ResourcePressureEvaluator
{
    public const double DefaultHighCpuThresholdPercent = 80.0;
    public const double DefaultReleaseCpuThresholdPercent = 50.0;

    public double HighPressureCpuThresholdPercent { get; }
    public double ReleasePressureCpuThresholdPercent { get; }

    public ResourcePressureEvaluator(
        double highCpuThreshold = DefaultHighCpuThresholdPercent,
        double releaseCpuThreshold = DefaultReleaseCpuThresholdPercent)
    {
        if (highCpuThreshold <= releaseCpuThreshold)
        {
            throw new ArgumentException("High pressure threshold must be strictly greater than release threshold.");
        }

        HighPressureCpuThresholdPercent = highCpuThreshold;
        ReleasePressureCpuThresholdPercent = releaseCpuThreshold;
    }

    /// <summary>
    /// Checks whether the instantaneous CPU sample exceeds the high pressure threshold.
    /// </summary>
    public bool IsInstantaneousPressureExceeded(double currentCpuPercent) =>
        currentCpuPercent >= HighPressureCpuThresholdPercent;

    /// <summary>
    /// Checks whether the instantaneous CPU sample has dropped below the recovery threshold.
    /// </summary>
    public bool IsInstantaneousReleaseAchieved(double currentCpuPercent) =>
        currentCpuPercent <= ReleasePressureCpuThresholdPercent;
}
