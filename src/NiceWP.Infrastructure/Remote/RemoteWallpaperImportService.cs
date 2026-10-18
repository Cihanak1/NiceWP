using System.Diagnostics;
using System.Net;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Represents the result of a remote wallpaper import attempt.
/// </summary>
public sealed record RemoteImportResult(
    bool Success,
    WallpaperLibraryItem? Item = null,
    bool AlreadyExisted = false,
    bool Cancelled = false,
    string? ErrorMessage = null);

/// <summary>
/// Safely downloads, validates, stores, and integrates remote wallpapers into NiceWP library.
/// Ensures zero direct streaming by the Runtime Host; all remote media is downloaded and verified locally first.
/// </summary>
public sealed class RemoteWallpaperImportService
{
    private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = RemoteImportConstants.DefaultTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RemoteImportConstants.DefaultTimeout
        };
    });

    private readonly WallpaperLibraryService libraryService;
    private readonly HttpClient httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver;
    private readonly string tempDirectory;
    private readonly string storageDirectory;

    public RemoteWallpaperImportService(
        WallpaperLibraryService? libraryService = null,
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        string? tempDirectory = null,
        string? storageDirectory = null)
    {
        this.libraryService = libraryService ?? new WallpaperLibraryService();
        this.httpClient = httpClient ?? SharedHttpClient.Value;
        this.dnsResolver = dnsResolver;
        this.tempDirectory = string.IsNullOrWhiteSpace(tempDirectory)
            ? GetDefaultTempDirectory()
            : Path.GetFullPath(tempDirectory);
        this.storageDirectory = string.IsNullOrWhiteSpace(storageDirectory)
            ? GetDefaultStorageDirectory()
            : Path.GetFullPath(storageDirectory);
    }

    public static string GetDefaultTempDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RemoteImportConstants.DefaultFolderName,
            RemoteImportConstants.TempRelativePath);

    public static string GetDefaultStorageDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RemoteImportConstants.DefaultFolderName,
            RemoteImportConstants.StorageRelativePath);

    /// <summary>
    /// Removes unfinished download temporary files in the remote temp directory older than the specified age.
    /// Never deletes or alters library files.
    /// </summary>
    public void CleanStaleTempFiles(TimeSpan? maxAge = null)
    {
        try
        {
            if (!Directory.Exists(tempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(1));
            var tempFiles = Directory.GetFiles(tempDirectory, "*.download.tmp", SearchOption.TopDirectoryOnly);
            foreach (var file in tempFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore errors cleaning up individual stale temp files
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to enumerate stale remote temp files: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads a remote image or video from an HTTP/HTTPS URL, validates it against SSRF, size limits,
    /// and binary media structure, moves it to NiceWP-managed local storage, and adds it to the library.
    /// </summary>
    public async Task<RemoteImportResult> ImportAsync(
        string? url,
        IProgress<RemoteDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Initial URL syntax and scheme validation
        if (!NetworkSafetyValidator.ValidateUrl(url, out var currentUri, out var urlError))
        {
            return new RemoteImportResult(false, ErrorMessage: urlError);
        }

        var sanitizedUrl = NetworkSafetyValidator.SanitizeUrl(currentUri);
        var sourceHost = currentUri.Host;

        // 2. Duplicate detection: reuse local file if already imported from the exact same source
        var existingItem = await libraryService.FindBySourceDisplayUrlAsync(sanitizedUrl, cancellationToken).ConfigureAwait(false);
        if (existingItem != null && existingItem.FileExists())
        {
            progress?.Report(new RemoteDownloadProgress("Added to library.", 0, -1, 100));
            return new RemoteImportResult(
                Success: true,
                Item: existingItem,
                AlreadyExisted: true);
        }

        // 3. Stale temp files cleanup pass
        CleanStaleTempFiles();

        // 4. Setup local temp path
        Directory.CreateDirectory(tempDirectory);
        var tempFilePath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.download.tmp");

        progress?.Report(new RemoteDownloadProgress("Connecting...", 0, -1, null));

        HttpResponseMessage? response = null;
        var redirectCount = 0;

        try
        {
            // 5. Connect and resolve redirects with SSRF check on every hop
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var safetyResult = await NetworkSafetyValidator.ValidateSafetyAsync(currentUri, dnsResolver, cancellationToken).ConfigureAwait(false);
                if (!safetyResult.Success)
                {
                    return new RemoteImportResult(false, ErrorMessage: safetyResult.ErrorMessage);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                try
                {
                    response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new RemoteImportResult(false, ErrorMessage: "Download timed out.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    return new RemoteImportResult(false, ErrorMessage: "Download timed out.");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"HTTP request to '{currentUri}' failed: {ex.Message}");
                    return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorCouldNotConnect);
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode is >= 300 and <= 399)
                {
                    redirectCount++;
                    if (redirectCount > RemoteImportConstants.MaxRedirects)
                    {
                        response.Dispose();
                        return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorCouldNotConnect);
                    }

                    var location = response.Headers.Location;
                    response.Dispose();
                    response = null;

                    if (location == null)
                    {
                        return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorCouldNotConnect);
                    }

                    if (!location.IsAbsoluteUri)
                    {
                        if (!Uri.TryCreate(currentUri, location, out var resolvedRedirect))
                        {
                            return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorInvalidUrl);
                        }
                        currentUri = resolvedRedirect;
                    }
                    else
                    {
                        currentUri = location;
                    }

                    continue;
                }

                break;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                response?.Dispose();
                return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorCouldNotConnect);
            }

            // 6. Content-Length early rejection
            var contentLength = response.Content.Headers.ContentLength;
            var mediaTypeHint = response.Content.Headers.ContentType?.MediaType;
            var isImageHint = mediaTypeHint != null && mediaTypeHint.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            if (contentLength.HasValue)
            {
                if (contentLength.Value > RemoteImportConstants.MaxGlobalStreamSizeBytes)
                {
                    response.Dispose();
                    return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                }

                if (isImageHint && contentLength.Value > RemoteImportConstants.MaxImageSizeBytes)
                {
                    response.Dispose();
                    return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                }
            }

            // 7. Stream directly to disk using a bounded 64KB buffer
            progress?.Report(new RemoteDownloadProgress("Downloading", 0, contentLength ?? -1, contentLength.HasValue && contentLength.Value > 0 ? 0 : null));

            var maxAllowedBytes = isImageHint
                ? RemoteImportConstants.MaxImageSizeBytes
                : RemoteImportConstants.MaxGlobalStreamSizeBytes;

            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long totalBytesRead = 0;
                var lastReportTime = Stopwatch.GetTimestamp();
                int? lastReportedPercentage = null;

                int read;
                while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytesRead += read;
                    if (totalBytesRead > maxAllowedBytes)
                    {
                        response.Dispose();
                        return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    // Throttled progress reporting (on percentage change or at least 200ms elapsed)
                    if (progress != null)
                    {
                        int? percentage = null;
                        if (contentLength.HasValue && contentLength.Value > 0)
                        {
                            percentage = (int)Math.Clamp((totalBytesRead * 100) / contentLength.Value, 0, 100);
                        }

                        var elapsedMs = (Stopwatch.GetTimestamp() - lastReportTime) * 1000 / Stopwatch.Frequency;
                        if (percentage != lastReportedPercentage || elapsedMs >= 200)
                        {
                            lastReportTime = Stopwatch.GetTimestamp();
                            lastReportedPercentage = percentage;
                            progress.Report(new RemoteDownloadProgress("Downloading", totalBytesRead, contentLength ?? -1, percentage));
                        }
                    }
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            response.Dispose();
            response = null;

            // 8. Media validation: verify true file content independently of headers
            progress?.Report(new RemoteDownloadProgress("Validating...", 0, -1, null));

            var detectedType = MediaSignatureDetector.Detect(tempFilePath);
            if (detectedType == DetectedMediaType.Unknown)
            {
                return new RemoteImportResult(false, ErrorMessage: "Downloaded file is not valid media.");
            }

            var extension = MediaSignatureDetector.GetExtension(detectedType);
            var fileInfo = new FileInfo(tempFilePath);
            var fileSize = fileInfo.Length;

            if (detectedType is DetectedMediaType.Png or DetectedMediaType.Jpeg)
            {
                if (fileSize > RemoteImportConstants.MaxImageSizeBytes)
                {
                    return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                }
            }
            else if (detectedType == DetectedMediaType.Mp4)
            {
                if (fileSize > RemoteImportConstants.MaxVideoSizeBytes)
                {
                    return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                }
            }

            // 9. Move atomically to permanent library/remote storage
            Directory.CreateDirectory(storageDirectory);
            var finalFileName = $"{Guid.NewGuid():N}{extension}";
            var finalFilePath = Path.Combine(storageDirectory, finalFileName);

            File.Move(tempFilePath, finalFilePath);

            // 10. Secondary validation via local media pipeline
            var validation = WallpaperLibraryService.ValidateWallpaperFile(finalFilePath, out var normalizedFullPath, out var wallpaperType);
            if (!validation.Success)
            {
                try { File.Delete(finalFilePath); } catch { }
                return new RemoteImportResult(false, ErrorMessage: "Downloaded file is not valid media.");
            }

            // 11. Determine clean display name
            var rawName = Path.GetFileNameWithoutExtension(currentUri.AbsolutePath);
            string displayName;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                displayName = $"Remote {wallpaperType} ({sourceHost})";
            }
            else
            {
                var cleanName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars())).Trim();
                displayName = string.IsNullOrWhiteSpace(cleanName)
                    ? $"Remote {wallpaperType} ({sourceHost})"
                    : $"{cleanName}{extension}";
            }

            // 12. Add to library with sanitized remote metadata
            var addResult = await libraryService.AddRemoteAsync(
                path: normalizedFullPath,
                displayName: displayName,
                sourceHost: sourceHost,
                sourceDisplayUrl: sanitizedUrl,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!addResult.Success || addResult.Item == null)
            {
                try { File.Delete(finalFilePath); } catch { }
                return new RemoteImportResult(false, ErrorMessage: "Failed to persist wallpaper library.");
            }

            progress?.Report(new RemoteDownloadProgress("Added to library.", 0, -1, 100));

            return new RemoteImportResult(
                Success: true,
                Item: addResult.Item,
                AlreadyExisted: addResult.AlreadyExisted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteImportResult(false, ErrorMessage: "Download timed out.");
        }
        catch (OperationCanceledException)
        {
            return new RemoteImportResult(false, Cancelled: true, ErrorMessage: "Download cancelled.");
        }
        catch (TimeoutException)
        {
            return new RemoteImportResult(false, ErrorMessage: "Download timed out.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Remote import failed: {ex}");
            return new RemoteImportResult(false, ErrorMessage: NetworkSafetyValidator.ErrorCouldNotConnect);
        }
        finally
        {
            response?.Dispose();
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { }
            }
        }
    }
}
