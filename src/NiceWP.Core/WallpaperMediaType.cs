namespace NiceWP.Core;

/// <summary>
/// Identifies the media category of a wallpaper item.
/// </summary>
public enum WallpaperMediaType
{
    Image = 0,
    Video = 1
}

public static class WallpaperMediaTypeExtensions
{
    public const string TypeImage = "Image";
    public const string TypeVideo = "Video";

    public static string ToConfigString(this WallpaperMediaType type) => type switch
    {
        WallpaperMediaType.Video => TypeVideo,
        _ => TypeImage
    };

    public static WallpaperMediaType ParseMediaType(string? value)
    {
        if (string.Equals(value?.Trim(), TypeVideo, StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperMediaType.Video;
        }

        return WallpaperMediaType.Image;
    }
}
