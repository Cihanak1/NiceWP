using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Analysis;

/// <summary>
/// Versioned JSON container schema for persisted wallpaper analysis records.
/// </summary>
public sealed class WallpaperAnalysisStoreData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, WallpaperAnalysisMetadata> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Manages thread-safe, atomic persistence of wallpaper analysis metadata at
/// %LocalAppData%\NiceWP\wallpaper-analysis.json.
/// </summary>
public sealed class WallpaperAnalysisStore
{
    public const string DefaultFileName = "wallpaper-analysis.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WallpaperAnalysisMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();
    private CancellationTokenSource? _debounceCts;
    private int _isDirty;

    public string FilePath { get; }
    public bool IsDirty => Volatile.Read(ref _isDirty) != 0;

    public WallpaperAnalysisStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultStorePath()
            : Path.GetFullPath(filePath);
    }

    public static string GetDefaultStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            WallpaperLibraryService.DefaultFolderName,
            DefaultFileName);

    /// <summary>
    /// Loads cached analysis entries from disk into memory.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var data = await JsonSerializer.DeserializeAsync<WallpaperAnalysisStoreData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (data?.Entries != null)
            {
                _cache.Clear();
                foreach (var (k, v) in data.Entries)
                {
                    if (v != null)
                    {
                        _cache[k] = v;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WallpaperAnalysisStore] Failed to load store: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists current in-memory analysis cache atomically to disk.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

            WallpaperAnalysisStoreData data;
            lock (_cache)
            {
                data = new WallpaperAnalysisStoreData
                {
                    Version = WallpaperAnalysisStoreData.CurrentVersion,
                    Entries = new Dictionary<string, WallpaperAnalysisMetadata>(_cache, StringComparer.OrdinalIgnoreCase)
                };
                Interlocked.Exchange(ref _isDirty, 0);
            }

            await using (var tempStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new Utf8JsonWriter(tempStream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, data, JsonOptions);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WallpaperAnalysisStore] Failed to save store: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Retrieves a cached analysis record if present.
    /// </summary>
    public bool TryGet(string wallpaperId, out WallpaperAnalysisMetadata? metadata)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(wallpaperId, out metadata);
        }
    }

    /// <summary>
    /// Stores or updates an analysis record in the in-memory cache.
    /// </summary>
    public void Set(WallpaperAnalysisMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_cache)
        {
            _cache[metadata.WallpaperId] = metadata;
            Interlocked.Exchange(ref _isDirty, 1);
        }
    }

    /// <summary>
    /// Debounces saving metadata to disk to coalesce rapid multiple updates and avoid write amplification.
    /// </summary>
    public void RequestSave(int delayMs = 1000)
    {
        lock (_saveLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        await SaveAsync(token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[WallpaperAnalysisStore] Debounced save failed: {ex.Message}");
                }
            }, token);
        }
    }

    /// <summary>
    /// Removes an analysis record by wallpaper ID.
    /// </summary>
    public bool Remove(string wallpaperId)
    {
        lock (_cache)
        {
            return _cache.Remove(wallpaperId);
        }
    }

    /// <summary>
    /// Returns a snapshot of all cached entries.
    /// </summary>
    public IReadOnlyDictionary<string, WallpaperAnalysisMetadata> GetAll()
    {
        lock (_cache)
        {
            return new Dictionary<string, WallpaperAnalysisMetadata>(_cache, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies if a cached metadata record is fresh and valid against the file on disk.
    /// </summary>
    public static bool IsMetadataFresh(WallpaperAnalysisMetadata? metadata, string filePath)
    {
        if (metadata == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            return !metadata.IsStale(info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return false;
        }
    }
}
