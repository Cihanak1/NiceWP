namespace NiceWP.Core;

public enum WallpaperError
{
    None, InvalidPath, FileNotFound, UnsupportedFormat, InvalidImage,
    RendererFailed, DesktopHostUnavailable, CleanupFailed, Busy, Stopped
}

public sealed record WallpaperResult(WallpaperError Error = WallpaperError.None, string? Message = null)
{
    public bool Success => Error == WallpaperError.None;
}
