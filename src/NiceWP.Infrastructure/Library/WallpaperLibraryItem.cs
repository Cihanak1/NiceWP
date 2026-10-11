namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Represents a single wallpaper entry persisted in the local wallpaper library (%LocalAppData%\NiceWP\library.json).
/// </summary>
public sealed class WallpaperLibraryItem
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset AddedAtUtc { get; set; }

    /// <summary>
    /// Computes at runtime whether the underlying wallpaper file currently exists on disk.
    /// </summary>
    public bool FileExists() =>
        !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
}
