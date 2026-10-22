using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Coordinates the lifecycle, gesture progress, and candidate resolution for
/// the interactive corner page-turn wallpaper transition.
/// </summary>
public sealed class WallpaperTransitionCoordinator : IDisposable
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Func<WallpaperRotationService?> _getRotationService;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<ResourceBudget>? _getResourceBudget;
    private readonly Func<bool>? _isAdaptiveBlocked;
    private readonly Action<string, string>? _auditLogger;

    private CornerGestureContext? _currentContext;
    private CornerDragTracker? _dragTracker;
    private WallpaperLibraryItem? _peekedCandidate;
    private bool _isDisposed;

    public TransitionState State { get; private set; } = TransitionState.Idle;

    public CornerGestureContext? CurrentContext => _currentContext;

    public WallpaperLibraryItem? PeekedCandidate => _peekedCandidate;

    public event Action<TransitionState>? StateChanged;
    public event Action<CornerGestureContext>? GestureUpdated;
    public event Action<WallpaperLibraryItem?>? TransitionCommitted;
    public event Action? TransitionCancelled;

    public WallpaperTransitionCoordinator(
        Func<WallpaperRotationService?> getRotationService,
        Func<AppSettings> getSettings,
        Func<ResourceBudget>? getResourceBudget = null,
        Func<bool>? isAdaptiveBlocked = null,
        Action<string, string>? auditLogger = null)
    {
        _getRotationService = getRotationService ?? throw new ArgumentNullException(nameof(getRotationService));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _getResourceBudget = getResourceBudget;
        _isAdaptiveBlocked = isAdaptiveBlocked;
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Checks whether interactive transitions are currently permissible.
    /// </summary>
    public bool CanInitiateTransition()
    {
        if (_isDisposed) return false;

        var settings = _getSettings();
        if (!settings.InteractiveTransitionEnabled)
        {
            return false;
        }

        var rotation = _getRotationService();
        if (rotation == null || rotation.Mode == RotationMode.Off)
        {
            return false;
        }

        if (_isAdaptiveBlocked != null && _isAdaptiveBlocked())
        {
            return false;
        }

        return State == TransitionState.Idle || State == TransitionState.HoverPreview;
    }

    /// <summary>
    /// Handles pointer hover over the top-right hot corner.
    /// </summary>
    public bool SetHoverPreview(bool isHovered)
    {
        if (_isDisposed) return false;

        if (State == TransitionState.Dragging || State == TransitionState.Completing || State == TransitionState.Cancelling)
        {
            return false;
        }

        if (isHovered)
        {
            if (!CanInitiateTransition()) return false;
            SetState(TransitionState.HoverPreview);
            return true;
        }
        else
        {
            if (State == TransitionState.HoverPreview)
            {
                SetState(TransitionState.Idle);
            }
            return true;
        }
    }

    /// <summary>
    /// Initiates a corner drag gesture from the top-right corner.
    /// </summary>
    public async Task<bool> BeginCornerGestureAsync(
        double startX,
        double startY,
        double screenWidth,
        double screenHeight,
        CancellationToken cancellationToken = default)
    {
        if (!CanInitiateTransition())
        {
            LogAudit("GestureRejected", "Interactive gesture rejected: preconditions not satisfied.");
            return false;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TransitionState.Dragging || State == TransitionState.Completing || State == TransitionState.Cancelling)
            {
                return false;
            }

            var settings = _getSettings();
            var rotation = _getRotationService();
            if (rotation == null) return false;

            // Resolve next candidate preview
            _peekedCandidate = rotation.PeekNextCandidate();
            if (_peekedCandidate == null)
            {
                LogAudit("GestureCancelledNoCandidate", "No eligible next wallpaper candidate available.");
                SetState(TransitionState.Idle);
                return false;
            }

            var sensitivity = settings.EffectiveTransitionSensitivity;
            var effectiveQuality = settings.EffectiveTransitionQuality.ResolveEffective(
                _getResourceBudget?.Invoke() ?? ResourceBudget.Balanced);

            _dragTracker = new CornerDragTracker(
                startX: startX,
                startY: startY,
                screenWidth: screenWidth,
                screenHeight: screenHeight,
                thresholdDistance: sensitivity.GetDragDistanceThreshold());

            var initialMetrics = _dragTracker.TrackPosition(startX, startY, 0.0);

            _currentContext = new CornerGestureContext(
                StartX: startX,
                StartY: startY,
                CurrentX: startX,
                CurrentY: startY,
                ScreenWidth: screenWidth,
                ScreenHeight: screenHeight,
                Progress: initialMetrics.Progress,
                Velocity: initialMetrics.SmoothedVelocity,
                Sensitivity: sensitivity,
                Quality: effectiveQuality,
                PaperWaveEnabled: settings.PaperWaveEnabled,
                TopFoldX: initialMetrics.TopFoldX,
                RightFoldY: initialMetrics.RightFoldY,
                FoldAngleDegrees: initialMetrics.FoldAngleDegrees);

            SetState(TransitionState.Dragging);
            GestureUpdated?.Invoke(_currentContext);
            LogAudit("GestureStarted", $"Corner drag started from ({startX:F0},{startY:F0}). Next candidate: {_peekedCandidate.DisplayName}.");
            return true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Updates pointer position and recalculates progress and wave parameters.
    /// </summary>
    public void UpdateGesture(double currentX, double currentY, double velocity = 0)
    {
        if (State != TransitionState.Dragging || _currentContext == null)
        {
            return;
        }

        if (_dragTracker != null)
        {
            var metrics = _dragTracker.TrackPosition(currentX, currentY, velocity);
            _currentContext = _currentContext with
            {
                CurrentX = currentX,
                CurrentY = currentY,
                Progress = metrics.Progress,
                Velocity = metrics.SmoothedVelocity,
                TopFoldX = metrics.TopFoldX,
                RightFoldY = metrics.RightFoldY,
                FoldAngleDegrees = metrics.FoldAngleDegrees
            };
        }
        else
        {
            var dx = _currentContext.StartX - currentX;
            var dy = currentY - _currentContext.StartY;
            var distance = Math.Max(0.0, (dx * 0.7071) + (dy * 0.7071));
            var threshold = _currentContext.Sensitivity.GetDragDistanceThreshold();
            var progress = Math.Clamp(distance / threshold, 0.0, 1.0);

            _currentContext = _currentContext with
            {
                CurrentX = currentX,
                CurrentY = currentY,
                Progress = progress,
                Velocity = velocity
            };
        }

        GestureUpdated?.Invoke(_currentContext);
    }

    /// <summary>
    /// Cancels the active gesture, animating the paper curl back to rest.
    /// </summary>
    public async Task CancelAsync(string reason = "UserCancelled", CancellationToken cancellationToken = default)
    {
        if (State != TransitionState.Dragging && State != TransitionState.HoverPreview)
        {
            return;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(TransitionState.Cancelling);
            LogAudit("GestureCancelled", $"Corner gesture cancelled. Reason: {reason}.");

            _currentContext = null;
            _dragTracker = null;
            _peekedCandidate = null;

            TransitionCancelled?.Invoke();
            SetState(TransitionState.Idle);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Commits the active gesture, advancing to the peeked next wallpaper.
    /// </summary>
    public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (State != TransitionState.Dragging || _currentContext == null || !_currentContext.CanCommit)
        {
            await CancelAsync("InsufficientProgress", cancellationToken).ConfigureAwait(false);
            return false;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(TransitionState.Completing);
            var targetCandidate = _peekedCandidate;
            var rotation = _getRotationService();

            WallpaperRotationResult? result = null;
            if (rotation != null)
            {
                result = await rotation.RotateNextAsync(trigger: "InteractiveCorner", cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var success = result?.Success == true;
            LogAudit("GestureCommitted", $"Corner gesture committed. Advanced to: {targetCandidate?.DisplayName ?? "Next"}. Success: {success}.");

            _currentContext = null;
            _dragTracker = null;
            _peekedCandidate = null;

            TransitionCommitted?.Invoke(targetCandidate);
            SetState(TransitionState.Idle);
            return success;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void SetState(TransitionState newState)
    {
        if (State == newState) return;
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void LogAudit(string eventType, string details)
    {
        _auditLogger?.Invoke(eventType, details);
        Trace.WriteLine($"[WallpaperTransitionCoordinator] {eventType}: {details}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _syncLock.Dispose();
    }
}
