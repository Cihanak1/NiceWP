namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Combined status payload containing the latest snapshot and rolling short-term statistics.
/// </summary>
public sealed class RuntimePerformanceStatus
{
    public RuntimePerformanceSnapshot Snapshot { get; set; } = new();
    public RuntimePerformanceStats Stats { get; set; } = new();
}
