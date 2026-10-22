using System.Diagnostics;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Manages pointer capture lifecycle, gesture initiation, release commit,
/// capture loss recovery, and escape key cancellation for corner page-turn transitions.
/// </summary>
public sealed class CornerPointerCaptureManager : IDisposable
{
    private const int VkEscape = 0x1B;

    private readonly WallpaperTransitionCoordinator _coordinator;
    private readonly HotCornerDetector _detector;
    private readonly Action<string, string>? _auditLogger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private uint? _capturedPointerId;
    private double _lastX;
    private double _lastY;
    private double _lastTimestampMs;
    private bool _isDisposed;

    public bool HasCapture => _capturedPointerId.HasValue;

    public uint? CapturedPointerId => _capturedPointerId;

    public event Action? CaptureAcquired;
    public event Action<string>? CaptureReleased;

    public CornerPointerCaptureManager(
        WallpaperTransitionCoordinator coordinator,
        HotCornerDetector detector,
        Action<string, string>? auditLogger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Attempts to initiate pointer capture when mouse/pointer presses down inside the hot corner.
    /// </summary>
    public async Task<bool> TryAcquireCaptureAsync(
        uint pointerId,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || HasCapture)
        {
            return false;
        }

        // Validate hit test
        if (!_detector.IsEnabled || !HotCornerHitTester.IsPointInTopRightCorner(x, y, _detector.ScreenWidth, _detector.ScreenHeight, _detector.CornerSize))
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasCapture) return false;

            var initiated = await _coordinator.BeginCornerGestureAsync(
                startX: x,
                startY: y,
                screenWidth: _detector.ScreenWidth,
                screenHeight: _detector.ScreenHeight,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!initiated)
            {
                return false;
            }

            _capturedPointerId = pointerId;
            _lastX = x;
            _lastY = y;
            _lastTimestampMs = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

            LogAudit("CaptureAcquired", $"Pointer capture acquired for PointerId {pointerId} at ({x:F0},{y:F0}).");
            CaptureAcquired?.Invoke();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Processes pointer movement while capture is held.
    /// </summary>
    public void ProcessPointerMoved(uint pointerId, double currentX, double currentY)
    {
        if (_isDisposed || !HasCapture || _capturedPointerId != pointerId)
        {
            return;
        }

        var nowMs = Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
        var dt = Math.Max(1.0, nowMs - _lastTimestampMs);
        var dx = currentX - _lastX;
        var dy = currentY - _lastY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var velocity = (distance / dt) * 1000.0; // px/sec

        _lastX = currentX;
        _lastY = currentY;
        _lastTimestampMs = nowMs;

        _coordinator.UpdateGesture(currentX, currentY, velocity);
    }

    /// <summary>
    /// Processes pointer release, committing the transition if threshold is met, or cancelling otherwise.
    /// </summary>
    public async Task<bool> ProcessPointerReleasedAsync(
        uint pointerId,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !HasCapture || _capturedPointerId != pointerId)
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasCapture || _capturedPointerId != pointerId) return false;

            _capturedPointerId = null;

            var context = _coordinator.CurrentContext;
            bool success;
            if (context != null && context.CanCommit)
            {
                LogAudit("CaptureReleasedCommit", "Pointer released with sufficient progress: committing transition.");
                success = await _coordinator.CommitAsync(cancellationToken).ConfigureAwait(false);
                CaptureReleased?.Invoke("Commit");
            }
            else
            {
                LogAudit("CaptureReleasedCancel", "Pointer released with insufficient progress: cancelling transition.");
                await _coordinator.CancelAsync("ReleaseUnderThreshold", cancellationToken).ConfigureAwait(false);
                CaptureReleased?.Invoke("CancelUnderThreshold");
                success = false;
            }

            _detector.Reset();
            return success;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Handles sudden pointer capture loss (e.g. system modal, task switch, Alt+Tab).
    /// </summary>
    public async Task HandleCaptureLostAsync(
        uint? pointerId = null,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !HasCapture) return;
        if (pointerId.HasValue && _capturedPointerId != pointerId.Value) return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasCapture) return;

            _capturedPointerId = null;
            LogAudit("CaptureLost", "Pointer capture lost by OS or window manager: safely cancelling transition.");
            await _coordinator.CancelAsync("CaptureLost", cancellationToken).ConfigureAwait(false);
            CaptureReleased?.Invoke("CaptureLost");
            _detector.Reset();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Handles key press events. ESC key immediately cancels the gesture.
    /// </summary>
    public async Task<bool> HandleKeyDownAsync(
        int virtualKeyCode,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !HasCapture) return false;

        if (virtualKeyCode == VkEscape)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!HasCapture) return false;

                _capturedPointerId = null;
                LogAudit("EscapeCancelled", "Escape pressed during gesture: cancelling transition.");
                await _coordinator.CancelAsync("EscapePressed", cancellationToken).ConfigureAwait(false);
                CaptureReleased?.Invoke("EscapePressed");
                _detector.Reset();
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        return false;
    }

    /// <summary>
    /// Safely cancels when the host window is deactivated or invalid.
    /// </summary>
    public async Task HandleDeactivationOrInvalidationAsync(
        string reason = "WindowDeactivated",
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !HasCapture) return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasCapture) return;

            _capturedPointerId = null;
            LogAudit("DeactivationCancelled", $"Transition cancelled due to: {reason}.");
            await _coordinator.CancelAsync(reason, cancellationToken).ConfigureAwait(false);
            CaptureReleased?.Invoke(reason);
            _detector.Reset();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LogAudit(string eventType, string details)
    {
        _auditLogger?.Invoke(eventType, details);
        Trace.WriteLine($"[CornerPointerCaptureManager] {eventType}: {details}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lock.Dispose();
    }
}
