using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using NiceWP.Core.Discover;
using NiceWP.Infrastructure.Analysis;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Remote;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Securely downloads and imports online open-license wallpapers into NiceWP's managed library.
/// Enforces HTTPS-only, SSRF prevention, stream size bounds, magic bytes validation, and atomic storage.
/// </summary>
public sealed class OnlineWallpaperDownloader : IOnlineWallpaperDownloader
{
    public const long MaxImageSizeBytes = 100 * 1024 * 1024; // 100 MB
    public const long MaxVideoSizeBytes = 500 * 1024 * 1024; // 500 MB

    private readonly HttpClient _httpClient;
    private readonly WallpaperLibraryService _libraryService;
    private readonly WallpaperAnalysisService? _analysisService;
    private readonly WallpaperThumbnailService? _thumbnailService;
    private readonly string _baseOnlineDirectory;

    public OnlineWallpaperDownloader(
        WallpaperLibraryService libraryService,
        WallpaperAnalysisService? analysisService = null,
        WallpaperThumbnailService? thumbnailService = null,
        HttpClient? httpClient = null,
        string? baseOnlineDirectory = null)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _analysisService = analysisService;
        _thumbnailService = thumbnailService;
        _httpClient = httpClient ?? new HttpClient();

        _baseOnlineDirectory = baseOnlineDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NiceWP",
            "Library",
            "Online");
    }

    public async Task<OnlineDownloadResult> DownloadAndImportAsync(
        OnlineWallpaperItem onlineItem,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (onlineItem == null)
        {
            return new OnlineDownloadResult(false, ErrorMessage: "Online item is null.");
        }

        // 1. License Revalidation
        var licenseInfo = WallpaperLicensePolicy.Evaluate(onlineItem.LicenseCode, onlineItem.LicenseUrl, onlineItem.IsPublicDomain);
        if (!WallpaperLicensePolicy.IsDefaultDiscoverAllowed(licenseInfo))
        {
            return new OnlineDownloadResult(false, ErrorMessage: $"License '{licenseInfo.Name}' is not permitted for download.");
        }

        // 2. Duplicate Detection
        var currentLibrary = await _libraryService.LoadAsync(cancellationToken);
        var existingItem = currentLibrary.FindByOrigin(onlineItem.ProviderId, onlineItem.RemoteId);
        if (existingItem != null && existingItem.FileExists())
        {
            progress?.Report(new OnlineDownloadProgress("Already in Library", 0, 0, 100));
            return new OnlineDownloadResult(
                Success: true,
                AlreadyExisted: true,
                LocalFilePath: existingItem.FilePath,
                LibraryItem: existingItem);
        }

        // 3. SSRF & Scheme Validation
        if (!NetworkSafetyValidator.ValidateUrl(onlineItem.DownloadUrl, out var downloadUri, out var urlError))
        {
            return new OnlineDownloadResult(false, ErrorMessage: $"Download URL rejected: {urlError}");
        }

        if (!string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new OnlineDownloadResult(false, ErrorMessage: "Only HTTPS download URLs are permitted.");
        }

        // 4. Resolve Target Paths & Safe Filename
        var providerDir = Path.Combine(_baseOnlineDirectory, SanitizeProviderName(onlineItem.ProviderId));
        var tempDir = Path.Combine(_baseOnlineDirectory, "temp");

        try
        {
            Directory.CreateDirectory(providerDir);
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            return new OnlineDownloadResult(false, ErrorMessage: $"Failed to create download directory: {ex.Message}");
        }

        var isVideo = string.Equals(onlineItem.MediaType, "video", StringComparison.OrdinalIgnoreCase);
        var maxSize = isVideo ? MaxVideoSizeBytes : MaxImageSizeBytes;

        // Verify available disk space before starting transfer
        if (!HasSufficientDiskSpace(_baseOnlineDirectory, maxSize + (50 * 1024 * 1024)))
        {
            return new OnlineDownloadResult(false, ErrorMessage: "Insufficient disk space available on drive to safely download wallpaper.");
        }

        var tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tmp");

        try
        {
            // 5. Download with streaming, redirect safety, and size bounding
            progress?.Report(new OnlineDownloadProgress("Downloading", 0, 0, 0));

            var downloadSuccess = await DownloadStreamWithSsrfCheckAsync(
                downloadUri,
                tempFilePath,
                maxSize,
                progress,
                cancellationToken);

            if (!downloadSuccess.Success)
            {
                TryDeleteFile(tempFilePath);
                return new OnlineDownloadResult(false, ErrorMessage: downloadSuccess.ErrorMessage);
            }

            // 6. Magic Bytes Validation
            progress?.Report(new OnlineDownloadProgress("Validating", 0, 0, 95));
            var detectedType = MediaSignatureDetector.Detect(tempFilePath);
            if (detectedType == DetectedMediaType.Unknown)
            {
                TryDeleteFile(tempFilePath);
                return new OnlineDownloadResult(false, ErrorMessage: "Downloaded file failed media signature verification (unrecognized or unsafe format).");
            }

            var extension = detectedType switch
            {
                DetectedMediaType.Png => ".png",
                DetectedMediaType.Jpeg => ".jpg",
                DetectedMediaType.Mp4 => ".mp4",
                _ => isVideo ? ".mp4" : ".jpg"
            };

            var finalFileName = GenerateSafeFileName(onlineItem.ProviderId, onlineItem.RemoteId, onlineItem.Title, extension);
            var finalDestinationPath = Path.Combine(providerDir, finalFileName);

            // 7. Atomic Move into Managed Online Directory
            File.Move(tempFilePath, finalDestinationPath, overwrite: true);

            // 8. Register in WallpaperLibrary
            progress?.Report(new OnlineDownloadProgress("Importing", 0, 0, 98));

            var libraryItemType = detectedType == DetectedMediaType.Mp4
                ? IpcConstants.WallpaperTypeVideo
                : IpcConstants.WallpaperTypeImage;

            var origin = new WallpaperOrigin
            {
                SourceType = WallpaperOrigin.SourceTypeOnline,
                Provider = onlineItem.ProviderId,
                RemoteId = onlineItem.RemoteId,
                OriginalUrl = onlineItem.SourceUrl,
                Creator = onlineItem.CreatorName,
                CreatorUrl = onlineItem.CreatorUrl,
                License = onlineItem.LicenseName,
                LicenseUrl = onlineItem.LicenseUrl,
                AttributionText = onlineItem.AttributionText,
                DownloadedAtUtc = DateTimeOffset.UtcNow
            };

            var addResult = await _libraryService.AddOnlineAsync(
                finalDestinationPath,
                displayName: onlineItem.Title,
                sourceHost: onlineItem.SourceName,
                sourceDisplayUrl: onlineItem.SourceUrl,
                origin: origin,
                cancellationToken: cancellationToken);

            var libraryItem = addResult.Item;

            // 9. Enqueue 0.0.19 Wallpaper Analysis & Thumbnail
            if (_analysisService != null && libraryItem != null)
            {
                _analysisService.EnqueueBatch([libraryItem]);
            }

            if (_thumbnailService != null && libraryItem?.Type == IpcConstants.WallpaperTypeVideo)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _thumbnailService.GetOrCreateVideoThumbnailAsync(libraryItem.FilePath, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Thumbnail generation failed for {libraryItem.FilePath}: {ex}");
                    }
                }, CancellationToken.None);
            }

            progress?.Report(new OnlineDownloadProgress("Completed", 0, 0, 100));
            return new OnlineDownloadResult(
                Success: true,
                AlreadyExisted: addResult.AlreadyExisted,
                LocalFilePath: finalDestinationPath,
                LibraryItem: libraryItem);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempFilePath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempFilePath);
            Trace.WriteLine($"Online download failed for {onlineItem.RemoteId}: {ex}");
            return new OnlineDownloadResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<(bool Success, string? ErrorMessage)> DownloadStreamWithSsrfCheckAsync(
        Uri targetUri,
        string destinationFilePath,
        long maxSizeBytes,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var currentUri = targetUri;
        var redirectCount = 0;
        const int maxRedirects = 3;

        while (redirectCount <= maxRedirects)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Add("User-Agent", "NiceWP/0.0.20 (+https://github.com/NiceWP)");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            // Handle HTTP redirects with SSRF revalidation
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect)
            {
                redirectCount++;
                if (redirectCount > maxRedirects)
                {
                    return (false, "Too many HTTP redirects.");
                }

                var location = response.Headers.Location;
                if (location == null)
                {
                    return (false, "Redirect missing location header.");
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!NetworkSafetyValidator.ValidateUrl(nextUri.ToString(), out var validatedNext, out var ssrfError))
                {
                    return (false, $"Redirect destination rejected: {ssrfError}");
                }

                if (!string.Equals(validatedNext.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "Redirect to non-HTTPS scheme is not allowed.");
                }

                currentUri = validatedNext;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Server returned HTTP {(int)response.StatusCode}");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            if (totalBytes > maxSizeBytes)
            {
                return (false, $"File size exceeds limit ({totalBytes / (1024 * 1024)} MB > {maxSizeBytes / (1024 * 1024)} MB)");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long bytesReadTotal = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                bytesReadTotal += bytesRead;
                if (bytesReadTotal > maxSizeBytes)
                {
                    return (false, $"File size exceeded maximum limit of {maxSizeBytes / (1024 * 1024)} MB.");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (totalBytes > 0)
                {
                    var percent = Math.Clamp((int)((double)bytesReadTotal / totalBytes * 90), 0, 90);
                    progress?.Report(new OnlineDownloadProgress("Downloading", bytesReadTotal, totalBytes, percent));
                }
                else
                {
                    progress?.Report(new OnlineDownloadProgress("Downloading", bytesReadTotal, -1, null));
                }
            }

            return (true, null);
        }

        return (false, "Too many HTTP redirects.");
    }

    private static string SanitizeProviderName(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            OnlineProviderPolicy.ProviderOpenverse => "Openverse",
            OnlineProviderPolicy.ProviderWikimedia => "Wikimedia",
            OnlineProviderPolicy.ProviderNasa => "NASA",
            _ => "Other"
        };

    private static string GenerateSafeFileName(string provider, string remoteId, string title, string extension)
    {
        var cleanProvider = Regex.Replace(provider, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
        var cleanRemoteId = Regex.Replace(remoteId, @"[^a-zA-Z0-9_\-]", "_");
        var slug = Regex.Replace(title, @"[^a-zA-Z0-9_\-]", "_");
        slug = Regex.Replace(slug, @"_+", "_").Trim('_');

        if (slug.Length > 40)
        {
            slug = slug[..40];
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "wallpaper";
        }

        var fileName = $"{cleanProvider}_{cleanRemoteId}_{slug}{extension}";
        return fileName;
    }

    /// <summary>
    /// Scans the online wallpapers temp folder and deletes abandoned temporary files
    /// created by interrupted downloads or prior sessions.
    /// </summary>
    public static int CleanOrphanTempFiles(string? baseOnlineDirectory = null)
    {
        int cleaned = 0;
        try
        {
            var baseDir = baseOnlineDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NiceWP",
                "online-wallpapers");

            var tempDir = Path.Combine(baseDir, "temp");
            if (!Directory.Exists(tempDir)) return 0;

            var dir = new DirectoryInfo(tempDir);
            var threshold = DateTime.UtcNow - TimeSpan.FromMinutes(15);

            foreach (var file in dir.EnumerateFiles("*.tmp"))
            {
                if (file.LastWriteTimeUtc < threshold)
                {
                    try
                    {
                        file.Delete();
                        cleaned++;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to clean orphan temp files: {ex}");
        }

        return cleaned;
    }

    /// <summary>
    /// Checks whether the target drive hosting the path has enough available free space.
    /// </summary>
    public static bool HasSufficientDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrWhiteSpace(root)) return true;

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Disk space check fallback: {ex.Message}");
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* best effort cleanup */ }
    }
}
