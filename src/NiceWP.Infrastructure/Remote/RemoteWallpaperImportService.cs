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
/// Supports direct media URLs as well as static wallpaper page URL discovery.
/// </summary>
public sealed class RemoteWallpaperImportService
{
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";
    private const string BrowserAcceptLanguage = "en-US,en;q=0.9";

    public const string ErrorNoWallpaperFoundOnPage = "No supported wallpaper was found on this page.";
    public const string ErrorDirectDownloadNotFound = "Could not find a direct download link on this page. Please use the direct image/video URL.";

    private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = RemoteImportConstants.DefaultTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RemoteImportConstants.DefaultTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(BrowserAccept);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(BrowserAcceptLanguage);
        return client;
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
    /// Imports a wallpaper from an HTTP/HTTPS URL (either direct media or wallpaper page).
    /// Validates inputs against SSRF, size limits, and binary media structure,
    /// moves it to NiceWP-managed local storage, and adds it to the library.
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

        var sanitizedPageUrl = NetworkSafetyValidator.SanitizeUrl(currentUri);
        var pageHost = currentUri.Host;

        // 2. Duplicate detection: reuse local file if already imported from the exact same source
        var existingItem = await libraryService.FindBySourceDisplayUrlAsync(sanitizedPageUrl, cancellationToken).ConfigureAwait(false);
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

        try
        {
            // 5. Connect to initial URL
            var connectResult = await ConnectAsync(currentUri, cancellationToken).ConfigureAwait(false);
            if (!connectResult.Success || connectResult.Response == null)
            {
                return new RemoteImportResult(false, ErrorMessage: connectResult.ErrorMessage);
            }

            var response = connectResult.Response;
            var finalUri = connectResult.FinalUri;
            string? pageTitle = null;
            var isDiscoveredFromHtml = false;

            // 6. Check if response is HTML
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var pathIsHtml = finalUri.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                             finalUri.AbsolutePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

            var isHtml = mediaType is "text/html" or "application/xhtml+xml" ||
                         (mediaType is null or "text/plain" && pathIsHtml);

            if (isHtml)
            {
                isDiscoveredFromHtml = true;
                progress?.Report(new RemoteDownloadProgress("Inspecting page...", 0, -1, null));

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > RemoteImportConstants.MaxHtmlDocumentSizeBytes)
                {
                    response.Dispose();
                    return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                }

                // Read HTML document bounded by MaxHtmlDocumentSizeBytes
                string html;
                using (response)
                await using (var htmlStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                using (var memStream = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    long totalHtmlBytes = 0;
                    int read;
                    while ((read = await htmlStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        totalHtmlBytes += read;
                        if (totalHtmlBytes > RemoteImportConstants.MaxHtmlDocumentSizeBytes)
                        {
                            return new RemoteImportResult(false, ErrorMessage: "File is too large.");
                        }
                        memStream.Write(buffer, 0, read);
                    }
                    html = System.Text.Encoding.UTF8.GetString(memStream.ToArray());
                }

                // Check for bot challenge / Cloudflare page
                if (html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteImportResult(false, ErrorMessage: ErrorDirectDownloadNotFound);
                }

                // Discover wallpaper candidates
                var candidate = HtmlWallpaperDiscoveryService.DiscoverWallpaper(html, finalUri);
                if (candidate == null)
                {
                    return new RemoteImportResult(false, ErrorMessage: ErrorNoWallpaperFoundOnPage);
                }

                progress?.Report(new RemoteDownloadProgress("Wallpaper found.", 0, -1, null));

                pageTitle = candidate.Title;
                var mediaUri = candidate.MediaUri;

                // Connect to discovered media URL
                var mediaConnect = await ConnectAsync(mediaUri, cancellationToken).ConfigureAwait(false);
                if (!mediaConnect.Success || mediaConnect.Response == null)
                {
                    return new RemoteImportResult(false, ErrorMessage: mediaConnect.ErrorMessage);
                }

                response = mediaConnect.Response;
                finalUri = mediaConnect.FinalUri;
            }

            // 7. Stream media directly to temp file and validate
            using (response)
            {
                var downloadResult = await DownloadAndValidateMediaAsync(response, tempFilePath, progress, cancellationToken).ConfigureAwait(false);
                if (!downloadResult.Success)
                {
                    return new RemoteImportResult(false, ErrorMessage: downloadResult.ErrorMessage);
                }

                var detectedType = downloadResult.DetectedType;
                var extension = MediaSignatureDetector.GetExtension(detectedType);
                var wallpaperType = MediaSignatureDetector.GetWallpaperType(detectedType);

                // 8. Move atomically to permanent library/remote storage
                Directory.CreateDirectory(storageDirectory);
                var finalFileName = $"{Guid.NewGuid():N}{extension}";
                var finalFilePath = Path.Combine(storageDirectory, finalFileName);
                File.Move(tempFilePath, finalFilePath);

                // 9. Secondary validation via local media pipeline
                var validation = WallpaperLibraryService.ValidateWallpaperFile(finalFilePath, out var normalizedFullPath, out _);
                if (!validation.Success)
                {
                    try { File.Delete(finalFilePath); } catch { }
                    return new RemoteImportResult(false, ErrorMessage: "Downloaded file is not valid media.");
                }

                // 10. Determine clean display name
                string displayName;
                if (isDiscoveredFromHtml && !string.IsNullOrWhiteSpace(pageTitle))
                {
                    displayName = $"{pageTitle}{extension}";
                }
                else
                {
                    var rawName = Path.GetFileNameWithoutExtension(finalUri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(rawName))
                    {
                        displayName = $"Remote {wallpaperType} ({pageHost})";
                    }
                    else
                    {
                        var cleanName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars())).Trim();
                        displayName = string.IsNullOrWhiteSpace(cleanName)
                            ? $"Remote {wallpaperType} ({pageHost})"
                            : $"{cleanName}{extension}";
                    }
                }

                // 11. Add to library with sanitized remote metadata
                var addResult = await libraryService.AddRemoteAsync(
                    path: normalizedFullPath,
                    displayName: displayName,
                    sourceHost: pageHost,
                    sourceDisplayUrl: sanitizedPageUrl,
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
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { }
            }
        }
    }

    private async Task<(bool Success, HttpResponseMessage? Response, Uri FinalUri, string? ErrorMessage)> ConnectAsync(
        Uri startUri,
        CancellationToken cancellationToken)
    {
        var currentUri = startUri;
        var redirectCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var safetyResult = await NetworkSafetyValidator.ValidateSafetyAsync(currentUri, dnsResolver, cancellationToken).ConfigureAwait(false);
            if (!safetyResult.Success)
            {
                return (false, null, currentUri, safetyResult.ErrorMessage);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", BrowserAccept);
            request.Headers.TryAddWithoutValidation("Accept-Language", BrowserAcceptLanguage);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (false, null, currentUri, "Download timed out.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                return (false, null, currentUri, "Download timed out.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"HTTP request to '{currentUri}' failed: {ex.Message}");
                return (false, null, currentUri, NetworkSafetyValidator.ErrorCouldNotConnect);
            }

            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and <= 399)
            {
                redirectCount++;
                if (redirectCount > RemoteImportConstants.MaxRedirects)
                {
                    response.Dispose();
                    return (false, null, currentUri, NetworkSafetyValidator.ErrorCouldNotConnect);
                }

                var location = response.Headers.Location;
                response.Dispose();

                if (location == null)
                {
                    return (false, null, currentUri, NetworkSafetyValidator.ErrorCouldNotConnect);
                }

                if (!location.IsAbsoluteUri)
                {
                    if (!Uri.TryCreate(currentUri, location, out var resolvedRedirect))
                    {
                        return (false, null, currentUri, NetworkSafetyValidator.ErrorInvalidUrl);
                    }
                    currentUri = resolvedRedirect;
                }
                else
                {
                    currentUri = location;
                }

                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                return (false, null, currentUri, ErrorDirectDownloadNotFound);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return (false, null, currentUri, "Remote resource was not found (404).");
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = $"Server returned error ({(int)response.StatusCode}).";
                response.Dispose();
                return (false, null, currentUri, err);
            }

            return (true, response, currentUri, null);
        }
    }

    private static async Task<(bool Success, DetectedMediaType DetectedType, string? ErrorMessage)> DownloadAndValidateMediaAsync(
        HttpResponseMessage response,
        string tempFilePath,
        IProgress<RemoteDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        var mediaTypeHint = response.Content.Headers.ContentType?.MediaType;
        var isImageHint = mediaTypeHint != null && mediaTypeHint.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        if (contentLength.HasValue)
        {
            if (contentLength.Value > RemoteImportConstants.MaxGlobalStreamSizeBytes)
            {
                return (false, DetectedMediaType.Unknown, "File is too large.");
            }

            if (isImageHint && contentLength.Value > RemoteImportConstants.MaxImageSizeBytes)
            {
                return (false, DetectedMediaType.Unknown, "File is too large.");
            }
        }

        var maxAllowedBytes = isImageHint
            ? RemoteImportConstants.MaxImageSizeBytes
            : RemoteImportConstants.MaxGlobalStreamSizeBytes;

        progress?.Report(new RemoteDownloadProgress("Downloading", 0, contentLength ?? -1, contentLength.HasValue && contentLength.Value > 0 ? 0 : null));

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
                    return (false, DetectedMediaType.Unknown, "File is too large.");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

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

        progress?.Report(new RemoteDownloadProgress("Validating...", 0, -1, null));

        var detectedType = MediaSignatureDetector.Detect(tempFilePath);
        if (detectedType == DetectedMediaType.Unknown)
        {
            return (false, DetectedMediaType.Unknown, "Downloaded file is not valid media.");
        }

        var fileInfo = new FileInfo(tempFilePath);
        var fileSize = fileInfo.Length;

        if (detectedType is DetectedMediaType.Png or DetectedMediaType.Jpeg)
        {
            if (fileSize > RemoteImportConstants.MaxImageSizeBytes)
            {
                return (false, DetectedMediaType.Unknown, "File is too large.");
            }
        }
        else if (detectedType == DetectedMediaType.Mp4)
        {
            if (fileSize > RemoteImportConstants.MaxVideoSizeBytes)
            {
                return (false, DetectedMediaType.Unknown, "File is too large.");
            }
        }

        return (true, detectedType, null);
    }
}
