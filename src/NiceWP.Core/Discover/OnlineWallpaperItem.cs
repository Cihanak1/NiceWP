namespace NiceWP.Core.Discover;

/// <summary>
/// Represents a normalized online wallpaper item retrieved from an open-license provider.
/// </summary>
public sealed record OnlineWallpaperItem(
    string ProviderId,
    string RemoteId,
    string Title,
    string? Description,
    string MediaType,
    int? Width,
    int? Height,
    double? AspectRatio,
    string ThumbnailUrl,
    string PreviewUrl,
    string DownloadUrl,
    string? CreatorName,
    string? CreatorUrl,
    string SourceName,
    string SourceUrl,
    string LicenseCode,
    string LicenseName,
    string? LicenseUrl,
    string AttributionText,
    bool IsPublicDomain,
    bool RequiresAttribution,
    bool CommercialUseAllowed,
    bool DerivativeUseAllowed,
    bool Mature = false,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? PublishedAt = null,
    IReadOnlyDictionary<string, string>? ProviderMetadata = null);
