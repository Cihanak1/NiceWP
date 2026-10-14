namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Fixed-capacity circular buffer storing recent performance snapshots.
/// Memory-bounded with O(1) insertion, overwriting oldest samples when capacity is reached.
/// </summary>
public sealed class PerformanceRingBuffer
{
    private readonly RuntimePerformanceSnapshot[] buffer;
    private readonly object syncRoot = new();
    private int head;
    private int tail;
    private int count;

    public int Capacity { get; }
    public int Count
    {
        get
        {
            lock (syncRoot)
            {
                return count;
            }
        }
    }

    public PerformanceRingBuffer(int capacity = 60)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        Capacity = capacity;
        buffer = new RuntimePerformanceSnapshot[capacity];
    }

    /// <summary>
    /// Adds a snapshot into the ring buffer. If full, overwrites the oldest sample.
    /// </summary>
    public void Add(RuntimePerformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (syncRoot)
        {
            buffer[head] = snapshot;
            head = (head + 1) % Capacity;

            if (count < Capacity)
            {
                count++;
            }
            else
            {
                tail = (tail + 1) % Capacity;
            }
        }
    }

    /// <summary>
    /// Returns all snapshots currently in the buffer in chronological order (oldest to newest).
    /// </summary>
    public RuntimePerformanceSnapshot[] GetSamples()
    {
        lock (syncRoot)
        {
            if (count == 0)
            {
                return [];
            }

            var result = new RuntimePerformanceSnapshot[count];
            for (var i = 0; i < count; i++)
            {
                var index = (tail + i) % Capacity;
                result[i] = buffer[index];
            }

            return result;
        }
    }

    /// <summary>
    /// Computes aggregated statistics across all samples currently in the buffer.
    /// </summary>
    public RuntimePerformanceStats CalculateStats(RuntimePerformanceSnapshot? latestSnapshot = null)
    {
        lock (syncRoot)
        {
            var samples = GetSamples();
            if (samples.Length == 0)
            {
                if (latestSnapshot != null)
                {
                    return new RuntimePerformanceStats
                    {
                        SampleCount = 1,
                        CurrentCpuPercent = latestSnapshot.CpuPercent,
                        AverageCpuPercent = latestSnapshot.CpuPercent,
                        PeakCpuPercent = latestSnapshot.CpuPercent,
                        CurrentWorkingSetBytes = latestSnapshot.WorkingSetBytes,
                        AverageWorkingSetBytes = latestSnapshot.WorkingSetBytes,
                        PeakWorkingSetBytes = latestSnapshot.WorkingSetBytes,
                        CurrentPrivateMemoryBytes = latestSnapshot.PrivateMemoryBytes,
                        AveragePrivateMemoryBytes = latestSnapshot.PrivateMemoryBytes,
                        PeakPrivateMemoryBytes = latestSnapshot.PrivateMemoryBytes,
                        CurrentThreadCount = latestSnapshot.ThreadCount,
                        PeakThreadCount = latestSnapshot.ThreadCount,
                        CurrentHandleCount = latestSnapshot.HandleCount,
                        PeakHandleCount = latestSnapshot.HandleCount
                    };
                }

                return new RuntimePerformanceStats { SampleCount = 0 };
            }

            var latest = latestSnapshot ?? samples[^1];

            double cpuSum = 0;
            var cpuCount = 0;
            double? cpuPeak = null;

            long wsSum = 0;
            long wsPeak = 0;

            long privSum = 0;
            long privPeak = 0;

            var threadPeak = 0;
            var handlePeak = 0;

            foreach (var s in samples)
            {
                if (s.CpuPercent.HasValue)
                {
                    var cpuVal = s.CpuPercent.Value;
                    cpuSum += cpuVal;
                    cpuCount++;
                    if (!cpuPeak.HasValue || cpuVal > cpuPeak.Value)
                    {
                        cpuPeak = cpuVal;
                    }
                }

                wsSum += s.WorkingSetBytes;
                if (s.WorkingSetBytes > wsPeak)
                {
                    wsPeak = s.WorkingSetBytes;
                }

                privSum += s.PrivateMemoryBytes;
                if (s.PrivateMemoryBytes > privPeak)
                {
                    privPeak = s.PrivateMemoryBytes;
                }

                if (s.ThreadCount > threadPeak)
                {
                    threadPeak = s.ThreadCount;
                }

                if (s.HandleCount > handlePeak)
                {
                    handlePeak = s.HandleCount;
                }
            }

            var sampleCount = samples.Length;
            return new RuntimePerformanceStats
            {
                SampleCount = sampleCount,
                CurrentCpuPercent = latest.CpuPercent,
                AverageCpuPercent = cpuCount > 0 ? Math.Round(cpuSum / cpuCount, 2) : null,
                PeakCpuPercent = cpuPeak.HasValue ? Math.Round(cpuPeak.Value, 2) : null,
                CurrentWorkingSetBytes = latest.WorkingSetBytes,
                AverageWorkingSetBytes = wsSum / sampleCount,
                PeakWorkingSetBytes = wsPeak,
                CurrentPrivateMemoryBytes = latest.PrivateMemoryBytes,
                AveragePrivateMemoryBytes = privSum / sampleCount,
                PeakPrivateMemoryBytes = privPeak,
                CurrentThreadCount = latest.ThreadCount,
                PeakThreadCount = threadPeak,
                CurrentHandleCount = latest.HandleCount,
                PeakHandleCount = handlePeak
            };
        }
    }

    /// <summary>
    /// Clears all samples in the buffer.
    /// </summary>
    public void Clear()
    {
        lock (syncRoot)
        {
            Array.Clear(buffer, 0, buffer.Length);
            head = 0;
            tail = 0;
            count = 0;
        }
    }
}
