namespace NiceWP.Core.Discover;

/// <summary>
/// Defines feature capabilities supported by a specific online wallpaper provider.
/// </summary>
public sealed record ProviderCapabilities(
    bool SupportsVideo = false,
    bool SupportsOrientationFilter = true,
    bool SupportsResolutionFilter = true,
    bool SupportsLicenseFilter = true,
    bool SupportsSorting = false,
    IReadOnlyList<string>? SupportedSortOptions = null);
