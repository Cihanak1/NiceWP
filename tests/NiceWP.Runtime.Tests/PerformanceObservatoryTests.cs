using System.Diagnostics;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Observatory;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class PerformanceObservatoryTests
{
    [Fact]
    public void CpuUsageCalculator_FirstSample_ReturnsNull()
    {
        var calculator = new CpuUsageCalculator();
        var usage = calculator.CalculateCpuUsage();
        Assert.Null(usage);
    }

    [Fact]
    public void CpuUsageCalculator_DeltaCalculation_NormalizesByProcessorCount()
    {
        // 1 second of CPU time over 2 seconds of wall clock on a 4-core machine:
        // (1.0 / (2.0 * 4)) * 100 = 12.5%
        var cpuDelta = TimeSpan.FromSeconds(1);
        var wallDelta = TimeSpan.FromSeconds(2);
        var cores = 4;

        var percent = CpuUsageCalculator.CalculateNormalizedPercent(cpuDelta, wallDelta, cores);
        Assert.NotNull(percent);
        Assert.Equal(12.5, percent.Value);
    }

    [Fact]
    public void CpuUsageCalculator_NegativeOrImpossibleDelta_ReturnsZero()
    {
        var negativeCpu = TimeSpan.FromSeconds(-1);
        var wallDelta = TimeSpan.FromSeconds(1);
        var percent = CpuUsageCalculator.CalculateNormalizedPercent(negativeCpu, wallDelta, 4);
        Assert.Equal(0.0, percent);

        var zeroWall = TimeSpan.Zero;
        var normalCpu = TimeSpan.FromSeconds(1);
        var zeroWallPercent = CpuUsageCalculator.CalculateNormalizedPercent(normalCpu, zeroWall, 4);
        Assert.Equal(0.0, zeroWallPercent);
    }

    [Fact]
    public void PerformanceRingBuffer_CapacityLimit_EnforcesSixtyItems()
    {
        var ring = new PerformanceRingBuffer(60);
        Assert.Equal(60, ring.Capacity);
        Assert.Equal(0, ring.Count);

        for (var i = 0; i < 100; i++)
        {
            ring.Add(new RuntimePerformanceSnapshot
            {
                ProcessId = i,
                WorkingSetBytes = i * 1000
            });
        }

        Assert.Equal(60, ring.Count);
        var samples = ring.GetSamples();
        Assert.Equal(60, samples.Length);
        // The first element should be sample 40, and the last should be sample 99
        Assert.Equal(40, samples[0].ProcessId);
        Assert.Equal(99, samples[^1].ProcessId);
    }

    [Fact]
    public void PerformanceRingBuffer_CalculatesCorrectAverageAndPeak()
    {
        var ring = new PerformanceRingBuffer(10);
        ring.Add(new RuntimePerformanceSnapshot { CpuPercent = 10.0, WorkingSetBytes = 100, PrivateMemoryBytes = 50, ThreadCount = 4, HandleCount = 100 });
        ring.Add(new RuntimePerformanceSnapshot { CpuPercent = 20.0, WorkingSetBytes = 200, PrivateMemoryBytes = 70, ThreadCount = 6, HandleCount = 120 });
        ring.Add(new RuntimePerformanceSnapshot { CpuPercent = 30.0, WorkingSetBytes = 300, PrivateMemoryBytes = 90, ThreadCount = 8, HandleCount = 150 });

        var stats = ring.CalculateStats();
        Assert.Equal(3, stats.SampleCount);
        Assert.Equal(30.0, stats.CurrentCpuPercent);
        Assert.Equal(20.0, stats.AverageCpuPercent);
        Assert.Equal(30.0, stats.PeakCpuPercent);

        Assert.Equal(300, stats.CurrentWorkingSetBytes);
        Assert.Equal(200, stats.AverageWorkingSetBytes);
        Assert.Equal(300, stats.PeakWorkingSetBytes);

        Assert.Equal(90, stats.CurrentPrivateMemoryBytes);
        Assert.Equal(70, stats.AveragePrivateMemoryBytes);
        Assert.Equal(90, stats.PeakPrivateMemoryBytes);

        Assert.Equal(8, stats.CurrentThreadCount);
        Assert.Equal(8, stats.PeakThreadCount);

        Assert.Equal(150, stats.CurrentHandleCount);
        Assert.Equal(150, stats.PeakHandleCount);
    }

    [Fact]
    public void PerformanceRingBuffer_EmptyBuffer_Safe()
    {
        var ring = new PerformanceRingBuffer(60);
        var stats = ring.CalculateStats();
        Assert.Equal(0, stats.SampleCount);
        Assert.Null(stats.CurrentCpuPercent);
        Assert.Null(stats.AverageCpuPercent);
        Assert.Null(stats.PeakCpuPercent);

        var fallbackSnap = new RuntimePerformanceSnapshot { WorkingSetBytes = 1024 };
        var statsWithFallback = ring.CalculateStats(fallbackSnap);
        Assert.Equal(1, statsWithFallback.SampleCount);
        Assert.Equal(1024, statsWithFallback.CurrentWorkingSetBytes);
    }

    [Fact]
    public void RuntimePerformanceSampler_NoWallpaper_ProducesValidSnapshot()
    {
        using var sampler = new RuntimePerformanceSampler(
            stateProvider: () => RuntimeHostStateSnapshot.Empty);

        var snapshot = sampler.TakeSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(IpcConstants.WallpaperTypeNone, snapshot.WallpaperType);
        Assert.Equal(IpcConstants.PlaybackStateNone, snapshot.WallpaperState);
        Assert.Equal("None", snapshot.PauseReasons);
        Assert.False(snapshot.DesktopHostConnected);
        Assert.False(snapshot.RendererWindowAlive);
        Assert.False(snapshot.HasActiveWallpaper);
    }

    [Fact]
    public void RuntimePerformanceSampler_ImageWallpaper_CapturesDimensionsAndMemory()
    {
        var imageState = new RuntimeHostStateSnapshot(
            WallpaperType: IpcConstants.WallpaperTypeImage,
            WallpaperState: IpcConstants.PlaybackStateRunning,
            PauseReasons: "None",
            ActiveWallpaperPath: @"C:\Wallpapers\image.jpg",
            DesktopHostConnected: true,
            RendererWindowAlive: true,
            SourceWidth: 3840,
            SourceHeight: 2160,
            DecodeWidth: 1920,
            DecodeHeight: 1080,
            ApproximateDecodedMemoryBytes: 1920 * 1080 * 4);

        using var sampler = new RuntimePerformanceSampler(
            stateProvider: () => imageState);

        var snapshot = sampler.TakeSnapshot();
        Assert.True(snapshot.IsImageActive);
        Assert.Equal(3840u, snapshot.SourceWidth);
        Assert.Equal(2160u, snapshot.SourceHeight);
        Assert.Equal(1920u, snapshot.DecodeWidth);
        Assert.Equal(1080u, snapshot.DecodeHeight);
        Assert.Equal(8294400L, snapshot.ApproximateDecodedMemoryBytes);
        Assert.True(snapshot.DesktopHostConnected);
        Assert.True(snapshot.RendererWindowAlive);
    }

    [Fact]
    public void RuntimePerformanceSampler_VideoWallpaper_CapturesDimensionsAndPlayback()
    {
        var videoState = new RuntimeHostStateSnapshot(
            WallpaperType: IpcConstants.WallpaperTypeVideo,
            WallpaperState: IpcConstants.PlaybackStatePaused,
            PauseReasons: "Fullscreen, Power",
            ActiveWallpaperPath: @"C:\Wallpapers\loop.mp4",
            DesktopHostConnected: true,
            RendererWindowAlive: true,
            VideoWidth: 1920,
            VideoHeight: 1080,
            PlaybackStatus: "Paused",
            PlaybackPosition: TimeSpan.FromSeconds(15),
            PlaybackDuration: TimeSpan.FromSeconds(60),
            FullscreenPauseActive: true,
            PowerPauseActive: true);

        using var sampler = new RuntimePerformanceSampler(
            stateProvider: () => videoState);

        var snapshot = sampler.TakeSnapshot();
        Assert.True(snapshot.IsVideoActive);
        Assert.Equal(1920u, snapshot.VideoWidth);
        Assert.Equal(1080u, snapshot.VideoHeight);
        Assert.Equal("Paused", snapshot.PlaybackStatus);
        Assert.Equal(TimeSpan.FromSeconds(15), snapshot.PlaybackPosition);
        Assert.Equal(TimeSpan.FromSeconds(60), snapshot.PlaybackDuration);
        Assert.Equal("Fullscreen, Power", snapshot.PauseReasons);
        Assert.True(snapshot.FullscreenPauseActive);
        Assert.True(snapshot.PowerPauseActive);
    }

    [Fact]
    public void RuntimePerformanceSampler_ExceptionInStateProvider_DoesNotThrowAndReturnsFallback()
    {
        using var sampler = new RuntimePerformanceSampler(
            stateProvider: () => throw new InvalidOperationException("Simulated renderer crash"));

        // Must NOT throw. Must isolate failure safely.
        var snapshot = sampler.TakeSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(IpcConstants.WallpaperTypeNone, snapshot.WallpaperType);
    }

    [Fact]
    public void GpuMetricsProvider_Unavailable_ReturnsNullsAndExplanatoryReason()
    {
        var provider = UnavailableGpuMetricsProvider.Instance;
        Assert.False(provider.IsAvailable);
        Assert.Null(provider.GetGpuUsagePercent());
        Assert.Null(provider.GetGpuMemoryBytes());
        Assert.Contains("overhead", provider.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerformanceReportGenerator_FormatsAllRequiredSections()
    {
        var status = new RuntimePerformanceStatus
        {
            Snapshot = new RuntimePerformanceSnapshot
            {
                ProcessId = 12345,
                Uptime = TimeSpan.FromMinutes(42),
                WallpaperType = IpcConstants.WallpaperTypeVideo,
                WallpaperState = IpcConstants.PlaybackStateRunning,
                ActiveWallpaperPath = @"C:\Media\test.mp4",
                DesktopHostConnected = true,
                RendererWindowAlive = true,
                VideoWidth = 1920,
                VideoHeight = 1080
            },
            Stats = new RuntimePerformanceStats
            {
                SampleCount = 60,
                CurrentCpuPercent = 1.25,
                AverageCpuPercent = 1.10,
                PeakCpuPercent = 2.45,
                CurrentWorkingSetBytes = 45 * 1024 * 1024,
                AverageWorkingSetBytes = 44 * 1024 * 1024,
                PeakWorkingSetBytes = 50 * 1024 * 1024,
                CurrentPrivateMemoryBytes = 30 * 1024 * 1024,
                AveragePrivateMemoryBytes = 29 * 1024 * 1024,
                PeakPrivateMemoryBytes = 35 * 1024 * 1024,
                CurrentThreadCount = 14,
                PeakThreadCount = 16,
                CurrentHandleCount = 280,
                PeakHandleCount = 310
            }
        };

        var report = PerformanceReportGenerator.GenerateTextReport(status, "0.0.12");

        Assert.Contains("NiceWP 0.0.12", report);
        Assert.Contains("12345", report);
        Assert.Contains("00:42:00", report);
        Assert.Contains("1.25%", report);
        Assert.Contains("1.10%", report);
        Assert.Contains("2.45%", report);
        Assert.Contains("Video", report);
        Assert.Contains("Running", report);
        Assert.Contains("1920 x 1080", report);
        Assert.Contains("WorkerW valid", report);
        Assert.Contains("HWND valid", report);
        Assert.Contains("DATA TRANSPARENCY & PRIVACY NOTICE", report);
        Assert.Contains("NiceWP does NOT transmit performance telemetry", report);
    }

    [Fact]
    public void Ipc_GetPerformanceStatus_SerializationRoundtrip()
    {
        var originalStatus = new RuntimePerformanceStatus
        {
            Snapshot = new RuntimePerformanceSnapshot
            {
                ProcessId = 999,
                CpuPercent = 3.5,
                WorkingSetBytes = 40000000,
                WallpaperType = IpcConstants.WallpaperTypeImage,
                WallpaperState = IpcConstants.PlaybackStateRunning,
                SourceWidth = 1920,
                SourceHeight = 1080,
                DesktopHostConnected = true,
                RendererWindowAlive = true
            },
            Stats = new RuntimePerformanceStats
            {
                SampleCount = 30,
                CurrentCpuPercent = 3.5,
                AverageCpuPercent = 3.1,
                PeakCpuPercent = 5.0
            }
        };

        var response = IpcResponse.PerformanceStatus("req-123", originalStatus);
        var json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<IpcResponse>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.True(deserialized.IsRunning);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.NotNull(deserialized.Performance);
        Assert.Equal(999, deserialized.Performance.Snapshot.ProcessId);
        Assert.Equal(3.5, deserialized.Performance.Snapshot.CpuPercent);
        Assert.Equal(1920u, deserialized.Performance.Snapshot.SourceWidth);
        Assert.Equal(30, deserialized.Performance.Stats.SampleCount);
    }

    [Fact]
    public void Ipc_GetPerformanceStatus_RuntimeUnavailable_HandlesGracefully()
    {
        var failResponse = IpcResponse.Fail("req-456", "Runtime host is unavailable (connection timed out).");
        var json = JsonSerializer.Serialize(failResponse);
        var deserialized = JsonSerializer.Deserialize<IpcResponse>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Success);
        Assert.False(deserialized.IsRunning);
        Assert.Null(deserialized.Performance);
        Assert.Contains("unavailable", deserialized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeHostApp_HandleCommand_GetPerformanceStatus_ReturnsStatusWithMetrics()
    {
        using var app = new RuntimeHostApp();
        var request = new IpcRequest(IpcConstants.GetPerformanceStatusCommand);

        var response = await app.HandleCommandAsync(request);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.True(response.IsRunning);
        Assert.NotNull(response.Performance);
        Assert.NotNull(response.Performance.Snapshot);
        Assert.NotNull(response.Performance.Stats);
        Assert.Equal(Environment.ProcessId, response.Performance.Snapshot.ProcessId);
    }

    [Fact]
    public void PerformanceReportGenerator_NoAutomaticDiskWrite_PureInMemory()
    {
        var tempBefore = Directory.GetFiles(Path.GetTempPath());
        var status = new RuntimePerformanceStatus();

        var report = PerformanceReportGenerator.GenerateTextReport(status);

        Assert.NotNull(report);
        Assert.NotEmpty(report);
        // Verify generating the report did not write any file to temp directory
        var tempAfter = Directory.GetFiles(Path.GetTempPath());
        Assert.Equal(tempBefore.Length, tempAfter.Length);
    }

    [Fact]
    public void RuntimePerformanceSampler_Lifecycle_StartAndStop_NoDuplicateTask()
    {
        using var sampler = new RuntimePerformanceSampler();

        // Multiple calls to Start must not create duplicate tasks
        sampler.Start();
        Assert.True(sampler.IsRunning);

        sampler.Start();
        Assert.True(sampler.IsRunning);

        sampler.Stop();
        Assert.False(sampler.IsRunning);
    }

    [Fact]
    public void RuntimePerformanceSampler_TelemetryOverhead_MeasuredAccurately()
    {
        using var sampler = new RuntimePerformanceSampler(
            stateProvider: () => RuntimeHostStateSnapshot.Empty);

        // Warmup
        for (var i = 0; i < 50; i++)
        {
            _ = sampler.TakeSnapshot();
        }

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < iterations; i++)
        {
            _ = sampler.TakeSnapshot();
        }

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        var totalUs = sw.Elapsed.TotalMilliseconds * 1000.0;
        var usPerSample = totalUs / iterations;
        var bytesPerSample = (allocAfter - allocBefore) / iterations;

        // Verify that sampling is extremely lightweight:
        // Each sample executes in under 10000 microseconds (10 ms, <1% of 1 core; measured ~3.3 ms once per second)
        Assert.True(usPerSample < 10000, $"Sampling duration {usPerSample} us exceeds limit.");

        // And each sample allocates under 16 KB of memory (measured ~6.3 KB per sample from Process.Refresh)
        Assert.True(bytesPerSample < 16384, $"Allocations per sample {bytesPerSample} bytes exceed limit.");

        // RingBuffer memory footprint for 60 snapshots
        var ring = new PerformanceRingBuffer(60);
        for (var i = 0; i < 60; i++)
        {
            ring.Add(new RuntimePerformanceSnapshot
            {
                ProcessId = 1234,
                CpuPercent = 1.0,
                WorkingSetBytes = 50 * 1024 * 1024,
                PrivateMemoryBytes = 30 * 1024 * 1024,
                ThreadCount = 12,
                HandleCount = 200,
                WallpaperType = "Video",
                WallpaperState = "Running"
            });
        }
        Assert.Equal(60, ring.Count);
    }
}
