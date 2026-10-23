namespace NiceWP.Core.Discover;

/// <summary>
/// Detailed metadata for an online wallpaper item, including high-resolution asset streams and provenance.
/// </summary>
public sealed record OnlineWallpaperDetails(
    string ProviderId,
    string RemoteId,
    string Title,
    string? Description,
    string MediaType,
    int? Width,
    int? Height,
    string DownloadUrl,
    string? PreviewUrl,
    string? CreatorName,
    string? CreatorUrl,
    string SourceName,
    string SourceUrl,
    string LicenseCode,
    string LicenseName,
    string? LicenseUrl,
    string AttributionText,
    IReadOnlyDictionary<string, string>? AdditionalMetadata = null);
