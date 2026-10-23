namespace NiceWP.Core.Discover;

/// <summary>
/// Defines the contract for an online wallpaper provider (Openverse, Wikimedia Commons, NASA).
/// </summary>
public interface IWallpaperProvider
{
    /// <summary>
    /// Unique provider identifier (e.g., "openverse", "wikimedia", "nasa").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Localized or user-facing display name for the provider.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Provider query and filtering capabilities.
    /// </summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Current rate limit status, if known.
    /// </summary>
    ProviderRateLimitState? CurrentRateLimit { get; }

    /// <summary>
    /// Performs an asynchronous search against the provider API.
    /// </summary>
    Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves full details and high-resolution asset links for a specific remote item.
    /// </summary>
    Task<OnlineWallpaperDetails?> GetDetailsAsync(string remoteId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an asset stream for downloading high-resolution media.
    /// </summary>
    Task<Stream?> OpenDownloadAsync(string downloadUrl, CancellationToken cancellationToken);
}
