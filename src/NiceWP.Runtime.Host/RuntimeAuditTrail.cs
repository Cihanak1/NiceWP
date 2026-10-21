using NiceWP.Core;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Fixed-capacity in-memory circular buffer for runtime lifecycle and health events.
/// Strictly local, non-persistent, thread-safe, and bounded to prevent memory growth.
/// </summary>
public sealed class RuntimeAuditTrail
{
    public const int DefaultCapacity = 50;

    private readonly object syncRoot = new();
    private readonly RuntimeAuditEvent[] buffer;
    private readonly int capacity;
    private int head;
    private int count;

    public int Capacity => capacity;
    public int Count { get { lock (syncRoot) return count; } }

    public RuntimeAuditTrail(int capacity = DefaultCapacity)
    {
        this.capacity = capacity > 0 ? capacity : DefaultCapacity;
        buffer = new RuntimeAuditEvent[this.capacity];
    }

    /// <summary>
    /// Records a new audit event into the circular ring buffer.
    /// </summary>
    public void Record(string eventType, string message)
    {
        var evt = new RuntimeAuditEvent(DateTimeOffset.UtcNow, eventType, message);
        lock (syncRoot)
        {
            buffer[head] = evt;
            head = (head + 1) % capacity;
            if (count < capacity)
            {
                count++;
            }
        }
    }

    public void RecordProfileChanged(AdaptiveDesktopProfile profile, string reason) =>
        Record("AdaptiveProfile", $"Switched to {profile}: {reason}");

    public void RecordDeepFrozen(string? wallpaperPath, string reason) =>
        Record("DeepFreeze", $"Resources released: {reason}");

    public void RecordDeepFreezeRestored(string? wallpaperPath) =>
        Record("DeepFreeze", "Playback and hardware decoders restored");

    public void RecordResourceGuardEngaged(string reason) =>
        Record("ResourceGuard", $"Throttled background load: {reason}");

    public void RecordAppRuleMatched(string processName, AdaptiveDesktopProfile profile) =>
        Record("AppRule", $"Matched {processName} -> {profile}");

    /// <summary>
    /// Returns the most recent events in chronological order (oldest to newest).
    /// </summary>
    public IReadOnlyList<RuntimeAuditEvent> GetRecentEvents(int maxCount = DefaultCapacity)
    {
        lock (syncRoot)
        {
            if (count == 0)
            {
                return Array.Empty<RuntimeAuditEvent>();
            }

            var resultCount = Math.Min(count, Math.Max(1, maxCount));
            var list = new List<RuntimeAuditEvent>(resultCount);

            // Starting index of the oldest entry in the window to return
            var startIndex = (head - resultCount + capacity) % capacity;
            for (var i = 0; i < resultCount; i++)
            {
                var idx = (startIndex + i) % capacity;
                list.Add(buffer[idx]);
            }

            return list;
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            Array.Clear(buffer, 0, buffer.Length);
            head = 0;
            count = 0;
        }
    }
}
