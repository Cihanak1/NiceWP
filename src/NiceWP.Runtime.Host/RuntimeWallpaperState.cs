using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Represents the in-memory active wallpaper state tracked by NiceWP.Runtime.Host.
/// </summary>
public sealed class RuntimeWallpaperState
{
    public static readonly RuntimeWallpaperState Empty = new(IpcConstants.WallpaperTypeNone, null, IpcConstants.PlaybackStateNone);

    public string Type { get; }
    public string? Path { get; }
    public string PlaybackState { get; }

    public bool HasActiveWallpaper =>
        !string.Equals(Type, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Path);

    public bool IsPaused =>
        HasActiveWallpaper &&
        string.Equals(PlaybackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);

    public bool IsRunning =>
        HasActiveWallpaper &&
        string.Equals(PlaybackState, IpcConstants.PlaybackStateRunning, StringComparison.OrdinalIgnoreCase);

    public RuntimeWallpaperState(string type, string? path, string? playbackState = null)
    {
        Type = string.IsNullOrWhiteSpace(type) ? IpcConstants.WallpaperTypeNone : type;
        Path = path;

        if (!HasActiveWallpaper)
        {
            PlaybackState = IpcConstants.PlaybackStateNone;
        }
        else if (string.Equals(playbackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase))
        {
            PlaybackState = IpcConstants.PlaybackStatePaused;
        }
        else
        {
            PlaybackState = IpcConstants.PlaybackStateRunning;
        }
    }

    public RuntimeWallpaperState AsPaused() =>
        HasActiveWallpaper
            ? new RuntimeWallpaperState(Type, Path, IpcConstants.PlaybackStatePaused)
            : Empty;

    public RuntimeWallpaperState AsRunning() =>
        HasActiveWallpaper
            ? new RuntimeWallpaperState(Type, Path, IpcConstants.PlaybackStateRunning)
            : Empty;
}
