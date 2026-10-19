using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Rotation;

/// <summary>
/// Result model returned by rotation operations (Next, Previous, Auto).
/// </summary>
public sealed record WallpaperRotationResult(
    bool Success,
    string Message,
    WallpaperLibraryItem? Item = null,
    bool Skipped = false,
    bool Stopped = false);
