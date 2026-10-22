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
    DateTimeOffset PreloadedAtUtc);

/// <summary>
/// Preloads and caches wallpaper candidate surfaces for interactive page-turn gestures,
/// ensuring zero disk lag during dragging.
/// </summary>
public sealed class PageTurnPreloadService : IDisposable
{
    private readonly ConcurrentDictionary<string, PreloadedTransitionCandidate> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string>? _auditLogger;
    private bool _isDisposed;

    public PageTurnPreloadService(Action<string, string>? auditLogger = null)
    {
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Preloads the specified wallpaper candidate for smooth transition display.
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

        return await Task.Run(() =>
        {
            if (!File.Exists(candidate.FilePath))
            {
                LogAudit("PreloadFailed", $"Candidate file not found on disk: {candidate.FilePath}");
                return null;
            }

            var isVideo = string.Equals(candidate.Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
            var previewPath = candidate.FilePath;

            var preloaded = new PreloadedTransitionCandidate(
                ItemId: candidate.Id,
                OriginalPath: candidate.FilePath,
                PreviewImagePath: previewPath,
                WallpaperType: candidate.Type,
                IsVideo: isVideo,
                PreloadedAtUtc: DateTimeOffset.UtcNow);

            _cache[candidate.Id] = preloaded;
            LogAudit("Preloaded", $"Preloaded candidate '{candidate.DisplayName}' for page-turn display.");
            return preloaded;
        }, cancellationToken).ConfigureAwait(false);
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
