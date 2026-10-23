using NiceWP.Core.Personalization;
using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Infrastructure.Rotation;

/// <summary>
/// Orchestrates wallpaper rotation (All Library, Favorites, Sequential, Shuffle)
/// with background timer scheduling, failure safeguards, and pause/efficiency compatibility.
/// </summary>
public sealed class WallpaperRotationService : IDisposable
{
    private const int MaxHistoryCount = 20;
    private const int MaxApplyAttempts = 5;

    private readonly Func<IReadOnlyList<WallpaperLibraryItem>> _getItems;
    private readonly Func<WallpaperLibraryItem, CancellationToken, Task<WallpaperLibraryApplyResult>> _applyItem;
    private readonly Func<CancellationToken, Task<IpcResponse>> _getHostStatus;
    private readonly Func<CancellationToken, Task<IpcResponse>>? _pauseHost;
    private readonly Func<CancellationToken, Task<IpcResponse>>? _resumeHost;
    private readonly IRotationRandomProvider _random;
    private readonly Action<string, string>? _auditLogger;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly List<WallpaperRotationHistoryEntry> _history = new();
    private readonly List<string> _shuffleDeck = new();

    private Timer? _timer;
    private bool _isDisposed;
    private bool _lastKnownIsPaused;
    private string? _lastKnownPauseReasons;

    public RotationMode Mode { get; private set; } = RotationMode.Off;
    public RotationOrder Order { get; private set; } = RotationOrder.Sequential;
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(30);
    public DateTimeOffset? NextDueTimeUtc { get; private set; }
    public bool IsOverdue { get; private set; }
    public string? CurrentItemId { get; private set; }
    public string? CurrentWallpaperPath { get; private set; }
    public Func<bool>? RotationPrecondition { get; set; }
    public Func<SmartSceneContext>? SmartSceneContextProvider { get; set; }
    public Func<string, WallpaperAnalysisMetadata?>? MetadataLookup { get; set; }
    public Func<PersonalizationProfile?>? PersonalizationProfileProvider { get; set; }
    public Func<string, bool>? ExclusionFilter { get; set; }
    public SmartScenes.ScoredWallpaperCandidate? LastSmartSelection { get; private set; }

    public IReadOnlyList<WallpaperRotationHistoryEntry> History
    {
        get
        {
            lock (_history)
            {
                return _history.ToList();
            }
        }
    }

    public event EventHandler<WallpaperRotationResult>? WallpaperRotated;
    public event EventHandler<WallpaperRotationStatus>? StatusChanged;

    public WallpaperRotationService(
        Func<IReadOnlyList<WallpaperLibraryItem>> getItems,
        Func<WallpaperLibraryItem, CancellationToken, Task<WallpaperLibraryApplyResult>> applyItem,
        Func<CancellationToken, Task<IpcResponse>> getHostStatus,
        Func<CancellationToken, Task<IpcResponse>>? pauseHost = null,
        Func<CancellationToken, Task<IpcResponse>>? resumeHost = null,
        IRotationRandomProvider? randomProvider = null,
        Action<string, string>? auditLogger = null)
    {
        _getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
        _applyItem = applyItem ?? throw new ArgumentNullException(nameof(applyItem));
        _getHostStatus = getHostStatus ?? throw new ArgumentNullException(nameof(getHostStatus));
        _pauseHost = pauseHost;
        _resumeHost = resumeHost;
        _random = randomProvider ?? DefaultRotationRandomProvider.Instance;
        _auditLogger = auditLogger;
    }

    public async Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Mode = RotationModeExtensions.ParseMode(settings.RotationMode);
            Order = RotationOrderExtensions.ParseOrder(settings.RotationOrder);
            Interval = TimeSpan.FromMinutes(AppSettings.NormalizeRotationIntervalMinutes(settings.RotationIntervalMinutes));
            CurrentItemId = settings.LastRotationItemId;
            CurrentWallpaperPath = settings.LastWallpaperPath;

            if (Mode != RotationMode.Off)
            {
                ScheduleNextTimerLocked();
                LogAudit("RotationStarted", $"Rotation initialized in {Mode} ({Order}) mode with {Interval.TotalMinutes}m interval.");
            }
            else
            {
                StopTimerLocked();
            }
        }
        finally
        {
            _syncLock.Release();
        }

        NotifyStatusChanged();
    }

    public async Task UpdateSettingsAsync(string modeStr, string orderStr, int intervalMinutes, CancellationToken cancellationToken = default)
    {
        var newMode = RotationModeExtensions.ParseMode(modeStr);
        var newOrder = RotationOrderExtensions.ParseOrder(orderStr);
        var newInterval = TimeSpan.FromMinutes(AppSettings.NormalizeRotationIntervalMinutes(intervalMinutes));

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modeChanged = Mode != newMode;
            var orderChanged = Order != newOrder;
            var intervalChanged = Interval != newInterval;

            Mode = newMode;
            Order = newOrder;
            Interval = newInterval;

            if (orderChanged)
            {
                _shuffleDeck.Clear();
            }

            if (Mode == RotationMode.Off)
            {
                StopTimerLocked();
                IsOverdue = false;
                if (modeChanged)
                {
                    LogAudit("RotationStopped", "Wallpaper rotation disabled by user.");
                }
            }
            else
            {
                ScheduleNextTimerLocked();
                if (modeChanged)
                {
                    LogAudit("RotationStarted", $"Wallpaper rotation enabled: {Mode} ({Order}), {Interval.TotalMinutes}m.");
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }

        NotifyStatusChanged();
    }

    public void NotifyCurrentWallpaper(string? path, string? id = null)
    {
        CurrentWallpaperPath = path;
        if (!string.IsNullOrWhiteSpace(id))
        {
            CurrentItemId = id;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            var item = _getItems().FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                CurrentItemId = item.Id;
            }
        }
        NotifyStatusChanged();
    }

    public void NotifyHostStatus(bool isPaused, string? pauseReasons)
    {
        _lastKnownIsPaused = isPaused;
        _lastKnownPauseReasons = pauseReasons;
        NotifyStatusChanged();
    }

    public void NotifyLibraryChanged()
    {
        lock (_shuffleDeck)
        {
            var candidates = GetEligibleCandidates(Mode).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _shuffleDeck.RemoveAll(id => !candidates.Contains(id));
        }
        NotifyStatusChanged();
    }

    /// <summary>
    /// Resets the rotation timer to a full interval (called when an external action, such as a schedule transition, applies a wallpaper).
    /// </summary>
    public void ResetTimer()
    {
        lock (_syncLock)
        {
            if (_isDisposed || Mode == RotationMode.Off) return;
            ScheduleNextTimerLocked();
        }
        NotifyStatusChanged();
    }

    public Task<WallpaperRotationResult> RotateNextAsync(string trigger = "Next", CancellationToken cancellationToken = default) =>
        RotateCoreAsync(isNext: true, trigger: trigger, cancellationToken: cancellationToken);

    public Task<WallpaperRotationResult> RotatePreviousAsync(string trigger = "Previous", CancellationToken cancellationToken = default) =>
        RotateCoreAsync(isNext: false, trigger: trigger, cancellationToken: cancellationToken);

    public async Task CheckOverdueOnResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || Mode == RotationMode.Off || !IsOverdue) return;

        IpcResponse status;
        try
        {
            status = await _getHostStatus(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Rotation] Failed to query host status for overdue resume: {ex}");
            return;
        }

        if (status.Success && !status.IsPaused)
        {
            await RotateCoreAsync(isNext: true, trigger: "Auto", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Peeks the candidate that would be applied on the next rotation step without modifying history or advancing timer.
    /// </summary>
    public WallpaperLibraryItem? PeekNextCandidate()
    {
        var effectiveMode = Mode == RotationMode.Off ? RotationMode.All : Mode;
        var candidates = GetEligibleCandidates(effectiveMode);
        if (candidates.Count == 0) return null;

        return SelectCandidate(candidates, isNext: true, excludedIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
    public WallpaperRotationStatus GetStatus()
    {
        var candidates = GetEligibleCandidates(Mode);
        var totalCount = candidates.Count;
        var currentIndex = -1;

        if (!string.IsNullOrWhiteSpace(CurrentItemId))
        {
            currentIndex = candidates.FindIndex(c => string.Equals(c.Id, CurrentItemId, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(CurrentWallpaperPath))
        {
            currentIndex = candidates.FindIndex(c => string.Equals(c.FilePath, CurrentWallpaperPath, StringComparison.OrdinalIgnoreCase));
        }

        var currentName = currentIndex >= 0 ? candidates[currentIndex].DisplayName : null;

        return new WallpaperRotationStatus(
            Mode: Mode,
            Order: Order,
            Interval: Interval,
            NextDueTimeUtc: NextDueTimeUtc,
            IsOverdue: IsOverdue,
            TotalEligibleCount: totalCount,
            CurrentIndex: currentIndex,
            CurrentItemId: CurrentItemId,
            CurrentWallpaperName: currentName,
            IsPaused: _lastKnownIsPaused,
            PauseReasons: _lastKnownPauseReasons);
    }

    private async Task<WallpaperRotationResult> RotateCoreAsync(bool isNext, string trigger, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Evaluate candidates for the active mode (or All if Off)
            var effectiveMode = Mode == RotationMode.Off ? RotationMode.All : Mode;
            var candidates = GetEligibleCandidates(effectiveMode);

            if (candidates.Count == 0)
            {
                var emptyResult = new WallpaperRotationResult(false, "No eligible wallpapers found for rotation.", Skipped: true);
                RecordHistory(string.Empty, "None", trigger, false, emptyResult.Message);
                LogAudit("RotationSkippedEmpty", "No eligible non-missing wallpapers available.");
                return emptyResult;
            }

            // Query host status
            IpcResponse hostStatus;
            try
            {
                hostStatus = await _getHostStatus(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Rotation] Failed to query host status: {ex}");
                hostStatus = IpcResponse.Fail(string.Empty, "Runtime unavailable.");
            }

            _lastKnownIsPaused = hostStatus.IsPaused;
            _lastKnownPauseReasons = hostStatus.PauseReasons;

            // If auto-triggered and runtime is paused, defer
            if (string.Equals(trigger, "Auto", StringComparison.OrdinalIgnoreCase) && (hostStatus.IsPaused || (RotationPrecondition != null && !RotationPrecondition())))
            {
                IsOverdue = true;
                LogAudit("RotationPausedDeferred", $"Rotation deferred because host is paused ({hostStatus.PauseReasons ?? "Unknown"}).");
                return new WallpaperRotationResult(false, "Rotation deferred while wallpaper is paused.", Skipped: true);
            }

            var wasManualPaused = hostStatus.IsPaused && (hostStatus.PauseReasons?.Contains(IpcConstants.PauseReasonManual, StringComparison.OrdinalIgnoreCase) == true);

            // Candidate iteration with failure handling
            var maxAttempts = Math.Min(candidates.Count, MaxApplyAttempts);
            var attemptedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var candidate = SelectCandidate(candidates, isNext, attemptedIds);
                if (candidate == null)
                {
                    break;
                }

                attemptedIds.Add(candidate.Id);

                // Check file existence
                if (!candidate.FileExists())
                {
                    RecordHistory(candidate.Id, candidate.DisplayName, trigger, false, "File not found on disk.");
                    LogAudit("RotationSkippedMissing", $"Skipped missing candidate: {candidate.DisplayName}");
                    continue;
                }

                // Attempt to apply
                WallpaperLibraryApplyResult applyResult;
                try
                {
                    applyResult = await _applyItem(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Rotation] Exception applying candidate {candidate.DisplayName}: {ex}");
                    applyResult = new WallpaperLibraryApplyResult(false, ex.Message);
                }

                if (applyResult.Success)
                {
                    CurrentItemId = candidate.Id;
                    CurrentWallpaperPath = candidate.FilePath;
                    IsOverdue = false;

                    // If manual pause was active and this was a user Next/Previous, keep paused state
                    if (wasManualPaused && _pauseHost != null)
                    {
                        try
                        {
                            await _pauseHost(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception pauseEx)
                        {
                            Trace.WriteLine($"[Rotation] Failed to re-pause after manual transition: {pauseEx}");
                        }
                    }

                    RecordHistory(candidate.Id, candidate.DisplayName, trigger, true);
                    LogAudit("RotationAdvanced", $"Rotated to {candidate.DisplayName} via {trigger}.");

                    if (Mode != RotationMode.Off)
                    {
                        ScheduleNextTimerLocked();
                    }

                    var result = new WallpaperRotationResult(true, $"Applied {candidate.DisplayName}", candidate);
                    WallpaperRotated?.Invoke(this, result);
                    NotifyStatusChanged();
                    return result;
                }
                else
                {
                    RecordHistory(candidate.Id, candidate.DisplayName, trigger, false, applyResult.Message);
                    LogAudit("RotationApplyFailed", $"Failed to apply candidate {candidate.DisplayName}: {applyResult.Message}");
                }
            }

            // All candidate attempts failed -> safe stop
            IsOverdue = false;
            LogAudit("RotationSafeStop", "All attempted rotation candidates failed. Rotation stopped safely.");
            var failResult = new WallpaperRotationResult(false, "All rotation candidates failed to apply.", Stopped: true);
            NotifyStatusChanged();
            return failResult;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private WallpaperLibraryItem? SelectCandidate(
        List<WallpaperLibraryItem> candidates,
        bool isNext,
        HashSet<string> excludedIds)
    {
        var available = candidates.Where(c => !excludedIds.Contains(c.Id)).ToList();
        if (ExclusionFilter != null)
        {
            var nonExcluded = available.Where(c => !ExclusionFilter(c.Id)).ToList();
            if (nonExcluded.Count > 0)
            {
                available = nonExcluded;
            }
        }
        if (available.Count == 0) return null;

        if (Order == RotationOrder.Smart)
        {
            return SelectSmartCandidate(available, isNext);
        }

        if (Order == RotationOrder.Shuffle)
        {
            return SelectShuffleCandidate(available, isNext);
        }

        return SelectSequentialCandidate(candidates, available, isNext);
    }

    private WallpaperLibraryItem SelectSmartCandidate(
        List<WallpaperLibraryItem> availableCandidates,
        bool isNext)
    {
        if (!isNext)
        {
            lock (_history)
            {
                for (var i = _history.Count - 1; i >= 0; i--)
                {
                    var entry = _history[i];
                    if (entry.Success && !string.Equals(entry.ItemId, CurrentItemId, StringComparison.OrdinalIgnoreCase))
                    {
                        var found = availableCandidates.FirstOrDefault(c => string.Equals(c.Id, entry.ItemId, StringComparison.OrdinalIgnoreCase));
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
            }
        }

        IReadOnlyList<string> recentIds;
        lock (_history)
        {
            recentIds = _history
                .Where(h => !string.IsNullOrWhiteSpace(h.ItemId))
                .Select(h => h.ItemId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
        }

        var baseContext = SmartSceneContextProvider?.Invoke() ?? new SmartSceneContext(SmartSceneMode.Automatic);
        var context = baseContext with
        {
            RecentWallpaperIds = recentIds
        };

        var selection = SmartScenes.SmartWallpaperSelector.Select(
            availableCandidates,
            MetadataLookup ?? (_ => null),
            context,
            _random,
            PersonalizationProfileProvider?.Invoke(),
            ExclusionFilter);

        if (selection != null)
        {
            LastSmartSelection = new SmartScenes.ScoredWallpaperCandidate(
                selection.SelectedItem,
                selection.ScoredCandidates.FirstOrDefault(c => c.Item.Id == selection.SelectedItem.Id)?.Metadata,
                selection.Score);

            LogAudit("SmartWallpaperSelected", $"Selected '{selection.SelectedItem.DisplayName}' for {context.EffectiveScene} scene with score {selection.Score.TotalScore} ({selection.Score.ReasonSummary})");
            return selection.SelectedItem;
        }

        return availableCandidates[0];
    }

    private WallpaperLibraryItem SelectSequentialCandidate(
        List<WallpaperLibraryItem> allCandidates,
        List<WallpaperLibraryItem> availableCandidates,
        bool isNext)
    {
        var currentIdx = allCandidates.FindIndex(c =>
            (!string.IsNullOrWhiteSpace(CurrentItemId) && string.Equals(c.Id, CurrentItemId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(CurrentWallpaperPath) && string.Equals(c.FilePath, CurrentWallpaperPath, StringComparison.OrdinalIgnoreCase)));

        if (currentIdx < 0)
        {
            return availableCandidates[0];
        }

        var count = allCandidates.Count;
        for (var i = 1; i <= count; i++)
        {
            var nextIdx = isNext
                ? (currentIdx + i) % count
                : (currentIdx - i + count) % count;

            var target = allCandidates[nextIdx];
            if (availableCandidates.Any(a => string.Equals(a.Id, target.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return target;
            }
        }

        return availableCandidates[0];
    }

    private WallpaperLibraryItem SelectShuffleCandidate(
        List<WallpaperLibraryItem> availableCandidates,
        bool isNext)
    {
        // When going Previous in shuffle mode, try to look back at the most recent successful item from history
        if (!isNext)
        {
            lock (_history)
            {
                for (var i = _history.Count - 1; i >= 0; i--)
                {
                    var entry = _history[i];
                    if (entry.Success && !string.Equals(entry.ItemId, CurrentItemId, StringComparison.OrdinalIgnoreCase))
                    {
                        var found = availableCandidates.FirstOrDefault(c => string.Equals(c.Id, entry.ItemId, StringComparison.OrdinalIgnoreCase));
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
            }
        }

        lock (_shuffleDeck)
        {
            // Clean up deck of any items not in availableCandidates
            var availableIds = availableCandidates.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _shuffleDeck.RemoveAll(id => !availableIds.Contains(id));

            if (_shuffleDeck.Count == 0)
            {
                // Refill deck with all available candidate IDs
                var list = availableCandidates.Select(c => c.Id).ToList();

                // Fisher-Yates shuffle
                for (var i = list.Count - 1; i > 0; i--)
                {
                    var j = _random.Next(i + 1);
                    (list[i], list[j]) = (list[j], list[i]);
                }

                // Avoid immediate repeat when deck has > 1 item
                if (list.Count > 1 && string.Equals(list[0], CurrentItemId, StringComparison.OrdinalIgnoreCase))
                {
                    (list[0], list[1]) = (list[1], list[0]);
                }

                _shuffleDeck.AddRange(list);
            }

            var chosenId = _shuffleDeck[0];
            _shuffleDeck.RemoveAt(0);

            var chosenItem = availableCandidates.FirstOrDefault(c => string.Equals(c.Id, chosenId, StringComparison.OrdinalIgnoreCase));
            return chosenItem ?? availableCandidates[0];
        }
    }

    private List<WallpaperLibraryItem> GetEligibleCandidates(RotationMode mode)
    {
        var all = _getItems();
        var eligible = new List<WallpaperLibraryItem>();

        foreach (var item in all)
        {
            if (item == null) continue;
            if (string.IsNullOrWhiteSpace(item.FilePath)) continue;
            if (!item.FileExists()) continue;

            var isImage = string.Equals(item.Type, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase);
            var isVideo = string.Equals(item.Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
            if (!isImage && !isVideo) continue;

            if (mode == RotationMode.Favorites && !item.IsFavorite)
            {
                continue;
            }

            eligible.Add(item);
        }

        // Stable order by AddedAtUtc then Id
        eligible.Sort((a, b) =>
        {
            var cmp = a.AddedAtUtc.CompareTo(b.AddedAtUtc);
            return cmp != 0 ? cmp : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });

        return eligible;
    }

    private void ScheduleNextTimerLocked()
    {
        if (_isDisposed || Mode == RotationMode.Off)
        {
            StopTimerLocked();
            return;
        }

        NextDueTimeUtc = DateTimeOffset.UtcNow + Interval;
        var dueMs = Math.Max(0, (long)(NextDueTimeUtc.Value - DateTimeOffset.UtcNow).TotalMilliseconds);

        if (_timer == null)
        {
            _timer = new Timer(OnTimerCallback, null, dueMs, Timeout.Infinite);
        }
        else
        {
            _timer.Change(dueMs, Timeout.Infinite);
        }
    }

    private void StopTimerLocked()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        NextDueTimeUtc = null;
    }

    private void OnTimerCallback(object? state)
    {
        if (_isDisposed || Mode == RotationMode.Off) return;

        // Run asynchronously without blocking timer pool thread
        _ = Task.Run(async () =>
        {
            try
            {
                await RotateCoreAsync(isNext: true, trigger: "Auto", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Rotation] Unhandled exception in timer callback: {ex}");
            }
        });
    }

    private void RecordHistory(string itemId, string displayName, string trigger, bool success, string? failureReason = null)
    {
        var entry = new WallpaperRotationHistoryEntry(
            ItemId: itemId,
            DisplayName: displayName,
            AppliedAtUtc: DateTimeOffset.UtcNow,
            Trigger: trigger,
            Success: success,
            FailureReason: failureReason);

        lock (_history)
        {
            _history.Add(entry);
            while (_history.Count > MaxHistoryCount)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private void LogAudit(string eventName, string description)
    {
        Trace.WriteLine($"[Rotation Audit] {eventName}: {description}");
        _auditLogger?.Invoke(eventName, description);
    }

    private void NotifyStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, GetStatus());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Rotation] Error raising StatusChanged: {ex}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _timer?.Dispose();
        _timer = null;
        _syncLock.Dispose();
    }
}
