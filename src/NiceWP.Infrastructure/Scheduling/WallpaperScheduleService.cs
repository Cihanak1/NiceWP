using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Infrastructure.Scheduling;

/// <summary>
/// Low-resource Day/Night schedule scheduler with zero periodic polling overhead.
/// Uses a single one-shot timer for due transitions, respects pause conditions, and prioritizes schedule transitions.
/// </summary>
public sealed class WallpaperScheduleService : IDisposable
{
    private readonly Func<string, WallpaperLibraryItem?> _getItemById;
    private readonly Func<WallpaperLibraryItem, CancellationToken, Task<WallpaperLibraryApplyResult>> _applyItem;
    private readonly Func<CancellationToken, Task<IpcResponse>> _getHostStatus;
    private readonly Action<string, string>? _auditLogger;
    private readonly Action? _onScheduleApplied;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private Timer? _timer;
    private bool _isDisposed;
    private bool _lastKnownIsPaused;

    public ScheduleMode Mode { get; private set; } = ScheduleMode.Off;

    public TimeOnly DayStartTime { get; private set; } = new(8, 0);

    public TimeOnly NightStartTime { get; private set; } = new(20, 0);

    public string? DayWallpaperId { get; private set; }

    public string? NightWallpaperId { get; private set; }

    public DayNightSlot CurrentSlot { get; private set; } = DayNightSlot.None;

    public DayNightSlot NextSlot { get; private set; } = DayNightSlot.None;

    public DateTimeOffset? NextTransitionUtc { get; private set; }

    public bool IsOverdue { get; private set; }
    public Func<bool>? SchedulePrecondition { get; set; }

    public event EventHandler<WallpaperScheduleStatus>? StatusChanged;

    public WallpaperScheduleService(
        Func<string, WallpaperLibraryItem?> getItemById,
        Func<WallpaperLibraryItem, CancellationToken, Task<WallpaperLibraryApplyResult>> applyItem,
        Func<CancellationToken, Task<IpcResponse>> getHostStatus,
        Action<string, string>? auditLogger = null,
        Action? onScheduleApplied = null)
    {
        _getItemById = getItemById ?? throw new ArgumentNullException(nameof(getItemById));
        _applyItem = applyItem ?? throw new ArgumentNullException(nameof(applyItem));
        _getHostStatus = getHostStatus ?? throw new ArgumentNullException(nameof(getHostStatus));
        _auditLogger = auditLogger;
        _onScheduleApplied = onScheduleApplied;
    }

    /// <summary>
    /// Initializes schedule configuration from persisted settings.
    /// </summary>
    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mode = ScheduleModeExtensions.ParseMode(settings.ScheduleMode);
        var dayStart = TimeOnly.TryParse(settings.EffectiveDayStartTime, out var ds) ? ds : new TimeOnly(8, 0);
        var nightStart = TimeOnly.TryParse(settings.EffectiveNightStartTime, out var ns) ? ns : new TimeOnly(20, 0);

        Configure(mode, dayStart, nightStart, settings.DayWallpaperId, settings.NightWallpaperId);
    }

    /// <summary>
    /// Configures the schedule settings and updates the one-shot transition timer.
    /// </summary>
    public void Configure(
        ScheduleMode mode,
        TimeOnly dayStart,
        TimeOnly nightStart,
        string? dayWallpaperId,
        string? nightWallpaperId)
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;

            Mode = mode;
            DayStartTime = dayStart;
            NightStartTime = nightStart;
            DayWallpaperId = string.IsNullOrWhiteSpace(dayWallpaperId) ? null : dayWallpaperId.Trim();
            NightWallpaperId = string.IsNullOrWhiteSpace(nightWallpaperId) ? null : nightWallpaperId.Trim();
            IsOverdue = false;

            if (Mode == ScheduleMode.Off)
            {
                StopTimerLocked();
                CurrentSlot = DayNightSlot.None;
                NextSlot = DayNightSlot.None;
                LogAudit("ScheduleStopped", "Wallpaper schedule disabled.");
            }
            else
            {
                ScheduleNextTimerLocked();
                LogAudit("ScheduleStarted", $"Day/Night schedule enabled. Day: {DayStartTime:HH:mm}, Night: {NightStartTime:HH:mm}.");
            }
        }

        NotifyStatusChanged();
    }

    public WallpaperScheduleStatus GetStatus()
    {
        lock (_syncLock)
        {
            return new WallpaperScheduleStatus
            {
                Mode = Mode,
                CurrentSlot = CurrentSlot,
                NextSlot = NextSlot,
                NextTransitionUtc = NextTransitionUtc,
                IsOverdue = IsOverdue,
                CurrentWallpaperId = CurrentSlot == DayNightSlot.Day ? DayWallpaperId : NightWallpaperId,
                NextWallpaperId = NextSlot == DayNightSlot.Day ? DayWallpaperId : NightWallpaperId
            };
        }
    }

    /// <summary>
    /// Evaluates whether the schedule requires applying the current slot (e.g. on resume or startup).
    /// </summary>
    public async Task EvaluateAndApplyCurrentSlotAsync(CancellationToken cancellationToken = default)
    {
        if (Mode == ScheduleMode.Off || _isDisposed) return;

        WallpaperLibraryItem? itemToApply = null;
        DayNightSlot slot;

        lock (_syncLock)
        {
            var now = DateTimeOffset.UtcNow;
            var info = DayNightScheduleResolver.CalculateNextTransition(now, DayStartTime, NightStartTime);
            CurrentSlot = info.CurrentSlot;
            NextSlot = info.NextSlot;
            NextTransitionUtc = info.NextTransitionUtc;
            slot = CurrentSlot;

            var targetId = slot == DayNightSlot.Day ? DayWallpaperId : NightWallpaperId;
            if (!string.IsNullOrEmpty(targetId))
            {
                itemToApply = _getItemById(targetId);
            }
        }

        if (itemToApply != null)
        {
            try
            {
                var result = await _applyItem(itemToApply, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    _onScheduleApplied?.Invoke();
                    LogAudit("ScheduleApplied", $"Applied {slot} wallpaper: {itemToApply.DisplayName}.");
                }
            }
            catch (Exception ex)
            {
                LogAudit("ScheduleFailed", $"Failed applying {slot} wallpaper: {ex.Message}");
            }
        }
        else
        {
            LogAudit("ScheduleWallpaperMissing", $"No wallpaper configured for {slot} slot.");
        }

        lock (_syncLock)
        {
            IsOverdue = false;
            ScheduleNextTimerLocked();
        }

        NotifyStatusChanged();
    }

    /// <summary>
    /// Notifies the scheduler of host pause/resume state changes.
    /// When resumed, any overdue transitions are applied once.
    /// </summary>
    public void NotifyPauseState(bool isPaused)
    {
        bool wasOverdue;
        lock (_syncLock)
        {
            _lastKnownIsPaused = isPaused;
            wasOverdue = !isPaused && IsOverdue;
        }

        if (wasOverdue)
        {
            _ = Task.Run(async () =>
            {
                await EvaluateAndApplyCurrentSlotAsync().ConfigureAwait(false);
            });
        }
    }

    private void ScheduleNextTimerLocked()
    {
        if (Mode == ScheduleMode.Off)
        {
            StopTimerLocked();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var info = DayNightScheduleResolver.CalculateNextTransition(now, DayStartTime, NightStartTime);

        CurrentSlot = info.CurrentSlot;
        NextSlot = info.NextSlot;
        NextTransitionUtc = info.NextTransitionUtc;

        var dueMs = (long)info.DueIn.TotalMilliseconds;
        // Cap timer to valid range [100ms, Int32.MaxValue - 2]
        if (dueMs < 100) dueMs = 100;
        if (dueMs > int.MaxValue - 2) dueMs = int.MaxValue - 2;

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
        NextTransitionUtc = null;
    }

    private void OnTimerCallback(object? state)
    {
        if (_isDisposed || Mode == ScheduleMode.Off) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Check if host is currently paused
                bool isPaused = false;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var status = await _getHostStatus(cts.Token).ConfigureAwait(false);
                    if (status.Success)
                    {
                        isPaused = status.IsPaused ||
                            string.Equals(status.PlaybackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    // Fall back to last known paused state
                    lock (_syncLock) { isPaused = _lastKnownIsPaused; }
                }

                var isAllowed = SchedulePrecondition == null || SchedulePrecondition();
                if (isPaused || !isAllowed)
                {
                    lock (_syncLock)
                    {
                        IsOverdue = true;
                        LogAudit("ScheduleDeferredPaused", "Schedule transition deferred due to paused playback.");
                    }
                    NotifyStatusChanged();
                    return;
                }

                await EvaluateAndApplyCurrentSlotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Schedule] Unhandled exception in timer callback: {ex}");
            }
        });
    }

    private void LogAudit(string eventType, string details)
    {
        try
        {
            _auditLogger?.Invoke(eventType, details);
        }
        catch
        {
            // Logging should never throw
        }
    }

    private void NotifyStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, GetStatus());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Schedule] Error raising StatusChanged: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _isDisposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _syncLock.Dispose();
    }
}
