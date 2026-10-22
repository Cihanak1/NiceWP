using NiceWP.Core;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Specifies virtual smart collection query categories across the wallpaper library.
/// Virtual collections do not move files; they provide instant query/filter projections.
/// </summary>
public enum SmartCollectionFilter
{
    All = 0,
    Favorites = 1,
    RecentlyAdded = 2,
    Dark = 3,
    Light = 4,
    LowResource = 5,
    HighResolution = 6,
    Static = 7,
    Video = 8,
    LowMotion = 9
}

public static class SmartCollectionFilterExtensions
{
    public const string FilterAll = "All";
    public const string FilterFavorites = "Favorites";
    public const string FilterRecentlyAdded = "RecentlyAdded";
    public const string FilterDark = "Dark";
    public const string FilterLight = "Light";
    public const string FilterLowResource = "LowResource";
    public const string FilterHighResolution = "HighResolution";
    public const string FilterStatic = "Static";
    public const string FilterVideo = "Video";
    public const string FilterLowMotion = "LowMotion";

    public static string ToConfigString(this SmartCollectionFilter filter) => filter switch
    {
        SmartCollectionFilter.Favorites => FilterFavorites,
        SmartCollectionFilter.RecentlyAdded => FilterRecentlyAdded,
        SmartCollectionFilter.Dark => FilterDark,
        SmartCollectionFilter.Light => FilterLight,
        SmartCollectionFilter.LowResource => FilterLowResource,
        SmartCollectionFilter.HighResolution => FilterHighResolution,
        SmartCollectionFilter.Static => FilterStatic,
        SmartCollectionFilter.Video => FilterVideo,
        SmartCollectionFilter.LowMotion => FilterLowMotion,
        _ => FilterAll
    };

    public static SmartCollectionFilter ParseFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SmartCollectionFilter.All;

        var trimmed = value.Trim();
        if (string.Equals(trimmed, FilterFavorites, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.Favorites;
        if (string.Equals(trimmed, FilterRecentlyAdded, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Recent", StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.RecentlyAdded;
        if (string.Equals(trimmed, FilterDark, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.Dark;
        if (string.Equals(trimmed, FilterLight, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.Light;
        if (string.Equals(trimmed, FilterLowResource, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.LowResource;
        if (string.Equals(trimmed, FilterHighResolution, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.HighResolution;
        if (string.Equals(trimmed, FilterStatic, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Images", StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.Static;
        if (string.Equals(trimmed, FilterVideo, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Videos", StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.Video;
        if (string.Equals(trimmed, FilterLowMotion, StringComparison.OrdinalIgnoreCase)) return SmartCollectionFilter.LowMotion;

        return SmartCollectionFilter.All;
    }
}
