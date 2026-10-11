using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Represents the persisted local wallpaper library container stored in %LocalAppData%\NiceWP\library.json.
/// </summary>
public sealed class WallpaperLibrary
{
    public const int CurrentVersion = 1;

    private static readonly string[] SupportedImageExtensions = [".png", ".jpg", ".jpeg"];
    private const string SupportedVideoExtension = ".mp4";

    public int Version { get; set; } = CurrentVersion;

    public List<WallpaperLibraryItem> Items { get; set; } = [];

    public static WallpaperLibrary CreateDefault() => new()
    {
        Version = CurrentVersion,
        Items = []
    };

    public WallpaperLibraryItem? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || Items == null)
        {
            return null;
        }

        var trimmedId = id.Trim();
        return Items.FirstOrDefault(item =>
            item != null &&
            string.Equals(item.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
    }

    public WallpaperLibraryItem? FindByPath(string? path)
    {
        if (!TryNormalizePath(path, out var fullPath) || Items == null)
        {
            return null;
        }

        return Items.FirstOrDefault(item =>
            item != null &&
            string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public bool ContainsPath(string? path) => FindByPath(path) != null;

    /// <summary>
    /// Returns a sanitized copy of the library with valid version, normalized items, and no duplicate IDs or paths.
    /// If the library version is unsupported or Items is null, returns an empty Version 1 library.
    /// </summary>
    public WallpaperLibrary Normalize()
    {
        if (Version != CurrentVersion || Items == null)
        {
            return CreateDefault();
        }

        var sanitizedItems = new List<WallpaperLibraryItem>(Items.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawItem in Items)
        {
            if (!TryNormalizeItem(rawItem, out var normalizedItem))
            {
                continue;
            }

            if (!seenIds.Add(normalizedItem.Id) || !seenPaths.Add(normalizedItem.FilePath))
            {
                continue;
            }

            sanitizedItems.Add(normalizedItem);
        }

        return new WallpaperLibrary
        {
            Version = CurrentVersion,
            Items = sanitizedItems
        };
    }

    public static bool TryDetermineTypeFromExtension(string? path, out string wallpaperType)
    {
        wallpaperType = IpcConstants.WallpaperTypeNone;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        if (SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            wallpaperType = IpcConstants.WallpaperTypeImage;
            return true;
        }

        if (string.Equals(extension, SupportedVideoExtension, StringComparison.OrdinalIgnoreCase))
        {
            wallpaperType = IpcConstants.WallpaperTypeVideo;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeItem(WallpaperLibraryItem? rawItem, out WallpaperLibraryItem normalizedItem)
    {
        normalizedItem = null!;
        if (rawItem == null ||
            string.IsNullOrWhiteSpace(rawItem.Id) ||
            string.IsNullOrWhiteSpace(rawItem.FilePath))
        {
            return false;
        }

        if (!TryNormalizePath(rawItem.FilePath, out var fullPath))
        {
            return false;
        }

        if (!TryDetermineTypeFromExtension(fullPath, out var expectedType))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rawItem.Type) &&
            !string.Equals(rawItem.Type.Trim(), expectedType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var displayName = string.IsNullOrWhiteSpace(rawItem.DisplayName)
            ? fileName
            : rawItem.DisplayName.Trim();

        var addedAtUtc = rawItem.AddedAtUtc == default
            ? DateTimeOffset.UtcNow
            : rawItem.AddedAtUtc.ToUniversalTime();

        normalizedItem = new WallpaperLibraryItem
        {
            Id = rawItem.Id.Trim(),
            Type = expectedType,
            FilePath = fullPath,
            DisplayName = displayName,
            AddedAtUtc = addedAtUtc
        };
        return true;
    }

    internal static bool TryNormalizePath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                fullPath = Path.GetFullPath(trimmed);
            }
            else
            {
                fullPath = Path.GetFullPath(trimmed);
            }

            return !string.IsNullOrWhiteSpace(Path.GetFileName(fullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
