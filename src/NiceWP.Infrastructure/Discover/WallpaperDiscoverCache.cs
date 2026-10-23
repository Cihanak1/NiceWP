using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Manages short-lived disk metadata caching for online discover queries in %LocalAppData%\NiceWP\discover-cache\.
/// Ensures API terms of service compliance without caching full media binaries.
/// </summary>
public sealed class WallpaperDiscoverCache
{
    private readonly string _cacheDirectory;
    private readonly TimeSpan _cacheDuration;
    private readonly object _lock = new();

    public WallpaperDiscoverCache(string? cacheDirectory = null, TimeSpan? cacheDuration = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NiceWP",
            "discover-cache");

        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(20);
    }

    public async Task<WallpaperSearchResult?> TryGetAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
    {
        var cacheFilePath = GetCacheFilePath(query);
        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(cacheFilePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheDuration)
            {
                // Expired
                File.Delete(cacheFilePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(cacheFilePath, Encoding.UTF8, cancellationToken);
            return JsonSerializer.Deserialize<WallpaperSearchResult>(json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Discover cache read error: {ex}");
            return null;
        }
    }

    public async Task SetAsync(WallpaperSearchQuery query, WallpaperSearchResult result, CancellationToken cancellationToken)
    {
        if (result.Items.Count == 0)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }

            var cacheFilePath = GetCacheFilePath(query);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });

            var tempPath = Path.Combine(_cacheDirectory, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);

            File.Move(tempPath, cacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Discover cache write error: {ex}");
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                var files = Directory.GetFiles(_cacheDirectory, "*.json");
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to clear discover cache: {ex}");
        }
    }

    private string GetCacheFilePath(WallpaperSearchQuery query)
    {
        var rawKey = $"{query.Query.Trim().ToLowerInvariant()}|{query.ProviderId}|{query.Orientation}|{query.MinResolution}|{query.MediaType}|{query.LicenseFilter}|{query.Page}|{query.PageSize}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"q_{hash}.json");
    }
}
