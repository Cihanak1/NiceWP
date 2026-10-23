namespace NiceWP.Core.Discover;

/// <summary>
/// Strict governance policy declaring permitted open-license production providers
/// and explicitly prohibiting restricted/unapproved providers.
/// </summary>
public static class OnlineProviderPolicy
{
    public const string ProviderOpenverse = "openverse";
    public const string ProviderWikimedia = "wikimedia";
    public const string ProviderNasa = "nasa";

    public const string ExcludedUnsplash = "unsplash";
    public const string ExcludedPexels = "pexels";
    public const string ExcludedPixabay = "pixabay";
    public const string ExcludedWallhaven = "wallhaven";

    public const string ReasonPexelsProhibited =
        "Pexels API Terms of Service strictly prohibit standalone wallpaper applications.";

    public const string ReasonPixabayProhibited =
        "Pixabay Content License restricts standalone wallpaper redistribution without significant modification.";

    public const string ReasonUnsplashProhibited =
        "Unsplash API Guidelines explicitly classify wallpaper applications as core experience replication requiring prior written approval.";

    public const string ReasonWallhavenProhibited =
        "Wallhaven relies on community-submitted content without uniform open-access copyright verification.";

    public static readonly IReadOnlyList<string> AllowedProductionProviderIds =
    [
        ProviderOpenverse,
        ProviderWikimedia,
        ProviderNasa
    ];

    public static readonly IReadOnlyList<string> ExcludedProviderIds =
    [
        ExcludedUnsplash,
        ExcludedPexels,
        ExcludedPixabay,
        ExcludedWallhaven
    ];

    /// <summary>
    /// Checks whether the given provider is permitted as a production provider in NiceWP 0.0.20.
    /// </summary>
    public static bool IsAllowedProductionProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        return AllowedProductionProviderIds.Contains(normalized);
    }

    /// <summary>
    /// Checks whether the given provider is explicitly excluded, returning the policy rationale.
    /// </summary>
    public static bool IsExcludedProvider(string? providerId, out string rationale)
    {
        rationale = string.Empty;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case ExcludedPexels:
                rationale = ReasonPexelsProhibited;
                return true;
            case ExcludedPixabay:
                rationale = ReasonPixabayProhibited;
                return true;
            case ExcludedUnsplash:
                rationale = ReasonUnsplashProhibited;
                return true;
            case ExcludedWallhaven:
                rationale = ReasonWallhavenProhibited;
                return true;
            default:
                return false;
        }
    }
}
