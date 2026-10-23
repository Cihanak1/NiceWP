namespace NiceWP.Core.Discover;

/// <summary>
/// Persisted provenance metadata recording the online source, author attribution, and license of a downloaded wallpaper.
/// </summary>
public sealed class WallpaperOrigin
{
    public const string SourceTypeOnline = "Online";

    public string SourceType { get; set; } = SourceTypeOnline;

    public string Provider { get; set; } = string.Empty;

    public string RemoteId { get; set; } = string.Empty;

    public string? OriginalUrl { get; set; }

    public string? Creator { get; set; }

    public string? CreatorUrl { get; set; }

    public string? License { get; set; }

    public string? LicenseUrl { get; set; }

    public string? AttributionText { get; set; }

    public DateTimeOffset DownloadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
