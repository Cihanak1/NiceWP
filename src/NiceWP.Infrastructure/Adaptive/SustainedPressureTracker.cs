namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Filters transient CPU bursts by requiring contention to remain continuously elevated
/// across a designated time window (default 15 seconds) before confirming sustained resource pressure.
/// </summary>
public sealed class SustainedPressureTracker
{
    public static readonly TimeSpan DefaultSustainedWindow = TimeSpan.FromSeconds(15);

    private readonly ResourcePressureEvaluator evaluator;
    public TimeSpan SustainedWindow { get; }
    public DateTimeOffset? PressureStartedUtc { get; private set; }
    public bool IsPressureSustained { get; private set; }

    public SustainedPressureTracker(
        ResourcePressureEvaluator? evaluator = null,
        TimeSpan? sustainedWindow = null)
    {
        this.evaluator = evaluator ?? new ResourcePressureEvaluator();
        SustainedWindow = sustainedWindow ?? DefaultSustainedWindow;
    }

    /// <summary>
    /// Ingests a new CPU utilization sample.
    /// Returns true when sustained pressure is confirmed.
    /// </summary>
    public bool RecordSample(double currentCpuPercent, DateTimeOffset timestampUtc)
    {
        if (evaluator.IsInstantaneousPressureExceeded(currentCpuPercent))
        {
            PressureStartedUtc ??= timestampUtc;
            if (timestampUtc - PressureStartedUtc.Value >= SustainedWindow)
            {
                IsPressureSustained = true;
            }
        }
        else if (!IsPressureSustained)
        {
            // Drop unconfirmed candidate start timestamp
            PressureStartedUtc = null;
        }

        return IsPressureSustained;
    }

    /// <summary>
    /// Resets tracking state unconditionally.
    /// </summary>
    public void Reset()
    {
        PressureStartedUtc = null;
        IsPressureSustained = false;
    }
}
