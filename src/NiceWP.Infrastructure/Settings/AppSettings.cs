using System.Text.Json.Serialization;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Settings;

/// <summary>
/// Represents persisted user and runtime settings stored in %LocalAppData%\NiceWP\settings.json.
/// Autostart state is intentionally excluded so the Windows Registry remains the single source of truth.
/// </summary>
public sealed class AppSettings
{
    public const string ThemeSystem = "System";
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";

    public bool RestoreLastWallpaper { get; set; } = true;

    public string LastWallpaperType { get; set; } = IpcConstants.WallpaperTypeNone;

    public string? LastWallpaperPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        RestoreLastWallpaper = true,
        LastWallpaperType = IpcConstants.WallpaperTypeNone,
        LastWallpaperPath = null,
        Theme = null
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
            LastWallpaperPath = normalizedPath,
            Theme = NormalizeTheme(Theme)
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

    public static string? NormalizeTheme(string? theme)
    {
        if (string.Equals(theme?.Trim(), ThemeLight, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeLight;
        }

        if (string.Equals(theme?.Trim(), ThemeDark, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeDark;
        }

        return null;
    }
}
