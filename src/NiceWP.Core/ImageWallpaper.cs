namespace NiceWP.Core;

public static class ImageWallpaper
{
    public static WallpaperResult Validate(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return new(WallpaperError.InvalidPath, "Select a local image file.");
        try
        {
            fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return new(WallpaperError.UnsupportedFormat, "Only PNG and JPEG images are supported.");
            return File.Exists(fullPath) ? new() : new(WallpaperError.FileNotFound, "Image file was not found.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(WallpaperError.InvalidPath, "The image path is invalid.");
        }
    }
}
