using System.Text.Json.Serialization;

namespace NiceWP.Core;

/// <summary>
/// Persisted lightweight metadata describing analyzed visual and performance attributes of a wallpaper item.
/// Designed to be source-agnostic (supporting local files today and cached/online sources in future milestones).
/// </summary>
public sealed class WallpaperAnalysisMetadata
{
    public string WallpaperId { get; set; } = string.Empty;

    public string SourceKey { get; set; } = string.Empty;

    public DateTimeOffset AnalyzedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public WallpaperMediaType MediaType { get; set; } = WallpaperMediaType.Image;

    public int Width { get; set; }

    public int Height { get; set; }

    public double AspectRatio { get; set; }

    public double Brightness { get; set; }

    public string DominantColorHex { get; set; } = "#000000";

    public WallpaperColorTemperature ColorTemperature { get; set; } = WallpaperColorTemperature.Neutral;

    public WallpaperMotionLevel MotionLevel { get; set; } = WallpaperMotionLevel.None;

    public WallpaperCost ResourceClass { get; set; } = WallpaperCost.Light;

    public bool IsDark { get; set; }

    public bool IsLight { get; set; }

    public bool IsHighResolution { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationSeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrameRate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Bitrate { get; set; }

    public long FileSizeBytes { get; set; }

    public DateTimeOffset FileLastModifiedUtc { get; set; }

    /// <summary>
    /// Checks whether this analysis metadata matches current file attributes on disk.
    /// </summary>
    public bool IsStale(long currentSizeBytes, DateTimeOffset currentLastModifiedUtc)
    {
        return FileSizeBytes != currentSizeBytes ||
               Math.Abs((FileLastModifiedUtc - currentLastModifiedUtc).TotalSeconds) > 1.0;
    }
}
