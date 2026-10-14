using System.Diagnostics;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Observatory;

namespace NiceWP.Runtime.Host;

/// <summary>
/// State snapshot provider record used by RuntimePerformanceSampler to decouple from WinUI internals.
/// </summary>
public sealed record RuntimeHostStateSnapshot(
    string WallpaperType,
    string WallpaperState,
    string PauseReasons,
    string? ActiveWallpaperPath,
    bool DesktopHostConnected,
    bool RendererWindowAlive,
    uint? SourceWidth = null,
    uint? SourceHeight = null,
    uint? DecodeWidth = null,
    uint? DecodeHeight = null,
    long? ApproximateDecodedMemoryBytes = null,
    uint? VideoWidth = null,
    uint? VideoHeight = null,
    string? PlaybackStatus = null,
    TimeSpan? PlaybackPosition = null,
    TimeSpan? PlaybackDuration = null,
    bool FullscreenPauseActive = false,
    bool PowerPauseActive = false,
    bool ManualPauseActive = false)
{
    public static readonly RuntimeHostStateSnapshot Empty = new(
        IpcConstants.WallpaperTypeNone,
        IpcConstants.PlaybackStateNone,
        "None",
        null,
        false,
        false);
}

/// <summary>
/// Low-overhead performance sampler for NiceWP.Runtime.Host.
/// Captures process metrics (CPU delta, working set, private memory, threads, handles, uptime),
/// runtime state, and short-term 60-sample rolling statistics.
/// Strictly failure-isolated: any exception during sampling is caught and never affects the wallpaper runtime.
/// </summary>
public sealed class RuntimePerformanceSampler : IDisposable
{
    private readonly Process process;
    private readonly Func<RuntimeHostStateSnapshot> stateProvider;
    private readonly CpuUsageCalculator cpuCalculator;
    private readonly IGpuMetricsProvider gpuProvider;
    private readonly PerformanceRingBuffer ringBuffer;
    private readonly int sampleIntervalMs;
    private readonly DateTime startTimeUtc;
    private readonly object syncRoot = new();
    private CancellationTokenSource? cts;
    private Task? sampleLoopTask;
    private RuntimePerformanceSnapshot latestSnapshot;
    private bool disposed;

    public int SampleIntervalMs => sampleIntervalMs;
    public PerformanceRingBuffer RingBuffer => ringBuffer;
    public RuntimePerformanceSnapshot LatestSnapshot => latestSnapshot;
    public bool IsRunning => sampleLoopTask != null && !sampleLoopTask.IsCompleted;

    public RuntimePerformanceSampler(
        Process? process = null,
        Func<RuntimeHostStateSnapshot>? stateProvider = null,
        IGpuMetricsProvider? gpuProvider = null,
        int sampleIntervalMs = 1000,
        int ringBufferCapacity = 60)
    {
        this.process = process ?? Process.GetCurrentProcess();
        this.stateProvider = stateProvider ?? (() => RuntimeHostStateSnapshot.Empty);
        this.gpuProvider = gpuProvider ?? UnavailableGpuMetricsProvider.Instance;
        this.sampleIntervalMs = sampleIntervalMs > 0 ? sampleIntervalMs : 1000;
        this.ringBuffer = new PerformanceRingBuffer(ringBufferCapacity > 0 ? ringBufferCapacity : 60);
        this.cpuCalculator = new CpuUsageCalculator(this.process);

        try
        {
            this.startTimeUtc = this.process.StartTime.ToUniversalTime();
        }
        catch
        {
            this.startTimeUtc = DateTime.UtcNow;
        }

        // Initialize with a safe default snapshot
        this.latestSnapshot = new RuntimePerformanceSnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            Uptime = TimeSpan.Zero
        };
    }

    /// <summary>
    /// Starts background periodic sampling loop.
    /// </summary>
    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed || sampleLoopTask != null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            sampleLoopTask = Task.Run(() => SampleLoopAsync(cts.Token));
        }
    }

    /// <summary>
    /// Stops the background sampling loop.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? toCancel;
        Task? taskToWait;

        lock (syncRoot)
        {
            toCancel = cts;
            taskToWait = sampleLoopTask;
            cts = null;
            sampleLoopTask = null;
        }

        if (toCancel != null)
        {
            try
            {
                toCancel.Cancel();
                taskToWait?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Clean teardown
            }
            finally
            {
                toCancel.Dispose();
            }
        }
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        // Initial sample to prime CPU delta calculation
        TakeSnapshot();

        while (!cancellationToken.IsCancellationRequested && !disposed)
        {
            try
            {
                await Task.Delay(sampleIntervalMs, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || disposed)
                {
                    break;
                }

                TakeSnapshot();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Unexpected error in performance sample loop: {ex}");
            }
        }
    }

    /// <summary>
    /// Captures a single performance snapshot. Completely failure-isolated.
    /// </summary>
    public RuntimePerformanceSnapshot TakeSnapshot()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var uptime = DateTime.UtcNow - startTimeUtc;
            if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;

            // Compute CPU usage
            var cpu = cpuCalculator.CalculateCpuUsage();

            // Read memory, threads, handles
            long workingSet = 0;
            long privateMemory = 0;
            var threadCount = 0;
            var handleCount = 0;

            try
            {
                process.Refresh();
                workingSet = process.WorkingSet64;
                privateMemory = process.PrivateMemorySize64;
                threadCount = process.Threads.Count;
                handleCount = process.HandleCount;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error reading process counters: {ex.Message}");
            }

            // Read runtime state safely
            RuntimeHostStateSnapshot state;
            try
            {
                state = stateProvider();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error querying runtime host state: {ex.Message}");
                state = RuntimeHostStateSnapshot.Empty;
            }

            // Query GPU safely
            double? gpuUsage = null;
            long? gpuMemory = null;
            var gpuReason = gpuProvider.StatusReason;
            try
            {
                if (gpuProvider.IsAvailable)
                {
                    gpuUsage = gpuProvider.GetGpuUsagePercent();
                    gpuMemory = gpuProvider.GetGpuMemoryBytes();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error querying GPU metrics: {ex.Message}");
            }

            var snapshot = new RuntimePerformanceSnapshot
            {
                TimestampUtc = now,
                ProcessId = Environment.ProcessId,
                Uptime = uptime,
                CpuPercent = cpu,
                WorkingSetBytes = workingSet,
                PrivateMemoryBytes = privateMemory,
                ThreadCount = threadCount,
                HandleCount = handleCount,

                WallpaperType = state.WallpaperType,
                WallpaperState = state.WallpaperState,
                PauseReasons = state.PauseReasons,
                ActiveWallpaperPath = state.ActiveWallpaperPath,
                DesktopHostConnected = state.DesktopHostConnected,
                RendererWindowAlive = state.RendererWindowAlive,

                SourceWidth = state.SourceWidth,
                SourceHeight = state.SourceHeight,
                DecodeWidth = state.DecodeWidth,
                DecodeHeight = state.DecodeHeight,
                ApproximateDecodedMemoryBytes = state.ApproximateDecodedMemoryBytes,

                VideoWidth = state.VideoWidth,
                VideoHeight = state.VideoHeight,
                PlaybackStatus = state.PlaybackStatus,
                PlaybackPosition = state.PlaybackPosition,
                PlaybackDuration = state.PlaybackDuration,

                FullscreenPauseActive = state.FullscreenPauseActive,
                PowerPauseActive = state.PowerPauseActive,
                ManualPauseActive = state.ManualPauseActive,

                GpuPercent = gpuUsage,
                GpuMemoryBytes = gpuMemory,
                GpuStatusMessage = gpuReason
            };

            lock (syncRoot)
            {
                latestSnapshot = snapshot;
                ringBuffer.Add(snapshot);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Critical telemetry failure isolated: {ex}");
            var fallback = new RuntimePerformanceSnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
                Uptime = TimeSpan.Zero
            };

            lock (syncRoot)
            {
                latestSnapshot = fallback;
            }

            return fallback;
        }
    }

    /// <summary>
    /// Returns the combined status containing the latest snapshot and rolling short-term statistics.
    /// </summary>
    public RuntimePerformanceStatus GetStatus()
    {
        lock (syncRoot)
        {
            var snap = latestSnapshot;
            var stats = ringBuffer.CalculateStats(snap);
            return new RuntimePerformanceStatus
            {
                Snapshot = snap,
                Stats = stats
            };
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
    }
}
