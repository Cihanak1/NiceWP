using System.Collections.Concurrent;
using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Represents a preloaded wallpaper candidate ready for instantaneous page-turn presentation.
/// </summary>
public sealed record PreloadedTransitionCandidate(
    string ItemId,
    string OriginalPath,
    string PreviewImagePath,
    string WallpaperType,
    bool IsVideo,
    bool IsPosterFrame,
    DateTimeOffset PreloadedAtUtc);

/// <summary>
/// Preloads and caches wallpaper candidate surfaces for interactive page-turn gestures,
/// optimizing video wallpaper previews with lightweight poster frames to avoid decoder conflicts.
/// </summary>
public sealed class PageTurnPreloadService : IDisposable
{
    private readonly ConcurrentDictionary<string, PreloadedTransitionCandidate> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly WallpaperThumbnailService _thumbnailService;
    private readonly Action<string, string>? _auditLogger;
    private bool _isDisposed;

    public PageTurnPreloadService(
        WallpaperThumbnailService? thumbnailService = null,
        Action<string, string>? auditLogger = null)
    {
        _thumbnailService = thumbnailService ?? new WallpaperThumbnailService();
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Preloads the specified wallpaper candidate for smooth transition display.
    /// Video wallpapers are optimized with cached poster frames to prevent concurrent decoder pressure.
    /// </summary>
    public async Task<PreloadedTransitionCandidate?> PreloadCandidateAsync(
        WallpaperLibraryItem? candidate,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || candidate == null || string.IsNullOrWhiteSpace(candidate.FilePath))
        {
            return null;
        }

        if (_cache.TryGetValue(candidate.Id, out var cached) && File.Exists(cached.PreviewImagePath))
        {
            return cached;
        }

        if (!File.Exists(candidate.FilePath))
        {
            LogAudit("PreloadFailed", $"Candidate file not found on disk: {candidate.FilePath}");
            return null;
        }

        var isVideo = string.Equals(candidate.Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
        string previewPath = candidate.FilePath;
        bool isPoster = false;

        if (isVideo)
        {
            try
            {
                // Obtain or generate lightweight video frame thumbnail
                var thumbnailPath = await _thumbnailService.GetOrCreateVideoThumbnailAsync(
                    candidate.FilePath,
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
                {
                    previewPath = thumbnailPath;
                    isPoster = true;
                    LogAudit("VideoPreviewOptimized", $"Resolved lightweight poster frame for video candidate: {candidate.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                LogAudit("VideoPreviewFallback", $"Thumbnail generation failed, falling back to direct path: {ex.Message}");
            }
        }

        var preloaded = new PreloadedTransitionCandidate(
            ItemId: candidate.Id,
            OriginalPath: candidate.FilePath,
            PreviewImagePath: previewPath,
            WallpaperType: candidate.Type,
            IsVideo: isVideo,
            IsPosterFrame: isPoster,
            PreloadedAtUtc: DateTimeOffset.UtcNow);

        if (_cache.Count >= 10)
        {
            var oldest = _cache.OrderBy(kv => kv.Value.PreloadedAtUtc).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _cache.TryRemove(oldest.Key, out _);
            }
        }

        _cache[candidate.Id] = preloaded;
        LogAudit("Preloaded", $"Preloaded candidate '{candidate.DisplayName}' (Type={candidate.Type}, Poster={isPoster}).");
        return preloaded;
    }

    /// <summary>
    /// Gets the cached preview path (lightweight poster frame for video, or original path for image) if available.
    /// </summary>
    public string? GetCachedPreviewPath(string? itemId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) &&
            _cache.TryGetValue(itemId, out var cached) &&
            File.Exists(cached.PreviewImagePath))
        {
            return cached.PreviewImagePath;
        }
        return null;
    }

    /// <summary>
    /// Clears preloaded candidate caches.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Invalidates a specific candidate item from the cache.
    /// </summary>
    public void Invalidate(string itemId)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            _cache.TryRemove(itemId, out _);
        }
    }

    private void LogAudit(string eventType, string details)
    {
        _auditLogger?.Invoke(eventType, details);
        Trace.WriteLine($"[PageTurnPreloadService] {eventType}: {details}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cache.Clear();
    }
}
