namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Simple JSON-serializable response model for local IPC replies.
/// </summary>
public sealed class IpcResponse
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public bool IsRunning { get; set; }
    public bool HasActiveWallpaper { get; set; }
    public string WallpaperType { get; set; } = IpcConstants.WallpaperTypeNone;
    public string? ActivePath { get; set; }
    public bool IsPaused { get; set; }
    public string PlaybackState { get; set; } = IpcConstants.PlaybackStateNone;

    public static IpcResponse Ok(string requestId, string? message = null) =>
        new()
        {
            RequestId = requestId,
            Success = true,
            Message = message,
            IsRunning = true
        };

    public static IpcResponse Fail(string requestId, string? message = null) =>
        new()
        {
            RequestId = requestId,
            Success = false,
            Message = message,
            IsRunning = false
        };

    public static IpcResponse Status(
        string requestId,
        bool isRunning,
        bool hasActiveWallpaper,
        string wallpaperType,
        string? activePath,
        string? message = null,
        string? playbackState = null,
        bool? isPaused = null)
    {
        var resolvedPlaybackState = string.IsNullOrWhiteSpace(playbackState)
            ? (hasActiveWallpaper
                ? (isPaused == true ? IpcConstants.PlaybackStatePaused : IpcConstants.PlaybackStateRunning)
                : IpcConstants.PlaybackStateNone)
            : playbackState;
        var resolvedIsPaused = isPaused ?? string.Equals(resolvedPlaybackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);

        return new()
        {
            RequestId = requestId,
            Success = true,
            Message = message,
            IsRunning = isRunning,
            HasActiveWallpaper = hasActiveWallpaper,
            WallpaperType = wallpaperType,
            ActivePath = activePath,
            IsPaused = resolvedIsPaused,
            PlaybackState = resolvedPlaybackState
        };
    }
}
