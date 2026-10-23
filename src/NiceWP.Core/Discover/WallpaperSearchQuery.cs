namespace NiceWP.Core.Discover;

/// <summary>
/// Encapsulates multi-provider online search query parameters.
/// </summary>
public sealed record WallpaperSearchQuery(
    string Query,
    string? ProviderId = null,
    string? Orientation = null,
    string? MinResolution = null,
    string? MediaType = null,
    string? LicenseFilter = null,
    string? Sort = null,
    bool SafeContent = true,
    int Page = 1,
    int PageSize = 24);
