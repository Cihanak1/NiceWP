using System.Diagnostics;
using System.Runtime.InteropServices;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Infrastructure.Transition;

public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom, int Width, int Height);

/// <summary>
/// A global, low-level Win32 mouse input monitor (WH_MOUSE_LL) that observes
/// desktop cursor movements and button presses for top-right hot corner page turn gestures.
/// Never swallows or consumes mouse events (always calls CallNextHookEx), ensuring 100%
/// compatibility with Windows Explorer desktop icons, selection, and clicks.
/// </summary>
public sealed class DesktopGestureInputMonitor : IDisposable
{
    private const int WhMouseLl = 14;
    private const int VkEscape = 0x1B;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;

    private readonly WallpaperTransitionCoordinator _coordinator;
    private readonly Func<WallpaperRotationService?> _getRotation;
    private readonly Action<string, string>? _externalAuditLogger;
    private readonly NativeMethods.LowLevelMouseProc _hookProcDelegate;
    private readonly List<string> _eventLog = [];
    private readonly object _logLock = new();

    private nint _hookHandle;
    private bool _isInstalled;
    private bool _isEnabled = true;
    private bool _isDragging;
    private bool _isHovering;
    private double _startX;
    private double _startY;
    private int _hotCornerSize = 32;
    private bool _isDisposed;

    private int _dispatchInFlight;
    private long _latestPackedPointer;
    private long _latestTimestampTicks;
    private int _totalPointerEvents;
    private int _droppedPointerEvents;

    public GesturePerformanceTelemetry Telemetry { get; } = new();

    public bool IsInstalled => _isInstalled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!_isEnabled && (_isDragging || _isHovering))
            {
                CancelActiveGesture("MonitorDisabled");
            }
        }
    }

    public bool IsDragging => _isDragging;
    public bool IsHovering => _isHovering;
    public int HotCornerSize
    {
        get => _hotCornerSize;
        set => _hotCornerSize = Math.Clamp(value, 16, 128);
    }

    public IReadOnlyList<string> EventLog
    {
        get
        {
            lock (_logLock)
            {
                return _eventLog.ToList();
            }
        }
    }

    public event Action<string, string>? AuditEventLogged;
    public event Action<bool>? HoverChanged;
    public event Action<double, double>? DragStarted;
    public event Action<double, double>? DragMoved;
    public event Action<double, double, bool>? DragReleased;
    public event Action<string>? DragCancelled;

    private MonitorBounds _cachedBounds;
    private long _lastBoundsRefreshTicks;

    public DesktopGestureInputMonitor(
        WallpaperTransitionCoordinator coordinator,
        Func<WallpaperRotationService?> getRotation,
        Action<string, string>? auditLogger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _getRotation = getRotation ?? throw new ArgumentNullException(nameof(getRotation));
        _externalAuditLogger = auditLogger;
        _cachedBounds = GetPrimaryMonitorBounds();
        _lastBoundsRefreshTicks = Stopwatch.GetTimestamp();

        _hookProcDelegate = LowLevelMouseHookProc;
    }

    /// <summary>
    /// Installs the global low-level mouse hook on the calling thread's message loop.
    /// </summary>
    public bool InstallHook()
    {
        if (_isDisposed || _isInstalled)
        {
            return _isInstalled;
        }

        var hMod = NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        if (hMod == 0) hMod = NativeMethods.GetModuleHandle(null);

        _hookHandle = NativeMethods.SetWindowsHookEx(WhMouseLl, _hookProcDelegate, hMod, 0);
        if (_hookHandle == 0)
        {
            _hookHandle = NativeMethods.SetWindowsHookEx(WhMouseLl, _hookProcDelegate, 0, 0);
        }

        if (_hookHandle != 0)
        {
            _isInstalled = true;
            LogAudit("MouseHookInstalled", $"Global WH_MOUSE_LL hook installed successfully. Handle: 0x{_hookHandle:X8}");
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        LogAudit("MouseHookInstallFailed", $"Failed to install WH_MOUSE_LL hook. Win32 error: {error}");
        return false;
    }

    /// <summary>
    /// Uninstalls the low-level mouse hook.
    /// </summary>
    public void UninstallHook()
    {
        if (!_isInstalled) return;

        if (_isDragging || _isHovering)
        {
            CancelActiveGesture("HookUninstalled");
        }

        if (_hookHandle != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }

        _isInstalled = false;
        LogAudit("MouseHookUninstalled", "Global WH_MOUSE_LL hook uninstalled cleanly.");
    }

    /// <summary>
    /// Low-level hook callback executed on mouse events before Windows dispatches them.
    /// Never swallows events: always delegates to CallNextHookEx.
    /// </summary>
    private nint LowLevelMouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _isEnabled && !_isDisposed && lParam != 0)
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                ProcessMouseEvent((uint)wParam, hookStruct.pt.X, hookStruct.pt.Y);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DesktopGestureInputMonitor] Exception in hook callback: {ex}");
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private MonitorBounds GetCurrentMonitorBounds()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastBoundsRefreshTicks > Stopwatch.Frequency * 2)
        {
            _cachedBounds = GetPrimaryMonitorBounds();
            _lastBoundsRefreshTicks = now;
        }
        return _cachedBounds;
    }

    /// <summary>
    /// Processes a single mouse event in screen-space coordinates.
    /// </summary>
    public void ProcessMouseEvent(uint msg, int x, int y)
    {
        if (!_isEnabled || _isDisposed) return;

        var bounds = GetCurrentMonitorBounds();
        var inHotCorner = IsInTopRightCorner(x, y, bounds);

        switch (msg)
        {
            case WmMouseMove:
                HandleMouseMove(x, y, bounds, inHotCorner);
                break;

            case WmLButtonDown:
                HandleLButtonDown(x, y, bounds, inHotCorner);
                break;

            case WmLButtonUp:
                HandleLButtonUp(x, y);
                break;

            case WmRButtonDown:
                if (_isDragging)
                {
                    CancelActiveGesture("RightClickCancelled");
                }
                break;
        }
    }

    private void HandleMouseMove(int x, int y, MonitorBounds bounds, bool inHotCorner)
    {
        if (_isDragging)
        {
            if ((NativeMethods.GetAsyncKeyState(VkEscape) & 0x8000) != 0)
            {
                CancelActiveGesture("EscapePressed");
                return;
            }

            var packed = ((long)x << 32) | (uint)y;
            var prev = Interlocked.Exchange(ref _latestPackedPointer, packed);
            if (prev != 0)
            {
                Interlocked.Increment(ref _droppedPointerEvents);
                Telemetry.DroppedPointerEvents = _droppedPointerEvents;
            }
            Interlocked.Exchange(ref _latestTimestampTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _totalPointerEvents);

            if (Interlocked.CompareExchange(ref _dispatchInFlight, 1, 0) == 0)
            {
                DispatchCoalescedMoves();
            }
            return;
        }

        // Hover handling
        if (inHotCorner)
        {
            if (!_isHovering)
            {
                if (_coordinator.CanInitiateTransition())
                {
                    _isHovering = true;
                    LogAudit("HotCornerEntered", $"Pointer entered hot corner at ({x},{y})");
                    _coordinator.SetHoverPreview(true);
                    HoverChanged?.Invoke(true);
                }
            }
        }
        else
        {
            if (_isHovering)
            {
                _isHovering = false;
                LogAudit("HotCornerExited", $"Pointer exited hot corner at ({x},{y})");
                _coordinator.SetHoverPreview(false);
                HoverChanged?.Invoke(false);
            }
        }
    }

    private void HandleLButtonDown(int x, int y, MonitorBounds bounds, bool inHotCorner)
    {
        if (!inHotCorner) return;

        LogAudit("LButtonDownDetected", $"LButtonDown at ({x},{y}) inside hot corner bounds ({bounds.Right - _hotCornerSize}..{bounds.Right}, {bounds.Top}..{bounds.Top + _hotCornerSize})");

        if (!_coordinator.CanInitiateTransition())
        {
            LogAudit("GestureRejected", "Gesture rejected: preconditions not satisfied (rotation disabled, single wallpaper, or adaptive blocked).");
            return;
        }

        _isDragging = true;
        _isHovering = false;
        _startX = x;
        _startY = y;

        Telemetry.Reset();
        _totalPointerEvents = 0;
        _droppedPointerEvents = 0;
        Interlocked.Exchange(ref _latestPackedPointer, 0);
        Interlocked.Exchange(ref _dispatchInFlight, 0);

        LogAudit("GestureAccepted", $"Gesture accepted starting at ({x},{y})");
        DragStarted?.Invoke(x, y);

        try
        {
            var initiated = _coordinator.BeginCornerGesture(_startX, _startY, bounds.Width, bounds.Height);
            if (initiated && _coordinator.PeekedCandidate != null)
            {
                LogAudit("CandidateResolved", $"Peeked next candidate: {_coordinator.PeekedCandidate.DisplayName}");
                LogAudit("TransitionStarted", $"Interactive transition started from ({_startX},{_startY})");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DesktopGestureInputMonitor] Error starting corner gesture: {ex}");
        }
    }

    private void HandleLButtonUp(int x, int y)
    {
        if (!_isDragging) return;

        _isDragging = false;
        _isHovering = false;

        Telemetry.TotalPointerEvents = _totalPointerEvents;
        Telemetry.DroppedPointerEvents = _droppedPointerEvents;

        var progress = _coordinator.CurrentContext?.Progress ?? 0.0;
        var shouldCommit = progress >= 0.30;

        LogAudit("LButtonReleased", $"LButtonUp at ({x},{y}) with Progress: {progress:P1}. Decision: {(shouldCommit ? "Commit" : "Cancel")}");
        DragReleased?.Invoke(x, y, shouldCommit);

        if (shouldCommit)
        {
            LogAudit("CommitTriggered", $"Committing transition with progress {progress:P1}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _coordinator.CommitAsync(animate: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DesktopGestureInputMonitor] Error committing gesture: {ex}");
                }
            });
        }
        else
        {
            LogAudit("CancelTriggered", $"Cancelling transition below threshold with progress {progress:P1}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _coordinator.CancelAsync("ReleaseBelowThreshold", animate: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DesktopGestureInputMonitor] Error cancelling gesture: {ex}");
                }
            });
        }
    }

    private void DispatchCoalescedMoves()
    {
        while (_isDragging)
        {
            var packed = Interlocked.Exchange(ref _latestPackedPointer, 0);
            if (packed == 0)
            {
                break;
            }

            var curX = (int)(packed >> 32);
            var curY = (int)(packed & 0xFFFFFFFF);
            var inputTicks = Interlocked.Read(ref _latestTimestampTicks);

            var sw = Stopwatch.StartNew();
            _coordinator.UpdateGesture(curX, curY);
            DragMoved?.Invoke(curX, curY);
            sw.Stop();

            var inputLatencyMs = (Stopwatch.GetTimestamp() - inputTicks) * 1000.0 / Stopwatch.Frequency;
            Telemetry.RecordFrame(inputLatencyMs, sw.Elapsed.TotalMilliseconds, 0);

            var progress = _coordinator.CurrentContext?.Progress ?? 0.0;
            LogAudit("ProgressChanged", $"Drag moved to ({curX},{curY}), Progress: {progress:P1}");
        }

        Interlocked.Exchange(ref _dispatchInFlight, 0);
        if (_isDragging && Interlocked.Read(ref _latestPackedPointer) != 0 &&
            Interlocked.CompareExchange(ref _dispatchInFlight, 1, 0) == 0)
        {
            var packed = Interlocked.Exchange(ref _latestPackedPointer, 0);
            if (packed != 0)
            {
                var curX = (int)(packed >> 32);
                var curY = (int)(packed & 0xFFFFFFFF);
                _coordinator.UpdateGesture(curX, curY);
                DragMoved?.Invoke(curX, curY);
            }
            Interlocked.Exchange(ref _dispatchInFlight, 0);
        }
    }

    public void CancelActiveGesture(string reason)
    {
        if (!_isDragging && !_isHovering) return;

        _isDragging = false;
        _isHovering = false;

        LogAudit("GestureCancelled", $"Gesture cancelled. Reason: {reason}");
        DragCancelled?.Invoke(reason);

        _ = Task.Run(async () =>
        {
            try
            {
                await _coordinator.CancelAsync(reason, animate: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DesktopGestureInputMonitor] Error cancelling gesture: {ex}");
            }
        });
    }

    public bool IsInTopRightCorner(int x, int y, MonitorBounds bounds)
    {
        var cornerLeft = bounds.Right - _hotCornerSize;
        var cornerBottom = bounds.Top + _hotCornerSize;

        return x >= cornerLeft && x <= bounds.Right && y >= bounds.Top && y <= cornerBottom;
    }

    public static MonitorBounds GetPrimaryMonitorBounds()
    {
        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        var hMonitor = NativeMethods.MonitorFromPoint(point, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        if (hMonitor != 0)
        {
            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                return new MonitorBounds(
                    mi.rcMonitor.Left,
                    mi.rcMonitor.Top,
                    mi.rcMonitor.Right,
                    mi.rcMonitor.Bottom,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top);
            }
        }

        var w = NativeMethods.GetSystemMetrics(0); // SM_CXSCREEN
        var h = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
        var width = w > 0 ? w : 1920;
        var height = h > 0 ? h : 1080;
        return new MonitorBounds(0, 0, width, height, width, height);
    }

    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "NiceWP-gesture-audit.log");

    private void LogAudit(string eventType, string detail)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{eventType}] {detail}";
        lock (_logLock)
        {
            _eventLog.Add(entry);
            if (_eventLog.Count > 200)
            {
                _eventLog.RemoveAt(0);
            }
        }

        // Suppress per-frame or high-frequency event spam from disk and trace
        if (string.Equals(eventType, "ProgressChanged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "OverlayRendered", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                File.AppendAllText(LogFilePath, entry + Environment.NewLine);
            }
            catch
            {
                // Ignore concurrent disk access errors
            }
        });

        Trace.WriteLine($"[DesktopGestureInputMonitor] {entry}");
        _externalAuditLogger?.Invoke(eventType, detail);
        AuditEventLogged?.Invoke(eventType, detail);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        UninstallHook();
    }

    private static class NativeMethods
    {
        public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public nuint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandle(string? lpModuleName);
    }
}
