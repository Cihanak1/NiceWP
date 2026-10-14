namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Abstraction for capturing process-specific GPU utilization and memory metrics.
/// </summary>
public interface IGpuMetricsProvider
{
    bool IsAvailable { get; }
    string StatusReason { get; }
    double? GetGpuUsagePercent();
    long? GetGpuMemoryBytes();
}

/// <summary>
/// Default provider documenting that per-process GPU metrics are intentionally unavailable
/// due to severe performance overhead (PDH engine enumeration taking >2 seconds per query).
/// </summary>
public sealed class UnavailableGpuMetricsProvider : IGpuMetricsProvider
{
    public static readonly UnavailableGpuMetricsProvider Instance = new();

    public bool IsAvailable => false;

    public string StatusReason =>
        "Unavailable: Windows process-level GPU counters require full D3D engine enumeration across all processes, adding 2+ seconds overhead per sample.";

    public double? GetGpuUsagePercent() => null;

    public long? GetGpuMemoryBytes() => null;
}
