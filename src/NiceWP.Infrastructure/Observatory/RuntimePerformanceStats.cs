namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Short-term rolling statistics calculated over a sliding window (e.g., 60 seconds).
/// </summary>
public sealed class RuntimePerformanceStats
{
    public int SampleCount { get; init; }
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromSeconds(60);

    // CPU Metrics (%)
    public double? CurrentCpuPercent { get; init; }
    public double? AverageCpuPercent { get; init; }
    public double? PeakCpuPercent { get; init; }

    // Working Set Metrics (bytes)
    public long CurrentWorkingSetBytes { get; init; }
    public long AverageWorkingSetBytes { get; init; }
    public long PeakWorkingSetBytes { get; init; }

    // Private Memory Metrics (bytes)
    public long CurrentPrivateMemoryBytes { get; init; }
    public long AveragePrivateMemoryBytes { get; init; }
    public long PeakPrivateMemoryBytes { get; init; }

    // Thread Counts
    public int CurrentThreadCount { get; init; }
    public int PeakThreadCount { get; init; }

    // Handle Counts
    public int CurrentHandleCount { get; init; }
    public int PeakHandleCount { get; init; }
}
