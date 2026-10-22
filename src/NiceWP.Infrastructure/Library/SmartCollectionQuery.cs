using NiceWP.Core;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Provides unified filtering for smart collections against items and their analyzed metadata.
/// </summary>
public static class SmartCollectionQuery
{
    /// <summary>
    /// Filters a candidate sequence according to the specified smart collection rule.
    /// </summary>
    public static IEnumerable<T> Apply<T>(
        IEnumerable<T> items,
        SmartCollectionFilter filter,
        Func<T, string> idSelector,
        Func<T, bool> isImageSelector,
        Func<T, bool> isFavoriteSelector,
        Func<string, WallpaperAnalysisMetadata?> metadataLookup)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(idSelector);

        return filter switch
        {
            SmartCollectionFilter.Favorites => items.Where(it => isFavoriteSelector(it)),

            SmartCollectionFilter.Static => items.Where(it => isImageSelector(it)),

            SmartCollectionFilter.Video => items.Where(it => !isImageSelector(it)),

            SmartCollectionFilter.Dark => items.Where(it =>
            {
                var meta = metadataLookup(idSelector(it));
                return meta?.IsDark == true;
            }),

            SmartCollectionFilter.Light => items.Where(it =>
            {
                var meta = metadataLookup(idSelector(it));
                return meta?.IsLight == true;
            }),

            SmartCollectionFilter.LowResource => items.Where(it =>
            {
                var meta = metadataLookup(idSelector(it));
                if (meta != null)
                {
                    return meta.ResourceClass == WallpaperCost.Light;
                }
                // Static images are naturally low resource
                return isImageSelector(it);
            }),

            SmartCollectionFilter.HighResolution => items.Where(it =>
            {
                var meta = metadataLookup(idSelector(it));
                return meta?.IsHighResolution == true;
            }),

            SmartCollectionFilter.LowMotion => items.Where(it =>
            {
                var meta = metadataLookup(idSelector(it));
                if (meta != null)
                {
                    return meta.MotionLevel is WallpaperMotionLevel.None or WallpaperMotionLevel.Low;
                }
                // Static images have zero motion
                return isImageSelector(it);
            }),

            _ => items
        };
    }
}
