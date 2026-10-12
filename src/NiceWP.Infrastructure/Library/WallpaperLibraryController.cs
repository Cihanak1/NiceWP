using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Read-only view projection of a <see cref="WallpaperLibraryItem"/> for UI presentation.
/// </summary>
public sealed record WallpaperLibraryEntryView(
    string Id,
    string DisplayName,
    string Type,
    string FilePath,
    DateTimeOffset AddedAtUtc,
    bool IsMissing)
{
    public string StatusText => IsMissing ? "Missing" : string.Empty;

    public bool CanApply => !IsMissing && WallpaperLibraryController.IsSupportedWallpaperType(Type);

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
            IsMissing: !item.FileExists());
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
    private readonly List<WallpaperLibraryEntryView> items = [];
    private readonly RuntimeIpcClient ipcClient;
    private readonly Func<CancellationToken, Task<IpcResponse>> ensureHostRunningAsync;

    public WallpaperLibraryService Service { get; }

    public RuntimeIpcClient IpcClient => ipcClient;

    public IReadOnlyList<WallpaperLibraryEntryView> Items => items;

    public string? SelectedItemId { get; private set; }

    public WallpaperLibraryEntryView? SelectedItem =>
        string.IsNullOrWhiteSpace(SelectedItemId)
            ? null
            : items.FirstOrDefault(item => string.Equals(item.Id, SelectedItemId, StringComparison.OrdinalIgnoreCase));

    public bool CanRemoveSelected => SelectedItem != null;

    public bool CanApplySelected =>
        SelectedItem is { IsMissing: false } selected &&
        IsSupportedWallpaperType(selected.Type);

    public WallpaperLibraryController(
        WallpaperLibraryService? service = null,
        RuntimeIpcClient? ipcClient = null,
        RuntimeHostLauncher? hostLauncher = null,
        Func<CancellationToken, Task<IpcResponse>>? ensureHostRunningAsync = null)
    {
        Service = service ?? new WallpaperLibraryService();
        this.ipcClient = ipcClient ?? hostLauncher?.IpcClient ?? new RuntimeIpcClient();
        var launcher = hostLauncher ?? new RuntimeHostLauncher(this.ipcClient);
        this.ensureHostRunningAsync = ensureHostRunningAsync
            ?? (cancellationToken => launcher.EnsureHostRunningAsync(cancellationToken: cancellationToken));
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

        var match = items.FirstOrDefault(item =>
            string.Equals(item.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        SelectedItemId = match?.Id;
    }

    /// <summary>
    /// Validates and adds a wallpaper file to the library, refreshes the projected list, and selects the item.
    /// </summary>
    public async Task<WallpaperLibraryActionResult> AddFileAsync(string? path, CancellationToken cancellationToken = default)
    {
        var addResult = await Service.AddAsync(path, cancellationToken).ConfigureAwait(false);
        if (!addResult.Success || addResult.Item == null)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: addResult.Message ?? "Failed to add wallpaper to library.",
                Entry: null,
                AlreadyExisted: false,
                Error: addResult.Error == WallpaperError.None ? WallpaperError.InvalidPath : addResult.Error);
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
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

        await LoadAsync(cancellationToken).ConfigureAwait(false);

        if (!removed)
        {
            return new WallpaperLibraryActionResult(
                Success: false,
                Message: "Selected wallpaper was not found in the library.",
                Error: WallpaperError.FileNotFound);
        }

        return new WallpaperLibraryActionResult(
            Success: true,
            Message: string.Empty,
            Error: WallpaperError.None);
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
            var projected = items.FirstOrDefault(entry =>
                string.Equals(entry.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
            if (projected == null)
            {
                if (string.Equals(SelectedItemId, trimmedId, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedItemId = null;
                }

                return new WallpaperLibraryApplyResult(
                    Success: false,
                    Message: "Selected wallpaper was not found in the library.",
                    Error: WallpaperError.FileNotFound);
            }

            item = new WallpaperLibraryItem
            {
                Id = projected.Id,
                DisplayName = projected.DisplayName,
                Type = projected.Type,
                FilePath = projected.FilePath,
                AddedAtUtc = projected.AddedAtUtc
            };
        }

        return await ApplyItemAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and applies a library item via Runtime Host, then synchronizes active status via GetStatus.
    /// Never modifies library.json or settings.json directly.
    /// </summary>
    public async Task<WallpaperLibraryApplyResult> ApplyItemAsync(
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
                Message: $"Unsupported wallpaper type: '{item.Type}'.",
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
                Message: validation.Error == WallpaperError.FileNotFound
                    ? "File not found."
                    : (validation.Message ?? "Invalid wallpaper file."),
                Entry: refreshedView,
                Error: validation.Error);
        }

        var ensureResponse = await ensureHostRunningAsync(cancellationToken).ConfigureAwait(false);
        if (!ensureResponse.Success)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: ensureResponse.Message ?? "Runtime host could not be started.",
                Entry: entryView,
                Error: WallpaperError.DesktopHostUnavailable);
        }

        var applyResponse = isVideo
            ? await ipcClient.SetVideoAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await ipcClient.SetImageAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!applyResponse.Success)
        {
            return new WallpaperLibraryApplyResult(
                Success: false,
                Message: applyResponse.Message ?? "Failed to apply wallpaper.",
                Entry: entryView,
                Error: WallpaperError.RendererFailed);
        }

        var expectedType = isVideo ? IpcConstants.WallpaperTypeVideo : IpcConstants.WallpaperTypeImage;
        var activeType = expectedType;
        var activePath = fullPath;
        var playbackState = IpcConstants.PlaybackStateRunning;
        var isPaused = false;

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

    private void RefreshProjectedEntry(WallpaperLibraryEntryView updatedEntry)
    {
        if (string.IsNullOrWhiteSpace(updatedEntry.Id))
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, updatedEntry.Id, StringComparison.OrdinalIgnoreCase))
            {
                items[i] = updatedEntry;
                break;
            }
        }
    }

    private void RebuildItems(WallpaperLibrary library)
    {
        var previousSelectedId = SelectedItemId;
        items.Clear();

        foreach (var item in library.Items)
        {
            items.Add(WallpaperLibraryEntryView.FromItem(item));
        }

        if (!string.IsNullOrWhiteSpace(previousSelectedId) &&
            items.Any(item => string.Equals(item.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedItemId = items.First(item =>
                string.Equals(item.Id, previousSelectedId, StringComparison.OrdinalIgnoreCase)).Id;
        }
        else
        {
            SelectedItemId = null;
        }
    }
}
