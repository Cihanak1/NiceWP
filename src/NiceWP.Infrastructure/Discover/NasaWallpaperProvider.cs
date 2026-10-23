using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Implements the NASA Image and Video Library provider (https://images-api.nasa.gov).
/// Supports both image and video wallpapers with rights filtering for US Government public domain works.
/// </summary>
public sealed class NasaWallpaperProvider : IWallpaperProvider
{
    public const string ProviderId = OnlineProviderPolicy.ProviderNasa;
    public const string DefaultDisplayName = "NASA";
    public const string ApiEndpoint = "https://images-api.nasa.gov";

    private readonly HttpClient _httpClient;

    public string Id => ProviderId;

    public string DisplayName => DefaultDisplayName;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsVideo: true,
        SupportsOrientationFilter: false,
        SupportsResolutionFilter: false,
        SupportsLicenseFilter: false,
        SupportsSorting: true,
        SupportedSortOptions: ["relevant", "newest"]);

    public ProviderRateLimitState? CurrentRateLimit => null;

    public NasaWallpaperProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
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

        try
        {
            var uri = BuildSearchUri(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return WallpaperSearchResult.Empty(ProviderId, $"NASA Library API returned HTTP {(int)response.StatusCode}");
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
            Trace.WriteLine($"NASA search error: {ex}");
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
            var assetUri = new Uri($"{ApiEndpoint}/asset/{Uri.EscapeDataString(remoteId)}");
            using var response = await _httpClient.GetAsync(assetUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string? bestDownloadUrl = null;
            string? previewUrl = null;

            if (root.TryGetProperty("collection", out var colEl) &&
                colEl.TryGetProperty("items", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array)
            {
                var urls = new List<string>();
                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (item.TryGetProperty("href", out var hrefProp) && !string.IsNullOrWhiteSpace(hrefProp.GetString()))
                    {
                        urls.Add(hrefProp.GetString()!);
                    }
                }

                // Choose best URL based on file type
                bestDownloadUrl = urls.FirstOrDefault(u => u.EndsWith("~orig.jpg", StringComparison.OrdinalIgnoreCase) ||
                                                           u.EndsWith("~large.jpg", StringComparison.OrdinalIgnoreCase) ||
                                                           u.EndsWith("~orig.mp4", StringComparison.OrdinalIgnoreCase))
                                  ?? urls.FirstOrDefault(u => u.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                              u.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

                previewUrl = urls.FirstOrDefault(u => u.EndsWith("~medium.jpg", StringComparison.OrdinalIgnoreCase) ||
                                                      u.EndsWith("~small.jpg", StringComparison.OrdinalIgnoreCase))
                             ?? bestDownloadUrl;
            }

            if (string.IsNullOrWhiteSpace(bestDownloadUrl))
            {
                return null;
            }

            var isVideo = bestDownloadUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

            return new OnlineWallpaperDetails(
                ProviderId: ProviderId,
                RemoteId: remoteId,
                Title: $"NASA Asset {remoteId}",
                Description: null,
                MediaType: isVideo ? "video" : "image",
                Width: null,
                Height: null,
                DownloadUrl: bestDownloadUrl,
                PreviewUrl: previewUrl,
                CreatorName: "NASA",
                CreatorUrl: "https://www.nasa.gov",
                SourceName: "NASA Image and Video Library",
                SourceUrl: $"https://images.nasa.gov/details/{Uri.EscapeDataString(remoteId)}",
                LicenseCode: WallpaperLicensePolicy.CodePublicDomain,
                LicenseName: "Public Domain (US Government Work)",
                LicenseUrl: "https://www.nasa.gov/multimedia/guidelines/index.html",
                AttributionText: $"Source: NASA ({remoteId})");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NASA GetDetails error for {remoteId}: {ex}");
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
        var mediaType = "image";
        if (string.Equals(query.MediaType, "video", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "video";
        }
        else if (string.Equals(query.MediaType, "all", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "image,video";
        }

        var parameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query.Query.Trim())}",
            $"media_type={mediaType}",
            $"page={Math.Max(1, query.Page)}"
        };

        var queryString = string.Join("&", parameters);
        return new Uri($"{ApiEndpoint}/search?{queryString}");
    }

    private static WallpaperSearchResult ParseResults(JsonElement root, WallpaperSearchQuery query)
    {
        var items = new List<OnlineWallpaperItem>();
        int? totalCount = null;

        if (!root.TryGetProperty("collection", out var collectionEl))
        {
            return WallpaperSearchResult.Empty(ProviderId);
        }

        if (collectionEl.TryGetProperty("metadata", out var metaEl) &&
            metaEl.TryGetProperty("total_hits", out var hitsProp) &&
            hitsProp.TryGetInt32(out var hits))
        {
            totalCount = hits;
        }

        if (collectionEl.TryGetProperty("items", out var itemsEl) &&
            itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var itemEl in itemsEl.EnumerateArray())
            {
                var item = ParseCollectionItem(itemEl);
                if (item != null)
                {
                    items.Add(item);
                }
            }
        }

        var hasMore = totalCount.HasValue && (query.Page * query.PageSize) < totalCount.Value;

        return new WallpaperSearchResult(
            ProviderId: ProviderId,
            Items: items,
            TotalCount: totalCount,
            Page: query.Page,
            HasMore: hasMore);
    }

    private static OnlineWallpaperItem? ParseCollectionItem(JsonElement itemEl)
    {
        if (!itemEl.TryGetProperty("data", out var dataArr) ||
            dataArr.ValueKind != JsonValueKind.Array ||
            dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var data = dataArr[0];
        var nasaId = data.TryGetProperty("nasa_id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(nasaId))
        {
            return null;
        }

        var title = data.TryGetProperty("title", out var titleProp) && !string.IsNullOrWhiteSpace(titleProp.GetString())
            ? titleProp.GetString()!.Trim()
            : $"NASA Wallpaper ({nasaId})";

        var description = data.TryGetProperty("description", out var descProp)
            ? descProp.GetString()
            : null;

        // Rights and Safety Filtering:
        // Exclude items with third-party copyright claims
        var copyright = data.TryGetProperty("copyright", out var copyProp) ? copyProp.GetString() : null;
        var rights = data.TryGetProperty("rights", out var rightsProp) ? rightsProp.GetString() : null;

        if (!NasaRightsFilter.IsSafeForDisplay(copyright, rights, description))
        {
            return null;
        }

        var rawMediaType = data.TryGetProperty("media_type", out var mtProp) ? mtProp.GetString() : "image";
        var mediaType = string.Equals(rawMediaType, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";

        var center = data.TryGetProperty("center", out var centerProp) ? centerProp.GetString() : null;
        var photographer = data.TryGetProperty("photographer", out var photoProp) ? photoProp.GetString() : null;

        // Resolve thumbnail from links array
        string? thumbnailUrl = null;
        if (itemEl.TryGetProperty("links", out var linksArr) &&
            linksArr.ValueKind == JsonValueKind.Array &&
            linksArr.GetArrayLength() > 0)
        {
            var firstLink = linksArr[0];
            if (firstLink.TryGetProperty("href", out var hrefProp))
            {
                thumbnailUrl = hrefProp.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return null;
        }

        // Derive high-resolution download URL from thumbnail convention
        string downloadUrl;
        string previewUrl;
        if (mediaType == "video")
        {
            // Video assets download URL is resolved via asset endpoint or derived .mp4
            downloadUrl = thumbnailUrl.Replace("~thumb.jpg", "~orig.mp4", StringComparison.OrdinalIgnoreCase);
            previewUrl = thumbnailUrl;
        }
        else
        {
            downloadUrl = thumbnailUrl.Replace("~thumb.jpg", "~orig.jpg", StringComparison.OrdinalIgnoreCase);
            previewUrl = thumbnailUrl.Replace("~thumb.jpg", "~medium.jpg", StringComparison.OrdinalIgnoreCase);
        }

        var creatorDisplay = !string.IsNullOrWhiteSpace(photographer)
            ? photographer
            : !string.IsNullOrWhiteSpace(center)
                ? $"NASA / {center}"
                : "NASA";

        var attributionText = $"Source: NASA ({nasaId})";
        var sourceUrl = $"https://images.nasa.gov/details/{Uri.EscapeDataString(nasaId)}";

        var tags = new List<string>();
        if (data.TryGetProperty("keywords", out var kwArr) && kwArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var kw in kwArr.EnumerateArray())
            {
                var val = kw.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    tags.Add(val.Trim());
                }
            }
        }

        DateTimeOffset? publishedAt = null;
        if (data.TryGetProperty("date_created", out var dcProp) &&
            DateTimeOffset.TryParse(dcProp.GetString(), out var dt))
        {
            publishedAt = dt;
        }

        return new OnlineWallpaperItem(
            ProviderId: ProviderId,
            RemoteId: nasaId,
            Title: title,
            Description: description,
            MediaType: mediaType,
            Width: null,
            Height: null,
            AspectRatio: null,
            ThumbnailUrl: thumbnailUrl,
            PreviewUrl: previewUrl,
            DownloadUrl: downloadUrl,
            CreatorName: creatorDisplay,
            CreatorUrl: "https://www.nasa.gov",
            SourceName: "NASA Image and Video Library",
            SourceUrl: sourceUrl,
            LicenseCode: WallpaperLicensePolicy.CodePublicDomain,
            LicenseName: "Public Domain (US Government Work)",
            LicenseUrl: "https://www.nasa.gov/multimedia/guidelines/index.html",
            AttributionText: attributionText,
            IsPublicDomain: true,
            RequiresAttribution: false,
            CommercialUseAllowed: true,
            DerivativeUseAllowed: true,
            Mature: false,
            Tags: tags,
            PublishedAt: publishedAt);
    }
}
