using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Settings;

/// <summary>
/// Represents persisted user and runtime settings stored in %LocalAppData%\NiceWP\settings.json.
/// Autostart state is intentionally excluded so the Windows Registry remains the single source of truth.
/// </summary>
public sealed class AppSettings
{
    public bool RestoreLastWallpaper { get; set; } = true;

    public string LastWallpaperType { get; set; } = IpcConstants.WallpaperTypeNone;

    public string? LastWallpaperPath { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        RestoreLastWallpaper = true,
        LastWallpaperType = IpcConstants.WallpaperTypeNone,
        LastWallpaperPath = null
    };

    /// <summary>
    /// Returns a normalized copy where invalid wallpaper types or inconsistent paths
    /// fall back to safe default values.
    /// </summary>
    public AppSettings Normalize()
    {
        var normalizedType = NormalizeWallpaperType(LastWallpaperType);
        var normalizedPath = string.IsNullOrWhiteSpace(LastWallpaperPath) ? null : LastWallpaperPath.Trim();

        if (normalizedType == IpcConstants.WallpaperTypeNone || normalizedPath == null)
        {
            normalizedType = IpcConstants.WallpaperTypeNone;
            normalizedPath = null;
        }

        return new AppSettings
        {
            RestoreLastWallpaper = RestoreLastWallpaper,
            LastWallpaperType = normalizedType,
            LastWallpaperPath = normalizedPath
        };
    }

    public static bool IsValidWallpaperType(string? wallpaperType) =>
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeWallpaperType(string? wallpaperType)
    {
        if (string.Equals(wallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase))
        {
            return IpcConstants.WallpaperTypeImage;
        }

        if (string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase))
        {
            return IpcConstants.WallpaperTypeVideo;
        }

        return IpcConstants.WallpaperTypeNone;
    }
}
