namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements dual-threshold hysteresis for Resource Guard to prevent rapid oscillation and CPU chattering.
/// To activate: contention must remain >= 80% CPU for 15 sustained seconds.
/// To release: load must drop and remain continuously &lt;= 50% CPU for 10 seconds.
/// </summary>
public sealed class ResourceGuardCoordinator
{
    public static readonly TimeSpan DefaultRecoveryWindow = TimeSpan.FromSeconds(10);

    private readonly ResourcePressureEvaluator evaluator;
    private readonly SustainedPressureTracker pressureTracker;

    public TimeSpan RecoveryWindow { get; }
    public DateTimeOffset? ReleaseStartedUtc { get; private set; }
    public bool IsActive { get; private set; }

    public ResourceGuardCoordinator(
        ResourcePressureEvaluator? evaluator = null,
        TimeSpan? sustainedWindow = null,
        TimeSpan? recoveryWindow = null)
    {
        this.evaluator = evaluator ?? new ResourcePressureEvaluator();
        pressureTracker = new SustainedPressureTracker(this.evaluator, sustainedWindow);
        RecoveryWindow = recoveryWindow ?? DefaultRecoveryWindow;
    }

    /// <summary>
    /// Processes a new CPU load sample and returns whether Resource Guard is actively engaged.
    /// </summary>
    public bool RecordSample(double currentCpuPercent, DateTimeOffset timestampUtc)
    {
        if (!IsActive)
        {
            if (pressureTracker.RecordSample(currentCpuPercent, timestampUtc))
            {
                IsActive = true;
                ReleaseStartedUtc = null;
            }
        }
        else
        {
            // Currently engaged; evaluate recovery hysteresis
            if (evaluator.IsInstantaneousReleaseAchieved(currentCpuPercent))
            {
                ReleaseStartedUtc ??= timestampUtc;
                if (timestampUtc - ReleaseStartedUtc.Value >= RecoveryWindow)
                {
                    IsActive = false;
                    ReleaseStartedUtc = null;
                    pressureTracker.Reset();
                }
            }
            else
            {
                // Load climbed above recovery threshold before completion; reset recovery timer
                ReleaseStartedUtc = null;
            }
        }

        return IsActive;
    }

    /// <summary>
    /// Forces immediate reset of Resource Guard state.
    /// </summary>
    public void Reset()
    {
        IsActive = false;
        ReleaseStartedUtc = null;
        pressureTracker.Reset();
    }
}
