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

    public bool IsFavorite { get; set; }

    public DateTimeOffset? LastUsedAtUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRemoteImported { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceHost { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDisplayUrl { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NiceWP.Core.Discover.WallpaperOrigin? Origin { get; set; }

    /// <summary>
    /// Computes at runtime whether the underlying wallpaper file currently exists on disk.
    /// </summary>
    public bool FileExists() =>
        !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
}
