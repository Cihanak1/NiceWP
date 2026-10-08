using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Represents the in-memory active wallpaper state tracked by NiceWP.Runtime.Host.
/// </summary>
public sealed class RuntimeWallpaperState
{
    public static readonly RuntimeWallpaperState Empty = new(IpcConstants.WallpaperTypeNone, null);

    public string Type { get; }
    public string? Path { get; }

    public bool HasActiveWallpaper =>
        !string.Equals(Type, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Path);

    public RuntimeWallpaperState(string type, string? path)
    {
        Type = string.IsNullOrWhiteSpace(type) ? IpcConstants.WallpaperTypeNone : type;
        Path = path;
    }
}
