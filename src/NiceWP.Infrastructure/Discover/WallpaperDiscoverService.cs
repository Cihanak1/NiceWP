using System.Diagnostics;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Aggregates multiple open-access wallpaper providers with bounded concurrency,
/// result deduplication, query caching, and resilient partial failure handling.
/// </summary>
public sealed class WallpaperDiscoverService
{
    private readonly IReadOnlyList<IWallpaperProvider> _providers;
    private readonly WallpaperDiscoverCache _cache;
    private readonly SemaphoreSlim _concurrencyThrottler = new(3, 3);

    public IReadOnlyList<IWallpaperProvider> Providers => _providers;

    public WallpaperDiscoverCache Cache => _cache;

    public WallpaperDiscoverService(
        IEnumerable<IWallpaperProvider>? providers = null,
        WallpaperDiscoverCache? cache = null)
    {
        _cache = cache ?? new WallpaperDiscoverCache();

        if (providers != null)
        {
            _providers = providers.ToList();
        }
        else
        {
            _providers =
            [
                new OpenverseWallpaperProvider(),
                new WikimediaWallpaperProvider(),
                new NasaWallpaperProvider()
            ];
        }
    }

    /// <summary>
    /// Executes a federated search across enabled providers with deduplication and caching.
    /// </summary>
    public async Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return WallpaperSearchResult.Empty("all");
        }

        // 1. Check cache first
        var cached = await _cache.TryGetAsync(query, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        // 2. Select targeted providers
        var activeProviders = SelectTargetProviders(query.ProviderId);
        if (activeProviders.Count == 0)
        {
            return WallpaperSearchResult.Empty("none", "No enabled providers matched the request.");
        }

        // 3. Parallel bounded execution
        var tasks = activeProviders.Select(provider => ExecuteProviderQueryAsync(provider, query, cancellationToken));
        var results = await Task.WhenAll(tasks);

        // 4. Merge, deduplicate, and rank
        var aggregatedItems = new List<OnlineWallpaperItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        ProviderRateLimitState? firstRateLimit = null;
        int totalReportedCount = 0;
        bool hasMore = false;

        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errors.Add($"{result.ProviderId}: {result.ErrorMessage}");
            }

            if (result.RateLimit?.IsLimited == true && firstRateLimit == null)
            {
                firstRateLimit = result.RateLimit;
            }

            if (result.TotalCount.HasValue)
            {
                totalReportedCount += result.TotalCount.Value;
            }

            if (result.HasMore)
            {
                hasMore = true;
            }

            foreach (var item in result.Items)
            {
                var dedupKey = GetItemDedupKey(item);
                if (seenUrls.Add(dedupKey))
                {
                    aggregatedItems.Add(item);
                }
            }
        }

        // Interleave / balance items fairly between providers if searching all
        if (activeProviders.Count > 1)
        {
            aggregatedItems = InterleaveProviderItems(results);
        }

        // Determine if all active providers failed due to offline / DNS network errors
        var allFailed = results.All(r => !string.IsNullOrWhiteSpace(r.ErrorMessage) && r.Items.Count == 0);
        var isNetworkOffline = allFailed && errors.All(e =>
            e.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("Could not connect", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase));

        string? compositeError = isNetworkOffline
            ? "No internet connection."
            : errors.Count > 0 ? string.Join(" | ", errors) : null;

        var finalResult = new WallpaperSearchResult(
            ProviderId: query.ProviderId ?? "all",
            Items: aggregatedItems,
            TotalCount: totalReportedCount > 0 ? totalReportedCount : aggregatedItems.Count,
            Page: query.Page,
            HasMore: hasMore,
            RateLimit: firstRateLimit,
            ErrorMessage: compositeError);

        // Cache successful non-empty results
        if (aggregatedItems.Count > 0)
        {
            await _cache.SetAsync(query, finalResult, cancellationToken);
        }

        return finalResult;
    }

    private List<IWallpaperProvider> SelectTargetProviders(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerId, "tümü", StringComparison.OrdinalIgnoreCase))
        {
            return _providers.ToList();
        }

        var normalized = providerId.Trim();
        return _providers.Where(p => string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<WallpaperSearchResult> ExecuteProviderQueryAsync(
        IWallpaperProvider provider,
        WallpaperSearchQuery query,
        CancellationToken cancellationToken)
    {
        await _concurrencyThrottler.WaitAsync(cancellationToken);
        try
        {
            return await provider.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Provider {provider.Id} query execution failed: {ex}");
            return WallpaperSearchResult.Empty(provider.Id, ex.Message);
        }
        finally
        {
            _concurrencyThrottler.Release();
        }
    }

    private static string GetItemDedupKey(OnlineWallpaperItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            return item.DownloadUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return item.SourceUrl.Trim();
        }

        return $"{item.ProviderId}_{item.RemoteId}";
    }

    private static List<OnlineWallpaperItem> InterleaveProviderItems(WallpaperSearchResult[] results)
    {
        var interleaved = new List<OnlineWallpaperItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var providerQueues = results
            .Where(r => r.Items.Count > 0)
            .Select(r => new Queue<OnlineWallpaperItem>(r.Items))
            .ToList();

        while (providerQueues.Any(q => q.Count > 0))
        {
            foreach (var queue in providerQueues)
            {
                if (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    var key = GetItemDedupKey(item);
                    if (seenKeys.Add(key))
                    {
                        interleaved.Add(item);
                    }
                }
            }
        }

        return interleaved;
    }
}
