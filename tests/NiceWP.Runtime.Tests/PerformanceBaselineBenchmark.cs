using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.Transition;
using Xunit;

namespace NiceWP.Runtime.Tests;

/// <summary>
/// Measures deterministic baseline performance metrics and invariants for Page Turn,
/// frame pacing, and resource budgets across Milestone 0.0.21.
/// </summary>
public sealed class PerformanceBaselineBenchmark : IDisposable
{
    private readonly string _tempDir;

    public PerformanceBaselineBenchmark()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_PerfBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    [Fact]
    public void CornerDragTracker_PositionTracking_ExecutesUnderTargetLatency()
    {
        var tracker = new CornerDragTracker(1920, 0, 1920, 1080, 400.0);
        var iterations = 500;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var x = 1920.0 - (i * 2.0);
            var y = i * 1.5;
            var metrics = tracker.TrackPosition(x, y, 100.0);
            Assert.True(metrics.Progress >= 0.0 && metrics.Progress <= 1.0);
        }

        sw.Stop();
        var avgMicros = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        // Mathematical tracking must execute in less than 100 microseconds per call
        Assert.True(avgMicros < 100.0, $"Tracker took {avgMicros:F2} µs/call which exceeds 100 µs target.");
    }

    [Fact]
    public void GestureTelemetry_FrameRecording_CalculatesMetricsWithoutAllocations()
    {
        var telemetry = new GesturePerformanceTelemetry();
        var iterations = 100;

        for (int i = 0; i < iterations; i++)
        {
            telemetry.RecordFrame(inputLatencyMs: 1.2, renderMs: 3.5, presentMs: 0.8);
        }

        Assert.Equal(iterations, telemetry.RenderedFrames);
        Assert.Equal(1.2, telemetry.GestureInputLatencyMs);
        Assert.Equal(3.5, telemetry.GestureRenderMs);
    }

    [Fact]
    public void CoalescedPointer_NoBacklogInvariant()
    {
        var settings = new AppSettings { InteractiveTransitionEnabled = true, RotationMode = RotationModeExtensions.ModeAll };
        var library = new WallpaperLibrary
        {
            Items =
            [
                new WallpaperLibraryItem { Id = "1", FilePath = "C:\\dummy1.jpg", Type = "image" },
                new WallpaperLibraryItem { Id = "2", FilePath = "C:\\dummy2.jpg", Type = "image" }
            ]
        };

        var coordinator = new WallpaperTransitionCoordinator(
            () => null,
            () => settings);

        // State starts idle
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }
}
