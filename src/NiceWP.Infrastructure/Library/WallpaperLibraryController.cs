using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Specifies the active filter for library presentation.
/// </summary>
public enum WallpaperLibraryFilter
{
    All,
    Images,
    Videos,
    Favorites,
    Recent
}

/// <summary>
/// Specifies the sort order for library presentation.
/// </summary>
public enum WallpaperLibrarySort
{
    Newest,
    Oldest,
    NameAsc,
    NameDesc
}

/// <summary>
/// Read-only view projection of a <see cref="WallpaperLibraryItem"/> for UI presentation.
/// </summary>
public sealed record WallpaperLibraryEntryView(
    string Id,
    string DisplayName,
    string Type,
    string FilePath,
    DateTimeOffset AddedAtUtc,
    bool IsMissing,
    bool IsFavorite = false,
    DateTimeOffset? LastUsedAtUtc = null)
{
    public string StatusText => IsMissing ? "Missing" : string.Empty;

    public bool CanApply => !IsMissing && WallpaperLibraryController.IsSupportedWallpaperType(Type);

    public bool CanRemove => true;

    public bool CanLocate => IsMissing;

    public bool CanOpenLocation => !IsMissing;

    public bool IsImage => string.Equals(Type, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase);

    public bool IsVideo => string.Equals(Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

    public string ToolTipText =>
        IsMissing
            ? $"{DisplayName}\n{FilePath}\nStatus: File not found on disk"
            : $"{DisplayName}\n{FilePath}";

    public string DisplayLabel =>
        IsMissing
            ? $"{DisplayName} [{Type}] - Missing"
            : $"{DisplayName} [{Type}]";

    public override string ToString() => DisplayLabel;

    public static WallpaperLibraryEntryView FromItem(WallpaperLibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var displayName = string.IsNullOrWhiteSpace(item.DisplayName)
            ? Path.GetFileName(item.FilePath)
            : item.DisplayName;

        return new WallpaperLibraryEntryView(
            Id: item.Id,
            DisplayName: displayName,
            Type: item.Type,
            FilePath: item.FilePath,
            AddedAtUtc: item.AddedAtUtc,
            IsMissing: !item.FileExists(),
            IsFavorite: item.IsFavorite,
            LastUsedAtUtc: item.LastUsedAtUtc);
    }
}

/// <summary>
/// Represents the result of a user-initiated library management operation.
/// </summary>
public sealed record WallpaperLibraryActionResult(
    bool Success,
    string? Message = null,
    WallpaperLibraryEntryView? Entry = null,
    bool AlreadyExisted = false,
    WallpaperError Error = WallpaperError.None);

/// <summary>
/// Represents the result of applying a library entry as the active wallpaper via Runtime Host.
/// </summary>
public sealed record WallpaperLibraryApplyResult(
    bool Success,
    string? Message = null,
    WallpaperLibraryEntryView? Entry = null,
    WallpaperError Error = WallpaperError.None,
    string ActiveWallpaperType = IpcConstants.WallpaperTypeNone,
    string? ActiveWallpaperPath = null,
    string PlaybackState = IpcConstants.PlaybackStateNone,
    bool IsPaused = false);

/// <summary>
/// Coordinates local wallpaper library state, selection, add, remove, and apply flows for the UI.
/// </summary>
public sealed class WallpaperLibraryController
{
    public const int MaxRecentItems = 8;

    private readonly List<WallpaperLibraryEntryView> allItems = [];
    private readonly List<WallpaperLibraryEntryView> visibleItems = [];
    private readonly RuntimeIpcClient ipcClient;
    private readonly Func<CancellationToken, Task<IpcResponse>> ensureHostRunningAsync;
    private readonly Action<string> openFileLocationAction;
    private int busyFlag;

    public WallpaperLibraryService Service { get; }

    public RuntimeIpcClient IpcClient => ipcClient;

    public IReadOnlyList<WallpaperLibraryEntryView> Items => visibleItems;

    public IReadOnlyList<WallpaperLibraryEntryView> AllItems => allItems;

    public string? SelectedItemId { get; private set; }

    public bool IsBusy => Volatile.Read(ref busyFlag) != 0;

    public string SearchText { get; private set; } = string.Empty;

    public WallpaperLibraryFilter Filter { get; private set; } = WallpaperLibraryFilter.All;

    public WallpaperLibrarySort Sort { get; private set; } = WallpaperLibrarySort.Newest;

    public WallpaperLibraryEntryView? SelectedItem =>
        string.IsNullOrWhiteSpace(SelectedItemId)
            ? null
            : visibleItems.FirstOrDefault(item => string.Equals(item.Id, SelectedItemId, StringComparison.OrdinalIgnoreCase));

    public bool CanRemoveSelected => !IsBusy && SelectedItem is { CanRemove: true };

    public bool CanApplySelected => !IsBusy && SelectedItem is { CanApply: true };

    public WallpaperLibraryController(
        WallpaperLibraryService? service = null,
        RuntimeIpcClient? ipcClient = null,
        RuntimeHostLauncher? hostLauncher = null,
        Func<CancellationToken, Task<IpcResponse>>? ensureHostRunningAsync = null,
        Action<string>? openFileLocationAction = null)
    {
        Service = service ?? new WallpaperLibraryService();
        this.ipcClient = ipcClient ?? hostLauncher?.IpcClient ?? new RuntimeIpcClient();
        var launcher = hostLauncher ?? new RuntimeHostLauncher(this.ipcClient);
        this.ensureHostRunningAsync = ensureHostRunningAsync
            ?? (cancellationToken => launcher.EnsureHostRunningAsync(cancellationToken: cancellationToken));
        this.openFileLocationAction = openFileLocationAction ?? DefaultOpenFileLocation;
    }

    private static void DefaultOpenFileLocation(string fullPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{fullPath}\"",
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }

    public void SetSearchText(string? searchText)
    {
        SearchText = searchText?.Trim() ?? string.Empty;
        ApplyFilterAndSort();
    }

    public void SetFilter(WallpaperLibraryFilter filter)
    {
        Filter = filter;
        ApplyFilterAndSort();
    }

    public void SetSort(WallpaperLibrarySort sort)
    {
        Sort = sort;
        ApplyFilterAndSort();
    }

    public async Task<bool> ToggleFavoriteAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        var entry = allItems.FirstOrDefault(item => string.Equals(item.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return false;
        }

        var newFavorite = !entry.IsFavorite;
        var updated = await Service.SetFavoriteAsync(trimmedId, newFavorite, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            for (var i = 0; i < allItems.Count; i++)
            {
                if (string.Equals(allItems[i].Id, trimmedId, StringComparison.OrdinalIgnoreCase))
                {
                    allItems[i] = allItems[i] with { IsFavorite = newFavorite };
                    break;
                }
            }

            ApplyFilterAndSort();
        }

        return updated;
    }

    public async Task<bool> SetFavoriteAsync(string? id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        var updated = await Service.SetFavoriteAsync(trimmedId, isFavorite, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            for (var i = 0; i < allItems.Count; i++)
            {
                if (string.Equals(allItems[i].Id, trimmedId, StringComparison.OrdinalIgnoreCase))
                {
                    allItems[i] = allItems[i] with { IsFavorite = isFavorite };
                    break;
                }
            }

            ApplyFilterAndSort();
        }

        return updated;
    }

    public async Task<bool> RecordLastUsedAsync(string? id, DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmedId = id.Trim();
        var now = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var updated = await Service.RecordLastUsedAsync(trimmedId, now, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            for (var i = 0; i < allItems.Count; i++)
            {
                if (string.Equals(allItems[i].Id, trimmedId, StringComparison.OrdinalIgnoreCase))
                {
                    allItems[i] = allItems[i] with { LastUsedAtUtc = now };
                    break;
                }
            }

            ApplyFilterAndSort();
        }

        return updated;
    }

    public async Task<bool> RecordLastUsedByPathAsync(string? path, DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var now = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var updated = await Service.RecordLastUsedByPathAsync(path, now, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            for (var i = 0; i < allItems.Count; i++)
            {
                if (string.Equals(allItems[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    allItems[i] = allItems[i] with { LastUsedAtUtc = now };
                    break;
                }
            }

            ApplyFilterAndSort();
        }

        return updated;
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<WallpaperLibraryEntryView> query = allItems;

        query = Filter switch
        {
            WallpaperLibraryFilter.Images => query.Where(item => item.IsImage),
            WallpaperLibraryFilter.Videos => query.Where(item => item.IsVideo),
            WallpaperLibraryFilter.Favorites => query.Where(item => item.IsFavorite),
            WallpaperLibraryFilter.Recent => query.Where(item => item.LastUsedAtUtc.HasValue),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(item =>
                item.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        if (Filter == WallpaperLibraryFilter.Recent)
        {
            query = query
                .OrderByDescending(item => item.LastUsedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.AddedAtUtc)
                .Take(MaxRecentItems);
        }
        else
        {
            query = Sort switch
            {
                WallpaperLibrarySort.Oldest => query
                    .OrderBy(item => item.AddedAtUtc)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
                WallpaperLibrarySort.NameAsc => query
                    .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.AddedAtUtc),
                WallpaperLibrarySort.NameDesc => query
                    .OrderByDescending(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.AddedAtUtc),
                _ => query // Default: Newest
                    .OrderByDescending(item => item.AddedAtUtc)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            };
        }

        visibleItems.Clear();
        visibleItems.AddRange(query);
    }

    public static bool IsSupportedWallpaperType(string? type) =>
        string.Equals(type?.Trim(), IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type?.Trim(), IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the wallpaper library from storage and refreshes the projected item list without duplicates.
    /// Safe to call multiple times; never throws on missing or corrupt library.json.
    /// </summary>
    public async Task<IReadOnlyList<WallpaperLibraryEntryView>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var acquired = Interlocked.CompareExchange(ref busyFlag, 1, 0) == 0;
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                Interlocked.Exchange(ref busyFlag, 0);
            }
        }
    }

    private async Task<IReadOnlyList<WallpaperLibraryEntryView>> LoadCoreAsync(CancellationToken cancellationToken = default)
    {
        var library = await Service.LoadAsync(cancellationToken).ConfigureAwait(false);
        RebuildItems(library);
        return Items;
    }

    /// <summary>
    /// Selects a library entry by ID, or clears the selection when null/unknown.
    /// </summary>
    public void SelectItem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            SelectedItemId = null;
            return;
        }

        var match = allItems.FirstOrDefault(item =>
            string.Equals(item.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        SelectedItemId = match?.Id;
    }

    /// <summary>
    /// Validates and adds a wallpaper file to the library, refreshes the projected list, and selects the item.
    /// </summary>
    public async Task<WallpaperLibraryActionResult> AddFileAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                AlreadyExisted: false,
                Error: WallpaperError.Busy);
        }

        try
        {
            var addResult = await Service.AddAsync(path, cancellationToken).ConfigureAwait(false);
            if (!addResult.Success || addResult.Item == null)
            {
                var error = addResult.Error == WallpaperError.None ? WallpaperError.InvalidPath : addResult.Error;
                return new WallpaperLibraryActionResult(
                    Success: false,
                    Message: MapUserStatusMessage(error, addResult.Message),
                    Entry: null,
                    AlreadyExisted: false,
                    Error: error);
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            SelectItem(addResult.Item.Id);

            var entry = SelectedItem ?? WallpaperLibraryEntryView.FromItem(addResult.Item);
            var message = addResult.AlreadyExisted
                ? $"Wallpaper is already in library: {entry.DisplayName}"
                : $"Added to library: {entry.DisplayName}";

            return new WallpaperLibraryActionResult(
                Success: true,
                Message: message,
                Entry: entry,
                AlreadyExisted: addResult.AlreadyExisted,
                Error: WallpaperError.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to add wallpaper to library: {ex}");
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Failed to load library.",
                Entry: null,
                AlreadyExisted: false,
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    /// <summary>
    /// Removes the currently selected entry from the library without deleting the physical file on disk.
    /// </summary>
    public Task<WallpaperLibraryActionResult> RemoveSelectedAsync(CancellationToken cancellationToken = default) =>
        RemoveByIdAsync(SelectedItemId, cancellationToken);

    /// <summary>
    /// Removes the entry with the specified ID from the library without deleting the physical file on disk.
    /// </summary>
    public async Task<WallpaperLibraryActionResult> RemoveByIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                AlreadyExisted: false,
                Error: WallpaperError.Busy);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new WallpaperLibraryActionResult(
                    Success: false,
                    Message: "Select a wallpaper in the library to remove.",
                    Error: WallpaperError.InvalidPath);
            }

            var trimmedId = id.Trim();
            var removed = await Service.RemoveAsync(trimmedId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(SelectedItemId, trimmedId, StringComparison.OrdinalIgnoreCase))
            {
                SelectedItemId = null;
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);

            if (!removed)
            {
                return new WallpaperLibraryActionResult(
                    Success: false,
                    Message: "File not found.",
                    Error: WallpaperError.FileNotFound);
            }

            return new WallpaperLibraryActionResult(
                Success: true,
                Message: string.Empty,
                Error: WallpaperError.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to remove wallpaper from library: {ex}");
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Failed to load library.",
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    /// <summary>
    /// Opens Windows Explorer with the physical file of the specified library item selected.
    /// If the file is missing, does not launch Explorer and returns an instructive error message.
    /// </summary>
    public WallpaperLibraryActionResult OpenFileLocation(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Select a wallpaper in the library.",
                Error: WallpaperError.InvalidPath);
        }

        var trimmedId = id.Trim();
        var entry = allItems.FirstOrDefault(item => string.Equals(item.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "File not found.",
                Error: WallpaperError.FileNotFound);
        }

        if (entry.IsMissing || !File.Exists(entry.FilePath))
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "File not found. Locate file to relink.",
                Entry: entry,
                Error: WallpaperError.FileNotFound);
        }

        try
        {
            openFileLocationAction(entry.FilePath);
            return new WallpaperLibraryActionResult(
                Success: true,
                Entry: entry,
                Error: WallpaperError.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to open file location for '{entry.FilePath}': {ex}");
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Failed to open file location.",
                Entry: entry,
                Error: WallpaperError.RendererFailed);
        }
    }

    /// <summary>
    /// Relinks the currently selected library entry to a new physical file path.
    /// </summary>
    public Task<WallpaperLibraryActionResult> RelinkSelectedAsync(string? newPath, CancellationToken cancellationToken = default) =>
        RelinkAsync(SelectedItemId, newPath, cancellationToken);

    /// <summary>
    /// Relinks the specified library entry to a new physical file path, refreshes the projected list, and keeps selection.
    /// </summary>
    public async Task<WallpaperLibraryActionResult> RelinkAsync(
        string? id,
        string? newPath,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                Error: WallpaperError.Busy);
        }

        try
        {
            var relinkResult = await Service.RelinkAsync(id, newPath, cancellationToken).ConfigureAwait(false);
            if (!relinkResult.Success || relinkResult.Item == null)
            {
                var message = !string.IsNullOrWhiteSpace(relinkResult.Message)
                    ? relinkResult.Message
                    : MapUserStatusMessage(relinkResult.Error);

                return new WallpaperLibraryActionResult(
                    Success: false,
                    Message: message,
                    Entry: null,
                    AlreadyExisted: relinkResult.AlreadyExisted,
                    Error: relinkResult.Error);
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            SelectItem(relinkResult.Item.Id);

            var entry = SelectedItem ?? WallpaperLibraryEntryView.FromItem(relinkResult.Item);
            return new WallpaperLibraryActionResult(
                Success: true,
                Message: $"File relinked: {entry.DisplayName}",
                Entry: entry,
                Error: WallpaperError.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to relink wallpaper: {ex}");
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Failed to relink file.",
                Entry: null,
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    /// <summary>
    /// Renames the currently selected library entry's display name without modifying the physical file on disk.
    /// </summary>
    public Task<WallpaperLibraryActionResult> RenameSelectedAsync(string? newDisplayName, CancellationToken cancellationToken = default) =>
        RenameAsync(SelectedItemId, newDisplayName, cancellationToken);

    /// <summary>
    /// Renames the specified library entry's display name without modifying the physical file on disk,
    /// refreshes the projected list, and keeps selection.
    /// </summary>
    public async Task<WallpaperLibraryActionResult> RenameAsync(
        string? id,
        string? newDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                Error: WallpaperError.Busy);
        }

        try
        {
            var renameResult = await Service.RenameAsync(id, newDisplayName, cancellationToken).ConfigureAwait(false);
            if (!renameResult.Success || renameResult.Item == null)
            {
                var message = !string.IsNullOrWhiteSpace(renameResult.Message)
                    ? renameResult.Message
                    : MapUserStatusMessage(renameResult.Error);

                return new WallpaperLibraryActionResult(
                    Success: false,
                    Message: message,
                    Entry: null,
                    Error: renameResult.Error);
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            SelectItem(renameResult.Item.Id);

            var entry = SelectedItem ?? WallpaperLibraryEntryView.FromItem(renameResult.Item);
            return new WallpaperLibraryActionResult(
                Success: true,
                Message: $"Renamed wallpaper: {entry.DisplayName}",
                Entry: entry,
                Error: WallpaperError.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to rename wallpaper: {ex}");
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Failed to rename wallpaper.",
                Entry: null,
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    /// <summary>
    /// Applies the currently selected library entry as the active wallpaper via Runtime Host.
    /// </summary>
    public Task<WallpaperLibraryApplyResult> ApplySelectedAsync(CancellationToken cancellationToken = default) =>
        ApplyByIdAsync(SelectedItemId, cancellationToken);

    /// <summary>
    /// Applies the library entry with the specified ID as the active wallpaper via Runtime Host.
    /// </summary>
    public async Task<WallpaperLibraryApplyResult> ApplyByIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                Error: WallpaperError.Busy);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new WallpaperLibraryApplyResult(
                    Success: false,
                    Message: "Select a wallpaper in the library to apply.",
                    Error: WallpaperError.InvalidPath);
            }

            var trimmedId = id.Trim();
            var item = await Service.FindByIdAsync(trimmedId, cancellationToken).ConfigureAwait(false);
            if (item == null)
            {
                var projected = allItems.FirstOrDefault(entry =>
                    string.Equals(entry.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
                if (projected == null)
                {
                    if (string.Equals(SelectedItemId, trimmedId, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedItemId = null;
                    }

                    return new WallpaperLibraryApplyResult(
                        Success: false,
                        Message: "File not found.",
                        Error: WallpaperError.FileNotFound);
                }

                item = new WallpaperLibraryItem
                {
                    Id = projected.Id,
                    DisplayName = projected.DisplayName,
                    Type = projected.Type,
                    FilePath = projected.FilePath,
                    AddedAtUtc = projected.AddedAtUtc,
                    IsFavorite = projected.IsFavorite,
                    LastUsedAtUtc = projected.LastUsedAtUtc
                };
            }

            return await ApplyItemCoreAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to apply wallpaper by ID: {ex}");
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Failed to apply wallpaper.",
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    /// <summary>
    /// Validates and applies a library item via Runtime Host, then synchronizes active status via GetStatus.
    /// Never modifies library.json or settings.json directly.
    /// </summary>
    public async Task<WallpaperLibraryApplyResult> ApplyItemAsync(
        WallpaperLibraryItem? item,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: MapUserStatusMessage(WallpaperError.Busy),
                Entry: null,
                Error: WallpaperError.Busy);
        }

        try
        {
            return await ApplyItemCoreAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to apply wallpaper item: {ex}");
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Failed to apply wallpaper.",
                Error: WallpaperError.RendererFailed);
        }
        finally
        {
            Interlocked.Exchange(ref busyFlag, 0);
        }
    }

    private async Task<WallpaperLibraryApplyResult> ApplyItemCoreAsync(
        WallpaperLibraryItem? item,
        CancellationToken cancellationToken = default)
    {
        if (item == null)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Select a wallpaper in the library to apply.",
                Error: WallpaperError.InvalidPath);
        }

        var entryView = WallpaperLibraryEntryView.FromItem(item);
        RefreshProjectedEntry(entryView);

        var trimmedType = item.Type?.Trim() ?? string.Empty;
        var isImage = string.Equals(trimmedType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase);
        var isVideo = string.Equals(trimmedType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

        if (!isImage && !isVideo)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Unsupported wallpaper.",
                Entry: entryView,
                Error: WallpaperError.UnsupportedFormat);
        }

        if (!item.FileExists())
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "File not found.",
                Entry: entryView,
                Error: WallpaperError.FileNotFound);
        }

        var validation = isVideo
            ? VideoWallpaper.Validate(item.FilePath, out var fullPath)
            : ImageWallpaper.Validate(item.FilePath, out fullPath);

        if (!validation.Success)
        {
            var refreshedView = WallpaperLibraryEntryView.FromItem(item);
            RefreshProjectedEntry(refreshedView);

            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: MapUserStatusMessage(validation.Error, validation.Message),
                Entry: refreshedView,
                Error: validation.Error);
        }

        IpcResponse ensureResponse;
        try
        {
            ensureResponse = await ensureHostRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to ensure runtime host is running: {ex}");
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Runtime unavailable.",
                Entry: entryView,
                Error: WallpaperError.DesktopHostUnavailable);
        }

        if (!ensureResponse.Success)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Runtime unavailable.",
                Entry: entryView,
                Error: WallpaperError.DesktopHostUnavailable);
        }

        if (!File.Exists(fullPath))
        {
            var missingView = new WallpaperLibraryEntryView(
                Id: entryView.Id,
                DisplayName: entryView.DisplayName,
                Type: entryView.Type,
                FilePath: entryView.FilePath,
                AddedAtUtc: entryView.AddedAtUtc,
                IsMissing: true);
            RefreshProjectedEntry(missingView);

            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "File not found.",
                Entry: missingView,
                Error: WallpaperError.FileNotFound);
        }

        IpcResponse applyResponse;
        try
        {
            applyResponse = isVideo
                ? await ipcClient.SetVideoAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await ipcClient.SetImageAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to send wallpaper command to runtime host: {ex}");
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: "Runtime unavailable.",
                Entry: entryView,
                Error: WallpaperError.DesktopHostUnavailable);
        }

        if (!applyResponse.Success)
        {
            var failureError = MapApplyFailureError(applyResponse.Message);
            var failureEntry = entryView;
            if (failureError == WallpaperError.FileNotFound || !File.Exists(fullPath))
            {
                failureError = WallpaperError.FileNotFound;
                failureEntry = new WallpaperLibraryEntryView(
                    Id: entryView.Id,
                    DisplayName: entryView.DisplayName,
                    Type: entryView.Type,
                    FilePath: entryView.FilePath,
                    AddedAtUtc: entryView.AddedAtUtc,
                    IsMissing: true);
                RefreshProjectedEntry(failureEntry);
            }

            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: MapUserStatusMessage(failureError, applyResponse.Message),
                Entry: failureEntry,
                Error: failureError);
        }

        var expectedType = isVideo ? IpcConstants.WallpaperTypeVideo : IpcConstants.WallpaperTypeImage;
        var activeType = expectedType;
        var activePath = fullPath;
        var playbackState = IpcConstants.PlaybackStateRunning;
        var isPaused = false;

        var applyTimestamp = DateTimeOffset.UtcNow;
        _ = await Service.RecordLastUsedAsync(item.Id, applyTimestamp, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < allItems.Count; i++)
        {
            if (string.Equals(allItems[i].Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                allItems[i] = allItems[i] with { LastUsedAtUtc = applyTimestamp };
                break;
            }
        }
        ApplyFilterAndSort();
        entryView = entryView with { LastUsedAtUtc = applyTimestamp };

        try
        {
            var statusResponse = await ipcClient.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (statusResponse.Success && statusResponse.HasActiveWallpaper && !string.IsNullOrWhiteSpace(statusResponse.ActivePath))
            {
                activeType = string.IsNullOrWhiteSpace(statusResponse.WallpaperType)
                    ? expectedType
                    : statusResponse.WallpaperType;
                activePath = statusResponse.ActivePath;
                playbackState = string.IsNullOrWhiteSpace(statusResponse.PlaybackState)
                    ? IpcConstants.PlaybackStateRunning
                    : statusResponse.PlaybackState;
                isPaused = statusResponse.IsPaused;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to query status after applying wallpaper: {ex}");
        }

        return new WallpaperLibraryApplyResult(
            Success: true,
            Message: string.Empty,
            Entry: entryView,
            Error: WallpaperError.None,
            ActiveWallpaperType: activeType,
            ActiveWallpaperPath: activePath,
            PlaybackState: playbackState,
            IsPaused: isPaused);
    }

    public static string MapUserStatusMessage(WallpaperError error, string? rawMessage = null)
    {
        if (error == WallpaperError.None && !string.IsNullOrWhiteSpace(rawMessage))
        {
            error = MapApplyFailureError(rawMessage);
        }

        return error switch
        {
            WallpaperError.DesktopHostUnavailable => "Runtime unavailable.",
            WallpaperError.FileNotFound => "File not found.",
            WallpaperError.UnsupportedFormat => "Unsupported wallpaper.",
            WallpaperError.RendererFailed => "Failed to apply wallpaper.",
            WallpaperError.Busy => "Wallpaper operation in progress.",
            WallpaperError.InvalidPath => !string.IsNullOrWhiteSpace(rawMessage) && !LooksLikeTechnicalException(rawMessage)
                ? rawMessage
                : "Select a valid wallpaper file.",
            _ => "Failed to apply wallpaper."
        };
    }

    public static string MapIpcFailureStatusMessage(string? rawMessage, string fallbackMessage = "Runtime unavailable.")
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return fallbackMessage;
        }

        var mappedError = MapApplyFailureError(rawMessage);
        return mappedError switch
        {
            WallpaperError.DesktopHostUnavailable => "Runtime unavailable.",
            WallpaperError.FileNotFound => "File not found.",
            WallpaperError.UnsupportedFormat => "Unsupported wallpaper.",
            _ => fallbackMessage
        };
    }

    private static bool LooksLikeTechnicalException(string message) =>
        message.Contains("Exception", StringComparison.Ordinal) ||
        message.Contains("   at ", StringComparison.Ordinal) ||
        message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase);

    private static WallpaperError MapApplyFailureError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return WallpaperError.RendererFailed;
        }

        if (message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("could not be started", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed to start", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("executable", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return WallpaperError.DesktopHostUnavailable;
        }

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperError.FileNotFound;
        }

        if (message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return WallpaperError.UnsupportedFormat;
        }

        return WallpaperError.RendererFailed;
    }

    private void RefreshProjectedEntry(WallpaperLibraryEntryView updatedEntry)
    {
        if (string.IsNullOrWhiteSpace(updatedEntry.Id))
        {
            return;
        }

        for (var i = 0; i < allItems.Count; i++)
        {
            if (string.Equals(allItems[i].Id, updatedEntry.Id, StringComparison.OrdinalIgnoreCase))
            {
                allItems[i] = updatedEntry;
                break;
            }
        }

        ApplyFilterAndSort();
    }

    private void RebuildItems(WallpaperLibrary library)
    {
        var previousSelectedId = SelectedItemId;
        allItems.Clear();

        foreach (var item in library.Items)
        {
            allItems.Add(WallpaperLibraryEntryView.FromItem(item));
        }

        ApplyFilterAndSort();

        if (!string.IsNullOrWhiteSpace(previousSelectedId) &&
            allItems.Any(item => string.Equals(item.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedItemId = allItems.First(item =>
                string.Equals(item.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase)).Id;
        }
        else
        {
            SelectedItemId = null;
        }
    }
}
