namespace NiceWP.Core;

public static class VideoWallpaper
{
    public static WallpaperResult Validate(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return new(WallpaperError.InvalidPath, "Select a local MP4 file.");
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
                return new(WallpaperError.UnsupportedFormat, "Only MP4/H.264 video is supported.");
            return File.Exists(fullPath) ? new() : new(WallpaperError.FileNotFound, "Video file was not found.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(WallpaperError.InvalidPath, "The video path is invalid.");
        }
    }
}
