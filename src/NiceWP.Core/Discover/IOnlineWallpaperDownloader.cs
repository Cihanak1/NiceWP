namespace NiceWP.Core.Discover;

/// <summary>
/// Result of an online wallpaper download and library import operation.
/// </summary>
public sealed record OnlineDownloadResult(
    bool Success,
    bool AlreadyExisted = false,
    string? LocalFilePath = null,
    string? ErrorMessage = null,
    object? LibraryItem = null);

/// <summary>
/// Defines the contract for securely downloading and importing online open-license wallpapers.
/// </summary>
public interface IOnlineWallpaperDownloader
{
    Task<OnlineDownloadResult> DownloadAndImportAsync(
        OnlineWallpaperItem onlineItem,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
