using System.Diagnostics;

namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Lightweight, allocation-free process CPU utilization calculator.
/// Uses process CPU time delta divided by wall clock delta and logical processor count.
/// </summary>
public sealed class CpuUsageCalculator
{
    private readonly Process process;
    private readonly int processorCount;
    private readonly Func<long> timestampProvider;
    private TimeSpan lastCpuTime;
    private long lastTimestamp;
    private bool hasInitialSample;

    public int ProcessorCount => processorCount;

    public CpuUsageCalculator(Process? process = null, int? logicalProcessorCount = null, Func<long>? timestampProvider = null)
    {
        this.process = process ?? Process.GetCurrentProcess();
        this.processorCount = logicalProcessorCount.GetValueOrDefault(Environment.ProcessorCount);
        if (this.processorCount <= 0)
        {
            this.processorCount = 1;
        }
        this.timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
    }

    /// <summary>
    /// Calculates the CPU utilization percentage since the previous call.
    /// Returns null on the initial sample (as required by specification).
    /// </summary>
    public double? CalculateCpuUsage()
    {
        try
        {
            process.Refresh();
            var currentCpuTime = process.TotalProcessorTime;
            var currentTimestamp = timestampProvider();

            if (!hasInitialSample)
            {
                lastCpuTime = currentCpuTime;
                lastTimestamp = currentTimestamp;
                hasInitialSample = true;
                return null;
            }

            var deltaTimestamp = currentTimestamp - lastTimestamp;
            var deltaCpuTime = currentCpuTime - lastCpuTime;

            lastCpuTime = currentCpuTime;
            lastTimestamp = currentTimestamp;

            if (deltaTimestamp <= 0)
            {
                return 0.0;
            }

            var wallClockSeconds = (double)deltaTimestamp / Stopwatch.Frequency;
            var cpuSeconds = deltaCpuTime.TotalSeconds;

            if (wallClockSeconds <= 0 || cpuSeconds < 0)
            {
                return 0.0;
            }

            var usage = (cpuSeconds / (wallClockSeconds * processorCount)) * 100.0;
            if (double.IsNaN(usage) || double.IsInfinity(usage) || usage < 0)
            {
                return 0.0;
            }

            return Math.Round(Math.Clamp(usage, 0.0, 100.0), 2);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Pure mathematical calculation for testing delta calculations with simulated inputs.
    /// </summary>
    public static double? CalculateNormalizedPercent(
        TimeSpan deltaCpuTime,
        TimeSpan deltaWallClock,
        int processorCount)
    {
        if (processorCount <= 0)
        {
            processorCount = 1;
        }

        if (deltaWallClock <= TimeSpan.Zero || deltaCpuTime < TimeSpan.Zero)
        {
            return 0.0;
        }

        var wallSeconds = deltaWallClock.TotalSeconds;
        var cpuSeconds = deltaCpuTime.TotalSeconds;

        var usage = (cpuSeconds / (wallSeconds * processorCount)) * 100.0;
        return Math.Round(Math.Clamp(usage, 0.0, 100.0), 2);
    }

    /// <summary>
    /// Resets the calculator so the next sample is treated as an initial baseline.
    /// </summary>
    public void Reset()
    {
        hasInitialSample = false;
        lastCpuTime = TimeSpan.Zero;
        lastTimestamp = 0;
    }
}
