namespace NiceWP.Core.Discover;

/// <summary>
/// Encapsulates the results of an online wallpaper provider query.
/// </summary>
public sealed record WallpaperSearchResult(
    string ProviderId,
    IReadOnlyList<OnlineWallpaperItem> Items,
    int? TotalCount = null,
    int Page = 1,
    bool HasMore = false,
    ProviderRateLimitState? RateLimit = null,
    string? ErrorMessage = null)
{
    public static WallpaperSearchResult Empty(string providerId, string? errorMessage = null, ProviderRateLimitState? rateLimit = null) =>
        new(providerId, Array.Empty<OnlineWallpaperItem>(), 0, 1, false, rateLimit, errorMessage);
}
