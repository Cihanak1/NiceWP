namespace NiceWP.Core;

/// <summary>
/// Represents the operational health state of the NiceWP Runtime Host.
/// </summary>
public enum RuntimeHealthStatus
{
    /// <summary>
    /// Runtime Host is operating normally with all subsystems and desktop attachments intact.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Operating in a degraded state (e.g. desktop host WorkerW temporarily detached during Explorer restart, or partial subsystem failure).
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// Runtime was stopped automatically due to safety guardrails (e.g. repeated renderer startup failures tripped the circuit breaker).
    /// </summary>
    SafeStopped = 2
}

public static class RuntimeHealthStatusExtensions
{
    public static string ToDisplayString(this RuntimeHealthStatus status) => status switch
    {
        RuntimeHealthStatus.Healthy => "Healthy",
        RuntimeHealthStatus.Degraded => "Degraded",
        RuntimeHealthStatus.SafeStopped => "Safe Stopped",
        _ => "Healthy"
    };
}
