using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Immutable point-in-time performance and runtime state snapshot of NiceWP.Runtime.Host.
/// </summary>
public sealed class RuntimePerformanceSnapshot
{
    // Process & System Metrics
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public int ProcessId { get; init; }
    public TimeSpan Uptime { get; init; }
    public double? CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }

    // Runtime & Wallpaper State
    public string WallpaperType { get; init; } = IpcConstants.WallpaperTypeNone;
    public string WallpaperState { get; init; } = IpcConstants.PlaybackStateNone;
    public string PauseReasons { get; init; } = "None";
    public string? ActiveWallpaperPath { get; init; }
    public bool DesktopHostConnected { get; init; }
    public bool RendererWindowAlive { get; init; }

    // Image-specific Details (null if not image or unavailable)
    public uint? SourceWidth { get; init; }
    public uint? SourceHeight { get; init; }
    public uint? DecodeWidth { get; init; }
    public uint? DecodeHeight { get; init; }
    public long? ApproximateDecodedMemoryBytes { get; init; }

    // Video-specific Details (null if not video or unavailable)
    public uint? VideoWidth { get; init; }
    public uint? VideoHeight { get; init; }
    public string? PlaybackStatus { get; init; }
    public TimeSpan? PlaybackPosition { get; init; }
    public TimeSpan? PlaybackDuration { get; init; }

    // Policy & Efficiency Flags
    public bool FullscreenPauseActive { get; init; }
    public bool PowerPauseActive { get; init; }
    public bool ManualPauseActive { get; init; }

    // GPU Metrics (null when unavailable)
    public double? GpuPercent { get; init; }
    public long? GpuMemoryBytes { get; init; }
    public string? GpuStatusMessage { get; init; } = "Unavailable (Process-level GPU counter requires engine enumeration with high overhead)";

    public bool IsImageActive =>
        string.Equals(WallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase);

    public bool IsVideoActive =>
        string.Equals(WallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

    public bool HasActiveWallpaper =>
        !string.Equals(WallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase);
}
