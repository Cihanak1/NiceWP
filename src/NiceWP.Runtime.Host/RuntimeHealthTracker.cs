using NiceWP.Core;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Observes runtime health, enforces safety guardrails, and manages the failure circuit breaker.
/// </summary>
public sealed class RuntimeHealthTracker
{
    public const int MaxConsecutiveFailuresBeforeTrip = 3;

    private readonly object syncRoot = new();
    private int consecutiveFailures;
    private bool isCircuitBreakerTripped;
    private string lastFailureReason = string.Empty;

    public bool IsCircuitBreakerTripped
    {
        get { lock (syncRoot) return isCircuitBreakerTripped; }
    }

    public int ConsecutiveFailures
    {
        get { lock (syncRoot) return consecutiveFailures; }
    }

    /// <summary>
    /// Evaluates current overall health based on subsystems and circuit breaker state.
    /// </summary>
    public (RuntimeHealthStatus Status, string Detail) EvaluateHealth(
        bool hasActiveWallpaper,
        bool desktopHostConnected,
        bool rendererWindowAlive)
    {
        lock (syncRoot)
        {
            if (isCircuitBreakerTripped)
            {
                return (RuntimeHealthStatus.SafeStopped,
                    $"Circuit breaker tripped: {consecutiveFailures} consecutive start failures. {lastFailureReason}".Trim());
            }

            if (hasActiveWallpaper)
            {
                if (!desktopHostConnected)
                {
                    return (RuntimeHealthStatus.Degraded, "Desktop host (WorkerW) detached or unavailable.");
                }

                if (!rendererWindowAlive)
                {
                    return (RuntimeHealthStatus.Degraded, "Renderer window handle is not alive.");
                }
            }

            return (RuntimeHealthStatus.Healthy, "Operating normally.");
        }
    }

    /// <summary>
    /// Computes a concise human-readable current efficiency action string for the UI.
    /// </summary>
    public string ComputeCurrentAction(
        bool isCircuitBreakerTripped,
        bool hasActiveWallpaper,
        bool isPaused,
        bool isManualPaused,
        bool isFullscreenPaused,
        bool isPowerPaused,
        bool isSessionLockedPaused,
        bool isDisplayOffPaused,
        string wallpaperType)
    {
        if (isCircuitBreakerTripped)
        {
            return "Safe Stopped: Circuit breaker tripped";
        }

        if (!hasActiveWallpaper)
        {
            return "Idle: No active wallpaper";
        }

        if (isPaused)
        {
            var reasons = new List<string>(5);
            if (isManualPaused) reasons.Add("Manual");
            if (isFullscreenPaused) reasons.Add("Fullscreen");
            if (isPowerPaused) reasons.Add("Battery Saver");
            if (isSessionLockedPaused) reasons.Add("Session Locked");
            if (isDisplayOffPaused) reasons.Add("Display Off");

            return reasons.Count > 0
                ? $"Paused: {string.Join(", ", reasons)}"
                : "Paused";
        }

        return string.Equals(wallpaperType, "Video", StringComparison.OrdinalIgnoreCase)
            ? "Running normally: Video wallpaper playing"
            : "Running normally: Image wallpaper active";
    }

    /// <summary>
    /// Records a successful wallpaper start or restore, resetting consecutive failures.
    /// </summary>
    public void RecordSuccess()
    {
        lock (syncRoot)
        {
            consecutiveFailures = 0;
            isCircuitBreakerTripped = false;
            lastFailureReason = string.Empty;
        }
    }

    /// <summary>
    /// Records a failure to apply/start wallpaper. Trips circuit breaker if threshold is reached.
    /// Returns true if the circuit breaker was tripped by this failure.
    /// </summary>
    public bool RecordFailure(string reason)
    {
        lock (syncRoot)
        {
            consecutiveFailures++;
            lastFailureReason = reason;

            if (consecutiveFailures >= MaxConsecutiveFailuresBeforeTrip)
            {
                isCircuitBreakerTripped = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Resets the circuit breaker when the user explicitly triggers a new manual apply.
    /// </summary>
    public void ResetCircuitBreaker()
    {
        lock (syncRoot)
        {
            consecutiveFailures = 0;
            isCircuitBreakerTripped = false;
            lastFailureReason = string.Empty;
        }
    }
}
