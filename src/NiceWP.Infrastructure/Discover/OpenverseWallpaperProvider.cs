using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Implements the Openverse search provider for freely licensed and public domain images.
/// Respects Openverse API usage terms, rate limiting headers, and mature content gating.
/// </summary>
public sealed class OpenverseWallpaperProvider : IWallpaperProvider
{
    public const string ProviderId = OnlineProviderPolicy.ProviderOpenverse;
    public const string DefaultDisplayName = "Openverse";
    public const string BaseApiUrl = "https://api.openverse.org/v1/images/";

    private readonly HttpClient _httpClient;
    private readonly OpenverseRateLimitHandler _rateLimitHandler;

    public string Id => ProviderId;

    public string DisplayName => DefaultDisplayName;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsVideo: false,
        SupportsOrientationFilter: true,
        SupportsResolutionFilter: true,
        SupportsLicenseFilter: true,
        SupportsSorting: false);

    public ProviderRateLimitState? CurrentRateLimit => _rateLimitHandler.CurrentState;

    public OpenverseWallpaperProvider(HttpClient? httpClient = null, OpenverseRateLimitHandler? rateLimitHandler = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _rateLimitHandler = rateLimitHandler ?? new OpenverseRateLimitHandler();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NiceWP/0.0.20 (+https://github.com/NiceWP)");
        }
    }

    public async Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return WallpaperSearchResult.Empty(ProviderId);
        }

        if (_rateLimitHandler.IsRateLimited(out var resetAt, out var retryAfter))
        {
            return WallpaperSearchResult.Empty(
                ProviderId,
                "Openverse is temporarily rate limited. Please try again later.",
                _rateLimitHandler.CurrentState);
        }

        try
        {
            var uri = BuildSearchUri(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _rateLimitHandler.RecordResponse(response);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return WallpaperSearchResult.Empty(
                    ProviderId,
                    "Openverse is temporarily rate limited. Please try again later.",
                    _rateLimitHandler.CurrentState);
            }

            if (!response.IsSuccessStatusCode)
            {
                return WallpaperSearchResult.Empty(
                    ProviderId,
                    $"Openverse API returned HTTP {(int)response.StatusCode}");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var jsonDoc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            var root = jsonDoc.RootElement;

            return ParseResults(root, query.Page);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Openverse search error: {ex}");
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
            var uri = new Uri($"{BaseApiUrl}{Uri.EscapeDataString(remoteId)}/");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var item = ParseItem(root);
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
        catch (Exception ex)
        {
            Trace.WriteLine($"Openverse GetDetails error for {remoteId}: {ex}");
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
        var parameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query.Query.Trim())}",
            "mature=false", // Safe content permanently
            $"page={Math.Max(1, query.Page)}",
            $"page_size={Math.Clamp(query.PageSize, 1, 50)}"
        };

        // License filter
        if (string.Equals(query.LicenseFilter, "publicdomain", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("license=pdm");
        }
        else if (string.Equals(query.LicenseFilter, "cc0", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("license=cc0");
        }
        else if (string.Equals(query.LicenseFilter, "ccby", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("license=by");
        }
        else if (string.Equals(query.LicenseFilter, "ccbysa", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("license=by-sa");
        }
        else
        {
            // Default: Whitelisted open licenses
            parameters.Add("license=pdm,cc0,by,by-sa");
        }

        // Orientation / Aspect Ratio
        if (string.Equals(query.Orientation, "landscape", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query.Orientation, "16:9", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query.Orientation, "16:10", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("aspect_ratio=landscape");
        }

        // Resolution
        if (!string.IsNullOrWhiteSpace(query.MinResolution) &&
            !string.Equals(query.MinResolution, "any", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add("size=large");
        }

        var queryString = string.Join("&", parameters);
        return new Uri($"{BaseApiUrl}?{queryString}");
    }

    private static WallpaperSearchResult ParseResults(JsonElement root, int requestedPage)
    {
        var totalCount = root.TryGetProperty("result_count", out var rc) ? rc.GetInt32() : 0;
        var pageCount = root.TryGetProperty("page_count", out var pc) ? pc.GetInt32() : 1;

        var items = new List<OnlineWallpaperItem>();
        if (root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in resultsElement.EnumerateArray())
            {
                var item = ParseItem(result);
                if (item != null)
                {
                    items.Add(item);
                }
            }
        }

        var hasMore = requestedPage < pageCount;
        return new WallpaperSearchResult(
            ProviderId: ProviderId,
            Items: items,
            TotalCount: totalCount,
            Page: requestedPage,
            HasMore: hasMore);
    }

    private static OnlineWallpaperItem? ParseItem(JsonElement el)
    {
        if (!el.TryGetProperty("id", out var idProp))
        {
            return null;
        }

        var id = idProp.GetString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = el.TryGetProperty("title", out var titleProp) && !string.IsNullOrWhiteSpace(titleProp.GetString())
            ? titleProp.GetString()!.Trim()
            : "Untitled Openverse Wallpaper";

        var url = el.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var thumbnail = el.TryGetProperty("thumbnail", out var thumbProp) && !string.IsNullOrWhiteSpace(thumbProp.GetString())
            ? thumbProp.GetString()!
            : url;

        var landingUrl = el.TryGetProperty("foreign_landing_url", out var landingProp)
            ? landingProp.GetString() ?? url
            : url;

        var creator = el.TryGetProperty("creator", out var creatorProp) ? creatorProp.GetString() : null;
        var creatorUrl = el.TryGetProperty("creator_url", out var creatorUrlProp) ? creatorUrlProp.GetString() : null;

        var rawLicense = el.TryGetProperty("license", out var licProp) ? licProp.GetString() : null;
        var licenseUrl = el.TryGetProperty("license_url", out var licUrlProp) ? licUrlProp.GetString() : null;

        var licenseInfo = WallpaperLicensePolicy.Evaluate(rawLicense, licenseUrl);
        if (!WallpaperLicensePolicy.IsDefaultDiscoverAllowed(licenseInfo))
        {
            // Filter out restricted or unknown licenses
            return null;
        }

        var width = el.TryGetProperty("width", out var wProp) && wProp.TryGetInt32(out var w) ? (int?)w : null;
        var height = el.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out var h) ? (int?)h : null;

        double? aspectRatio = (width > 0 && height > 0)
            ? Math.Round((double)width.Value / height.Value, 2)
            : null;

        var creatorDisplay = string.IsNullOrWhiteSpace(creator) ? "Unknown Artist" : creator.Trim();
        var attributionText = $"Photo by {creatorDisplay} via Openverse ({licenseInfo.Name})";

        var tags = new List<string>();
        if (el.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                if (tag.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    tags.Add(nameProp.GetString()!.Trim());
                }
            }
        }

        return new OnlineWallpaperItem(
            ProviderId: ProviderId,
            RemoteId: id,
            Title: title,
            Description: null,
            MediaType: "image",
            Width: width,
            Height: height,
            AspectRatio: aspectRatio,
            ThumbnailUrl: thumbnail,
            PreviewUrl: url,
            DownloadUrl: url,
            CreatorName: creator,
            CreatorUrl: creatorUrl,
            SourceName: "Openverse",
            SourceUrl: landingUrl,
            LicenseCode: licenseInfo.Code,
            LicenseName: licenseInfo.Name,
            LicenseUrl: licenseInfo.Url,
            AttributionText: attributionText,
            IsPublicDomain: licenseInfo.Status == LicenseStatus.Allowed && !licenseInfo.RequiresAttribution,
            RequiresAttribution: licenseInfo.RequiresAttribution,
            CommercialUseAllowed: licenseInfo.CommercialUseAllowed,
            DerivativeUseAllowed: licenseInfo.DerivativeUseAllowed,
            Mature: false,
            Tags: tags);
    }
}
