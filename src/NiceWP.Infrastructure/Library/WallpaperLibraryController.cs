using NiceWP.Core;

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
/// Coordinates local wallpaper library state, selection, add, and remove flows for the UI.
/// </summary>
public sealed class WallpaperLibraryController
{
    private readonly List<WallpaperLibraryEntryView> items = [];

    public WallpaperLibraryService Service { get; }

    public IReadOnlyList<WallpaperLibraryEntryView> Items => items;

    public string? SelectedItemId { get; private set; }

    public WallpaperLibraryEntryView? SelectedItem =>
        string.IsNullOrWhiteSpace(SelectedItemId)
            ? null
            : items.FirstOrDefault(item => string.Equals(item.Id, SelectedItemId, StringComparison.OrdinalIgnoreCase));

    public bool CanRemoveSelected => SelectedItem != null;

    public WallpaperLibraryController(WallpaperLibraryService? service = null)
    {
        Service = service ?? new WallpaperLibraryService();
    }

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
