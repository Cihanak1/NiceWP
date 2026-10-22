using System.Collections.Concurrent;
using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Analysis;

public sealed class AnalysisProgressEventArgs : EventArgs
{
    public int ItemsRemaining { get; init; }
    public int TotalEnqueued { get; init; }
    public bool IsAnalyzing => ItemsRemaining > 0;
}

/// <summary>
/// Orchestrates background, bounded-concurrency wallpaper analysis with persistent caching,
/// stale file invalidation, and progress reporting.
/// </summary>
public sealed class WallpaperAnalysisService : IDisposable
{
    private const int MaxConcurrentWorkers = 2;

    private readonly WallpaperAnalysisStore _store;
    private readonly WallpaperThumbnailService? _thumbnailService;
    private readonly SemaphoreSlim _workerThrottle = new(MaxConcurrentWorkers, MaxConcurrentWorkers);
    private readonly ConcurrentQueue<WallpaperLibraryItem> _queue = new();
    private readonly HashSet<string> _enqueuedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _workerCts;
    private Task? _processingTask;
    private int _totalBatchCount;
    private bool _isDisposed;

    public WallpaperAnalysisStore Store => _store;

    public event EventHandler<AnalysisProgressEventArgs>? ProgressChanged;
    public event EventHandler<WallpaperAnalysisMetadata>? AnalysisCompleted;

    public WallpaperAnalysisService(
        WallpaperAnalysisStore? store = null,
        WallpaperThumbnailService? thumbnailService = null)
    {
        _store = store ?? new WallpaperAnalysisStore();
        _thumbnailService = thumbnailService;
    }

    /// <summary>
    /// Initializes the store by loading persisted analysis cache from disk.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets analysis metadata for a wallpaper item. If cached and fresh, returns immediately without re-analyzing.
    /// If missing or stale, computes analysis asynchronously.
    /// </summary>
    public async Task<WallpaperAnalysisMetadata?> GetOrAnalyzeAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.FileExists())
        {
            return null;
        }

        if (_store.TryGet(item.Id, out var cached) && WallpaperAnalysisStore.IsMetadataFresh(cached, item.FilePath))
        {
            return cached;
        }

        await _workerThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring worker slot
            if (_store.TryGet(item.Id, out cached) && WallpaperAnalysisStore.IsMetadataFresh(cached, item.FilePath))
            {
                return cached;
            }

            var result = await AnalyzeSingleItemAsync(item, cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                _store.Set(result);
                _ = _store.SaveAsync(CancellationToken.None);
                AnalysisCompleted?.Invoke(this, result);
            }
            return result;
        }
        finally
        {
            _workerThrottle.Release();
        }
    }

    /// <summary>
    /// Enqueues multiple library items for non-blocking background analysis.
    /// </summary>
    public void EnqueueBatch(IEnumerable<WallpaperLibraryItem> items)
    {
        if (_isDisposed || items == null) return;

        int addedCount = 0;
        lock (_syncRoot)
        {
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || !item.FileExists())
                {
                    continue;
                }

                // If already fresh in store, skip enqueuing
                if (_store.TryGet(item.Id, out var existing) && WallpaperAnalysisStore.IsMetadataFresh(existing, item.FilePath))
                {
                    continue;
                }

                if (_enqueuedIds.Add(item.Id))
                {
                    _queue.Enqueue(item);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                _totalBatchCount += addedCount;
                NotifyProgress();
                EnsureWorkerRunning();
            }
        }
    }

    private void EnsureWorkerRunning()
    {
        lock (_syncRoot)
        {
            if (_processingTask == null || _processingTask.IsCompleted)
            {
                _workerCts?.Dispose();
                _workerCts = new CancellationTokenSource();
                _processingTask = Task.Run(() => ProcessQueueAsync(_workerCts.Token));
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        bool hasChanges = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WallpaperLibraryItem? nextItem = null;
                lock (_syncRoot)
                {
                    if (_queue.TryDequeue(out nextItem))
                    {
                        _enqueuedIds.Remove(nextItem.Id);
                    }
                }

                if (nextItem == null)
                {
                    break;
                }

                try
                {
                    await _workerThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (nextItem.FileExists())
                        {
                            var meta = await AnalyzeSingleItemAsync(nextItem, cancellationToken).ConfigureAwait(false);
                            if (meta != null)
                            {
                                _store.Set(meta);
                                hasChanges = true;
                                AnalysisCompleted?.Invoke(this, meta);
                            }
                        }
                    }
                    finally
                    {
                        _workerThrottle.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[WallpaperAnalysisService] Failed background item '{nextItem.FilePath}': {ex.Message}");
                }

                NotifyProgress();
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_queue.IsEmpty)
                {
                    _totalBatchCount = 0;
                }
            }
            NotifyProgress();

            if (hasChanges)
            {
                try
                {
                    await _store.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[WallpaperAnalysisService] Failed auto-save cache: {ex.Message}");
                }
            }
        }
    }

    private async Task<WallpaperAnalysisMetadata?> AnalyzeSingleItemAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        if (string.Equals(item.Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase))
        {
            return await VideoWallpaperAnalyzer.AnalyzeAsync(
                wallpaperId: item.Id,
                filePath: item.FilePath,
                thumbnailService: _thumbnailService,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return await ImageWallpaperAnalyzer.AnalyzeAsync(
            wallpaperId: item.Id,
            filePath: item.FilePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void NotifyProgress()
    {
        int remaining;
        int total;
        lock (_syncRoot)
        {
            remaining = _queue.Count;
            total = _totalBatchCount;
        }

        try
        {
            ProgressChanged?.Invoke(this, new AnalysisProgressEventArgs
            {
                ItemsRemaining = remaining,
                TotalEnqueued = total
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WallpaperAnalysisService] Progress callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _workerCts?.Cancel();
            _workerCts?.Dispose();
            _workerThrottle.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}
