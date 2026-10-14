using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Represents the result of attempting to add a wallpaper file to <see cref="WallpaperLibraryService"/>.
/// </summary>
public sealed record WallpaperLibraryAddResult(
    bool Success,
    WallpaperLibraryItem? Item = null,
    bool AlreadyExisted = false,
    WallpaperError Error = WallpaperError.None,
    string? Message = null);

/// <summary>
/// Represents the result of attempting to relink a wallpaper file in <see cref="WallpaperLibraryService"/>.
/// </summary>
public sealed record WallpaperLibraryRelinkResult(
    bool Success,
    WallpaperLibraryItem? Item = null,
    bool AlreadyExisted = false,
    WallpaperError Error = WallpaperError.None,
    string? Message = null);

/// <summary>
/// Represents the result of attempting to rename a wallpaper item in <see cref="WallpaperLibraryService"/>.
/// </summary>
public sealed record WallpaperLibraryRenameResult(
    bool Success,
    WallpaperLibraryItem? Item = null,
    WallpaperError Error = WallpaperError.None,
    string? Message = null);

/// <summary>
/// Provides resilient local JSON persistence and management for <see cref="WallpaperLibrary"/>
/// at %LocalAppData%\NiceWP\library.json.
/// </summary>
public sealed class WallpaperLibraryService
{
    public const string DefaultFolderName = "NiceWP";
    public const string DefaultFileName = "library.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public string FilePath { get; }

    public WallpaperLibraryService(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultLibraryPath()
            : Path.GetFullPath(filePath);
    }

    public static string GetDefaultLibraryPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultFolderName,
            DefaultFileName);

    /// <summary>
    /// Loads the wallpaper library from disk. Returns a safe empty Version 1 library if the file is missing,
    /// empty, or corrupt. Never deletes or overwrites a corrupt file during load, and preserves items whose
    /// physical files are currently missing on disk.
    /// </summary>
    public async Task<WallpaperLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes the wallpaper library atomically via a temporary file in the same directory,
    /// flushing to disk before replacing the target file.
    /// </summary>
    public async Task<bool> SaveAsync(WallpaperLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Validates and adds a local image or video wallpaper to the library.
    /// If the normalized path already exists in the library (case-insensitive), returns the existing item without creating a duplicate.
    /// </summary>
    public async Task<WallpaperLibraryAddResult> AddAsync(string? path, CancellationToken cancellationToken = default)
    {
        var validation = ValidateWallpaperFile(path, out var fullPath, out var wallpaperType);
        if (!validation.Success)
        {
            return new WallpaperLibraryAddResult(
                Success: false,
                Item: null,
                AlreadyExisted: false,
                Error: validation.Error,
                Message: validation.Message);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var existing = library.FindByPath(fullPath);
            if (existing != null)
            {
                return new WallpaperLibraryAddResult(
                    Success: true,
                    Item: existing,
                    AlreadyExisted: true);
            }

            var newItem = new WallpaperLibraryItem
            {
                Id = CreateUniqueId(library),
                Type = wallpaperType,
                FilePath = fullPath,
                DisplayName = Path.GetFileName(fullPath),
                AddedAtUtc = DateTimeOffset.UtcNow,
                IsFavorite = false,
                LastUsedAtUtc = null
            };

            library.Items.Add(newItem);
            var saved = await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
            if (!saved)
            {
                return new WallpaperLibraryAddResult(
                    Success: false,
                    Item: null,
                    AlreadyExisted: false,
                    Error: WallpaperError.RendererFailed,
                    Message: "Failed to persist wallpaper library.");
            }

            return new WallpaperLibraryAddResult(
                Success: true,
                Item: newItem,
                AlreadyExisted: false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes the library entry with the specified ID from library.json without deleting or modifying the physical file on disk.
    /// </summary>
    public async Task<bool> RemoveAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var removedCount = library.Items.RemoveAll(item =>
                string.Equals(item.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
            if (removedCount == 0)
            {
                return false;
            }

            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Relinks the specified library item to a new physical file path.
    /// Preserves the item's Id, DisplayName, AddedAtUtc, IsFavorite, and LastUsedAtUtc.
    /// Validates that media types match (Image to Image, Video to Video) and prevents duplicate paths.
    /// </summary>
    public async Task<WallpaperLibraryRelinkResult> RelinkAsync(
        string? id,
        string? newPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new WallpaperLibraryRelinkResult(
                Success: false,
                Error: WallpaperError.InvalidPath,
                Message: "Select a wallpaper in the library to relink.");
        }

        var validation = ValidateWallpaperFile(newPath, out var fullPath, out var newType);
        if (!validation.Success)
        {
            return new WallpaperLibraryRelinkResult(
                Success: false,
                Error: validation.Error,
                Message: validation.Message);
        }

        var trimmedId = id.Trim();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindById(trimmedId);
            if (item == null)
            {
                return new WallpaperLibraryRelinkResult(
                    Success: false,
                    Error: WallpaperError.FileNotFound,
                    Message: "File not found.");
            }

            if (!string.Equals(item.Type, newType, StringComparison.OrdinalIgnoreCase))
            {
                return new WallpaperLibraryRelinkResult(
                    Success: false,
                    Error: WallpaperError.UnsupportedFormat,
                    Message: "Unsupported wallpaper.");
            }

            var duplicate = library.FindByPath(fullPath);
            if (duplicate != null && !string.Equals(duplicate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                return new WallpaperLibraryRelinkResult(
                    Success: false,
                    AlreadyExisted: true,
                    Error: WallpaperError.InvalidPath,
                    Message: "File already exists in library.");
            }

            item.FilePath = fullPath;

            var saved = await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
            if (!saved)
            {
                return new WallpaperLibraryRelinkResult(
                    Success: false,
                    Error: WallpaperError.RendererFailed,
                    Message: "Failed to persist wallpaper library.");
            }

            return new WallpaperLibraryRelinkResult(
                Success: true,
                Item: item,
                Message: "File relinked.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Renames the display name of the specified library item without modifying the physical file on disk.
    /// </summary>
    public async Task<WallpaperLibraryRenameResult> RenameAsync(
        string? id,
        string? newDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new WallpaperLibraryRenameResult(
                Success: false,
                Error: WallpaperError.InvalidPath,
                Message: "Select a wallpaper in the library to rename.");
        }

        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            return new WallpaperLibraryRenameResult(
                Success: false,
                Error: WallpaperError.InvalidPath,
                Message: "Name cannot be empty.");
        }

        var trimmedName = newDisplayName.Trim();
        var trimmedId = id.Trim();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindById(trimmedId);
            if (item == null)
            {
                return new WallpaperLibraryRenameResult(
                    Success: false,
                    Error: WallpaperError.FileNotFound,
                    Message: "File not found.");
            }

            if (string.Equals(item.DisplayName, trimmedName, StringComparison.Ordinal))
            {
                return new WallpaperLibraryRenameResult(
                    Success: true,
                    Item: item,
                    Message: string.Empty);
            }

            item.DisplayName = trimmedName;

            var saved = await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
            if (!saved)
            {
                return new WallpaperLibraryRenameResult(
                    Success: false,
                    Error: WallpaperError.RendererFailed,
                    Message: "Failed to persist wallpaper library.");
            }

            return new WallpaperLibraryRenameResult(
                Success: true,
                Item: item,
                Message: string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Updates the favorite state for the specified library item and persists the change to library.json.
    /// </summary>
    public async Task<bool> SetFavoriteAsync(string? id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindById(trimmedId);
            if (item == null)
            {
                return false;
            }

            if (item.IsFavorite == isFavorite)
            {
                return true;
            }

            item.IsFavorite = isFavorite;
            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Toggles the favorite state for the specified library item and persists the change to library.json.
    /// </summary>
    public async Task<bool> ToggleFavoriteAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindById(trimmedId);
            if (item == null)
            {
                return false;
            }

            item.IsFavorite = !item.IsFavorite;
            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records the successful application timestamp for the specified library item and persists the change to library.json.
    /// </summary>
    public async Task<bool> RecordLastUsedAsync(string? id, DateTimeOffset? lastUsedAtUtc = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        var timestamp = (lastUsedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindById(trimmedId);
            if (item == null)
            {
                return false;
            }

            item.LastUsedAtUtc = timestamp;
            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records the successful application timestamp for a wallpaper matching the normalized path in library.json, if present.
    /// </summary>
    public async Task<bool> RecordLastUsedByPathAsync(string? path, DateTimeOffset? lastUsedAtUtc = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var timestamp = (lastUsedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var item = library.FindByPath(path);
            if (item == null)
            {
                return false;
            }

            item.LastUsedAtUtc = timestamp;
            return await SaveCoreAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resets the library to an empty Version 1 state and persists it to disk.
    /// </summary>
    public Task<bool> ResetAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(WallpaperLibrary.CreateDefault(), cancellationToken);

    public async Task<WallpaperLibraryItem?> FindByIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        var library = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return library.FindById(id);
    }

    public async Task<bool> ContainsPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        var library = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return library.ContainsPath(path);
    }

    public static WallpaperResult ValidateWallpaperFile(string? path, out string fullPath, out string wallpaperType)
    {
        fullPath = string.Empty;
        wallpaperType = IpcConstants.WallpaperTypeNone;

        if (string.IsNullOrWhiteSpace(path))
        {
            return new WallpaperResult(WallpaperError.InvalidPath, "Wallpaper file path cannot be empty.");
        }

        if (!WallpaperLibrary.TryDetermineTypeFromExtension(path.Trim(), out var candidateType))
        {
            try
            {
                _ = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new WallpaperResult(WallpaperError.InvalidPath, "The wallpaper path is invalid.");
            }

            return new WallpaperResult(WallpaperError.UnsupportedFormat, "Only PNG, JPEG, and MP4 wallpaper files are supported.");
        }

        if (string.Equals(candidateType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase))
        {
            var videoResult = VideoWallpaper.Validate(path.Trim(), out fullPath);
            if (videoResult.Success)
            {
                wallpaperType = IpcConstants.WallpaperTypeVideo;
            }
            return videoResult;
        }

        var imageResult = ImageWallpaper.Validate(path.Trim(), out fullPath);
        if (imageResult.Success)
        {
            wallpaperType = IpcConstants.WallpaperTypeImage;
        }
        return imageResult;
    }

    private async Task<WallpaperLibrary> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return WallpaperLibrary.CreateDefault();
            }

            var json = await File.ReadAllTextAsync(FilePath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return WallpaperLibrary.CreateDefault();
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return WallpaperLibrary.CreateDefault();
            }

            // If "Items" is explicitly present and not an array (e.g. null), treat as invalid and return empty library
            var hasItemsProperty = TryGetPropertyCaseInsensitive(document.RootElement, "Items", out var itemsElement);
            if (hasItemsProperty && itemsElement.ValueKind != JsonValueKind.Array)
            {
                return WallpaperLibrary.CreateDefault();
            }

            var version = WallpaperLibrary.CurrentVersion;
            if (TryGetPropertyCaseInsensitive(document.RootElement, "Version", out var versionElement))
            {
                if (versionElement.ValueKind != JsonValueKind.Number ||
                    !versionElement.TryGetInt32(out version) ||
                    version != WallpaperLibrary.CurrentVersion)
                {
                    return WallpaperLibrary.CreateDefault();
                }
            }

            var sanitizedItems = new List<WallpaperLibraryItem>();
            if (hasItemsProperty && itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in itemsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    try
                    {
                        var item = element.Deserialize<WallpaperLibraryItem>(JsonOptions);
                        if (item != null)
                        {
                            sanitizedItems.Add(item);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip individual malformed item while keeping valid items.
                    }
                }
            }

            var library = new WallpaperLibrary
            {
                Version = version,
                Items = sanitizedItems
            };

            return library.Normalize();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load wallpaper library from '{FilePath}': {ex}");
            return WallpaperLibrary.CreateDefault();
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private async Task<bool> SaveCoreAsync(WallpaperLibrary library, CancellationToken cancellationToken)
    {
        string? tempFilePath = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                Trace.WriteLine($"Cannot save wallpaper library: invalid directory for path '{FilePath}'.");
                return false;
            }

            Directory.CreateDirectory(directory);

            var normalized = library.Normalize();
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var bytes = Utf8NoBom.GetBytes(json);

            tempFilePath = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempFilePath, FilePath, overwrite: true);
            tempFilePath = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            CleanupTempFile(tempFilePath);
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save wallpaper library to '{FilePath}': {ex}");
            CleanupTempFile(tempFilePath);
            return false;
        }
    }

    private static string CreateUniqueId(WallpaperLibrary library)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (library.FindById(id) != null);

        return id;
    }

    private static void CleanupTempFile(string? tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (Exception cleanupEx)
        {
            Trace.WriteLine($"Failed to clean up temporary library file '{tempFilePath}': {cleanupEx}");
        }
    }
}
