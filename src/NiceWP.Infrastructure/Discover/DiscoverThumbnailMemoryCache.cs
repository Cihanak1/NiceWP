using System.Diagnostics;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Thread-safe, bounded memory cache with LRU eviction for online discover thumbnails.
/// Prevents memory bloat during extensive scrolling and eliminates duplicate network decodes.
/// </summary>
public sealed class DiscoverThumbnailMemoryCache<T> where T : class
{
    public const int DefaultCapacity = 120;

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, CacheEntry> _entries;
    private readonly int _capacity;
    private int _hitCount;
    private int _missCount;

    private sealed record CacheEntry(T Item, long LastAccessedTicks);

    public int Capacity => _capacity;
    public int Count { get { lock (_syncRoot) return _entries.Count; } }
    public int HitCount => _hitCount;
    public int MissCount => _missCount;

    public DiscoverThumbnailMemoryCache(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(10, capacity);
        _entries = new Dictionary<string, CacheEntry>(_capacity, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string? key, out T? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_syncRoot)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = entry with { LastAccessedTicks = Stopwatch.GetTimestamp() };
                _hitCount++;
                item = entry.Item;
                return true;
            }

            _missCount++;
            return false;
        }
    }

    public void Set(string? key, T item)
    {
        if (string.IsNullOrWhiteSpace(key) || item == null) return;

        lock (_syncRoot)
        {
            if (_entries.ContainsKey(key))
            {
                _entries[key] = new CacheEntry(item, Stopwatch.GetTimestamp());
                return;
            }

            // Evict oldest if capacity reached
            if (_entries.Count >= _capacity)
            {
                string? oldestKey = null;
                long oldestTicks = long.MaxValue;

                foreach (var (k, v) in _entries)
                {
                    if (v.LastAccessedTicks < oldestTicks)
                    {
                        oldestTicks = v.LastAccessedTicks;
                        oldestKey = k;
                    }
                }

                if (oldestKey != null)
                {
                    _entries.Remove(oldestKey);
                }
            }

            _entries[key] = new CacheEntry(item, Stopwatch.GetTimestamp());
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
            _hitCount = 0;
            _missCount = 0;
        }
    }
}
