using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Implements the Wikimedia Commons media provider using the MediaWiki action=query API.
/// Applies strict wallpaper resolution filters, HTML sanitization, and open-license whitelisting.
/// </summary>
public sealed class WikimediaWallpaperProvider : IWallpaperProvider
{
    public const string ProviderId = OnlineProviderPolicy.ProviderWikimedia;
    public const string DefaultDisplayName = "Wikimedia Commons";
    public const string ApiEndpoint = "https://commons.wikimedia.org/w/api.php";

    private readonly HttpClient _httpClient;

    public string Id => ProviderId;

    public string DisplayName => DefaultDisplayName;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsVideo: false,
        SupportsOrientationFilter: true,
        SupportsResolutionFilter: true,
        SupportsLicenseFilter: true,
        SupportsSorting: false);

    public ProviderRateLimitState? CurrentRateLimit => null;

    public WikimediaWallpaperProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NiceWP/0.0.20 (https://github.com/NiceWP; mailto:dev@nicewp.local)");
        }
    }

    public async Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return WallpaperSearchResult.Empty(ProviderId);
        }

        try
        {
            var uri = BuildSearchUri(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return WallpaperSearchResult.Empty(ProviderId, $"Wikimedia Commons returned HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            return ParseResults(root, query);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Wikimedia search error: {ex}");
            return WallpaperSearchResult.Empty(ProviderId, ex.Message);
        }
    }

    public async Task<OnlineWallpaperDetails?> GetDetailsAsync(string remoteId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteId))
        {
            return null;
        }

        try
        {
            var pageId = remoteId.Trim();
            var uri = new Uri($"{ApiEndpoint}?action=query&pageids={Uri.EscapeDataString(pageId)}&prop=imageinfo&iiprop=url|size|mime|mediatype|extmetadata&format=json&formatversion=2");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("query", out var queryEl) &&
                queryEl.TryGetProperty("pages", out var pagesEl) &&
                pagesEl.ValueKind == JsonValueKind.Array &&
                pagesEl.GetArrayLength() > 0)
            {
                var page = pagesEl[0];
                var item = ParsePageItem(page, null);
                if (item == null)
                {
                    return null;
                }

                return new OnlineWallpaperDetails(
                    ProviderId: ProviderId,
                    RemoteId: item.RemoteId,
                    Title: item.Title,
                    Description: item.Description,
                    MediaType: item.MediaType,
                    Width: item.Width,
                    Height: item.Height,
                    DownloadUrl: item.DownloadUrl,
                    PreviewUrl: item.PreviewUrl,
                    CreatorName: item.CreatorName,
                    CreatorUrl: item.CreatorUrl,
                    SourceName: item.SourceName,
                    SourceUrl: item.SourceUrl,
                    LicenseCode: item.LicenseCode,
                    LicenseName: item.LicenseName,
                    LicenseUrl: item.LicenseUrl,
                    AttributionText: item.AttributionText);
            }

            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Wikimedia GetDetails error for {remoteId}: {ex}");
            return null;
        }
    }

    public async Task<Stream?> OpenDownloadAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private static Uri BuildSearchUri(WallpaperSearchQuery query)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var offset = Math.Max(0, (query.Page - 1) * pageSize);
        var searchTerm = $"{query.Query.Trim()} filetype:bitmap";

        var parameters = new List<string>
        {
            "action=query",
            "generator=search",
            $"gsrsearch={Uri.EscapeDataString(searchTerm)}",
            "gsrnamespace=6", // File: namespace
            $"gsrlimit={pageSize}",
            $"gsroffset={offset}",
            "prop=imageinfo",
            "iiprop=url|size|mime|mediatype|extmetadata",
            "iiurlwidth=640",
            "format=json",
            "formatversion=2"
        };

        var queryString = string.Join("&", parameters);
        return new Uri($"{ApiEndpoint}?{queryString}");
    }

    private static WallpaperSearchResult ParseResults(JsonElement root, WallpaperSearchQuery query)
    {
        var items = new List<OnlineWallpaperItem>();
        var hasMore = false;

        if (root.TryGetProperty("continue", out _))
        {
            hasMore = true;
        }

        if (root.TryGetProperty("query", out var queryEl) &&
            queryEl.TryGetProperty("pages", out var pagesEl) &&
            pagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pagesEl.EnumerateArray())
            {
                var item = ParsePageItem(page, query);
                if (item != null)
                {
                    items.Add(item);
                }
            }
        }

        return new WallpaperSearchResult(
            ProviderId: ProviderId,
            Items: items,
            TotalCount: items.Count,
            Page: query.Page,
            HasMore: hasMore);
    }

    private static OnlineWallpaperItem? ParsePageItem(JsonElement page, WallpaperSearchQuery? query)
    {
        var pageId = page.TryGetProperty("pageid", out var pid) ? pid.GetInt64().ToString() : null;
        var rawTitle = page.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;

        if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        // Clean title (remove "File:" prefix and extension)
        var cleanTitle = rawTitle.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
            ? rawTitle[5..]
            : rawTitle;
        cleanTitle = Path.GetFileNameWithoutExtension(cleanTitle).Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            cleanTitle = "Wikimedia Commons Wallpaper";
        }

        if (!page.TryGetProperty("imageinfo", out var imageInfoEl) ||
            imageInfoEl.ValueKind != JsonValueKind.Array ||
            imageInfoEl.GetArrayLength() == 0)
        {
            return null;
        }

        var info = imageInfoEl[0];

        // Format validation: JPEG and PNG only. Exclude SVG, GIF, TIFF, audio, PDF.
        var mime = info.TryGetProperty("mime", out var mProp) ? mProp.GetString()?.ToLowerInvariant() : null;
        if (mime != "image/jpeg" && mime != "image/png")
        {
            return null;
        }

        var width = info.TryGetProperty("width", out var wProp) && wProp.TryGetInt32(out var w) ? (int?)w : null;
        var height = info.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out var h) ? (int?)h : null;

        // Minimum resolution check: standard 1080p equivalent pixel area (1920x1080 = 2,073,600)
        if (width.HasValue && height.HasValue)
        {
            var area = (long)width.Value * height.Value;
            if (area < 1920 * 1080)
            {
                return null;
            }

            // Orientation filter: if landscape requested, ensure width >= height
            if (query != null &&
                (string.Equals(query.Orientation, "landscape", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(query.Orientation, "16:9", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(query.Orientation, "16:10", StringComparison.OrdinalIgnoreCase)))
            {
                if (width.Value < height.Value)
                {
                    return null;
                }
            }
        }

        var fullUrl = info.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(fullUrl))
        {
            return null;
        }

        var thumbUrl = info.TryGetProperty("thumburl", out var tProp) && !string.IsNullOrWhiteSpace(tProp.GetString())
            ? tProp.GetString()!
            : fullUrl;

        var descriptionUrl = info.TryGetProperty("descriptionurl", out var descProp)
            ? descProp.GetString() ?? fullUrl
            : fullUrl;

        // Parse extmetadata
        string? artist = null;
        string? licenseShortName = null;
        string? licenseUrl = null;
        string? description = null;
        bool isExplicitPublicDomain = false;

        if (info.TryGetProperty("extmetadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
        {
            if (metaEl.TryGetProperty("Artist", out var artistProp) &&
                artistProp.TryGetProperty("value", out var aVal))
            {
                artist = HtmlTextSanitizer.Sanitize(aVal.GetString());
            }

            if (metaEl.TryGetProperty("LicenseShortName", out var licProp) &&
                licProp.TryGetProperty("value", out var lVal))
            {
                licenseShortName = HtmlTextSanitizer.Sanitize(lVal.GetString());
            }
            else if (metaEl.TryGetProperty("License", out var licAltProp) &&
                     licAltProp.TryGetProperty("value", out var lAltVal))
            {
                licenseShortName = HtmlTextSanitizer.Sanitize(lAltVal.GetString());
            }

            if (metaEl.TryGetProperty("LicenseUrl", out var licUrlProp) &&
                licUrlProp.TryGetProperty("value", out var luVal))
            {
                licenseUrl = luVal.GetString();
            }

            if (metaEl.TryGetProperty("ImageDescription", out var descMetaProp) &&
                descMetaProp.TryGetProperty("value", out var dVal))
            {
                description = HtmlTextSanitizer.Sanitize(dVal.GetString());
            }

            if (metaEl.TryGetProperty("Copyrighted", out var cProp) &&
                cProp.TryGetProperty("value", out var cVal) &&
                string.Equals(cVal.GetString(), "False", StringComparison.OrdinalIgnoreCase))
            {
                isExplicitPublicDomain = true;
            }
        }

        // License Evaluation
        var licenseInfo = WallpaperLicensePolicy.Evaluate(licenseShortName, licenseUrl, isExplicitPublicDomain);
        if (!WallpaperLicensePolicy.IsDefaultDiscoverAllowed(licenseInfo))
        {
            return null;
        }

        double? aspectRatio = (width > 0 && height > 0)
            ? Math.Round((double)width.Value / height.Value, 2)
            : null;

        var creatorDisplay = string.IsNullOrWhiteSpace(artist) ? "Wikimedia Contributor" : artist;
        var attributionText = $"Photo by {creatorDisplay} via Wikimedia Commons ({licenseInfo.Name})";

        return new OnlineWallpaperItem(
            ProviderId: ProviderId,
            RemoteId: pageId,
            Title: cleanTitle,
            Description: description,
            MediaType: "image",
            Width: width,
            Height: height,
            AspectRatio: aspectRatio,
            ThumbnailUrl: thumbUrl,
            PreviewUrl: fullUrl,
            DownloadUrl: fullUrl,
            CreatorName: artist,
            CreatorUrl: null,
            SourceName: "Wikimedia Commons",
            SourceUrl: descriptionUrl,
            LicenseCode: licenseInfo.Code,
            LicenseName: licenseInfo.Name,
            LicenseUrl: licenseInfo.Url,
            AttributionText: attributionText,
            IsPublicDomain: licenseInfo.Status == LicenseStatus.Allowed && !licenseInfo.RequiresAttribution,
            RequiresAttribution: licenseInfo.RequiresAttribution,
            CommercialUseAllowed: licenseInfo.CommercialUseAllowed,
            DerivativeUseAllowed: licenseInfo.DerivativeUseAllowed,
            Mature: false);
    }
}
