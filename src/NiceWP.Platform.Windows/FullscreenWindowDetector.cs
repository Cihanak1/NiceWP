using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NiceWP.Core;
using static NiceWP.Platform.Windows.DesktopNativeMethods;

namespace NiceWP.Platform.Windows;

/// <summary>
/// Represents screen coordinates for a window or display monitor.
/// </summary>
public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// Captures the relevant state of a candidate foreground window for fullscreen evaluation.
/// </summary>
public readonly record struct ForegroundWindowSnapshot(
    nint Handle,
    bool IsValidWindow,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    string ClassName,
    uint ProcessId,
    uint CurrentProcessId,
    string? ProcessName,
    bool HasWindowBounds,
    ScreenBounds WindowBounds,
    bool HasMonitorBounds,
    ScreenBounds MonitorBounds);

/// <summary>
/// Detects whether the active foreground window is a fullscreen application covering its monitor.
/// </summary>
public sealed class FullscreenWindowDetector : IFullscreenWindowDetector
{
    public const int DefaultBoundsTolerancePixels = 2;
    private const int MaxClassNameLength = 256;

    private static readonly string[] ExcludedShellClasses =
    [
        ProgmanClass,
        WorkerClass,
        DesktopViewClass,
        ShellTrayClass,
        SecondaryShellTrayClass
    ];

    private static readonly string[] ExcludedProcessNames =
    [
        "NiceWP.App",
        "NiceWP.App.exe",
        "NiceWP.Runtime.Host",
        "NiceWP.Runtime.Host.exe"
    ];

    private readonly Func<ForegroundWindowSnapshot> snapshotProvider;
    private readonly int boundsTolerancePixels;

    public FullscreenWindowDetector(int boundsTolerancePixels = DefaultBoundsTolerancePixels)
    {
        this.boundsTolerancePixels = Math.Max(0, boundsTolerancePixels);
        snapshotProvider = () => CaptureForegroundSnapshot(this.boundsTolerancePixels);
    }

    public FullscreenWindowDetector(
        Func<ForegroundWindowSnapshot> snapshotProvider,
        int boundsTolerancePixels = DefaultBoundsTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        this.snapshotProvider = snapshotProvider;
        this.boundsTolerancePixels = Math.Max(0, boundsTolerancePixels);
    }

    public bool IsFullscreenWindowActive()
    {
        try
        {
            var snapshot = snapshotProvider();
            return Evaluate(snapshot, boundsTolerancePixels);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error evaluating fullscreen foreground window: {ex}");
            return false;
        }
    }

    public static bool Evaluate(
        ForegroundWindowSnapshot snapshot,
        int tolerancePixels = DefaultBoundsTolerancePixels)
    {
        if (snapshot.Handle == 0 ||
            !snapshot.IsValidWindow ||
            !snapshot.IsVisible ||
            snapshot.IsMinimized ||
            snapshot.IsCloaked)
        {
            return false;
        }

        if (IsExcludedClassName(snapshot.ClassName))
        {
            return false;
        }

        if (snapshot.ProcessId != 0 &&
            snapshot.CurrentProcessId != 0 &&
            snapshot.ProcessId == snapshot.CurrentProcessId)
        {
            return false;
        }

        if (IsExcludedProcessName(snapshot.ProcessName))
        {
            return false;
        }

        if (!snapshot.HasWindowBounds || !snapshot.HasMonitorBounds)
        {
            return false;
        }

        return CoversMonitor(snapshot.WindowBounds, snapshot.MonitorBounds, tolerancePixels);
    }

    public static bool CoversMonitor(
        ScreenBounds window,
        ScreenBounds monitor,
        int tolerancePixels = DefaultBoundsTolerancePixels)
    {
        if (!window.IsValid || !monitor.IsValid)
        {
            return false;
        }

        var tolerance = Math.Max(0, tolerancePixels);
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }

    public static bool IsExcludedClassName(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        foreach (var excluded in ExcludedShellClasses)
        {
            if (string.Equals(className, excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsExcludedProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var excluded in ExcludedProcessNames)
        {
            if (string.Equals(processName, excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ForegroundWindowSnapshot CaptureForegroundSnapshot(int tolerancePixels)
    {
        var handle = GetForegroundWindow();
        if (handle == 0 || !IsWindow(handle))
        {
            return default;
        }

        var isVisible = IsWindowVisible(handle);
        var isMinimized = IsIconic(handle);
        var isCloaked = IsWindowCloaked(handle);
        var className = GetWindowClassName(handle);
        var hasWindowBounds = TryGetWindowBounds(handle, out var windowBounds);
        var hasMonitorBounds = TryGetMonitorBounds(handle, out var monitorBounds);

        GetWindowThreadProcessId(handle, out var processId);
        var currentProcessId = (uint)Environment.ProcessId;

        string? processName = null;
        if (isVisible &&
            !isMinimized &&
            !isCloaked &&
            !IsExcludedClassName(className) &&
            processId != 0 &&
            processId != currentProcessId &&
            hasWindowBounds &&
            hasMonitorBounds &&
            CoversMonitor(windowBounds, monitorBounds, tolerancePixels))
        {
            processName = TryGetProcessName(processId);
        }

        return new ForegroundWindowSnapshot(
            Handle: handle,
            IsValidWindow: true,
            IsVisible: isVisible,
            IsMinimized: isMinimized,
            IsCloaked: isCloaked,
            ClassName: className,
            ProcessId: processId,
            CurrentProcessId: currentProcessId,
            ProcessName: processName,
            HasWindowBounds: hasWindowBounds,
            WindowBounds: windowBounds,
            HasMonitorBounds: hasMonitorBounds,
            MonitorBounds: monitorBounds);
    }

    private static bool IsWindowCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, Marshal.SizeOf<int>()) == 0 &&
        cloaked != 0;

    private static string GetWindowClassName(nint handle)
    {
        var builder = new StringBuilder(MaxClassNameLength);
        var length = GetClassName(handle, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }

    private static bool TryGetWindowBounds(nint handle, out ScreenBounds bounds)
    {
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out WindowRect extendedRect, Marshal.SizeOf<WindowRect>()) == 0)
        {
            var dwmBounds = new ScreenBounds(extendedRect.Left, extendedRect.Top, extendedRect.Right, extendedRect.Bottom);
            if (dwmBounds.IsValid)
            {
                bounds = dwmBounds;
                return true;
            }
        }

        if (GetWindowRect(handle, out var windowRect))
        {
            var rawBounds = new ScreenBounds(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
            if (rawBounds.IsValid)
            {
                bounds = rawBounds;
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static bool TryGetMonitorBounds(nint handle, out ScreenBounds bounds)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            bounds = default;
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            bounds = default;
            return false;
        }

        var monitorBounds = new ScreenBounds(
            info.MonitorBounds.Left,
            info.MonitorBounds.Top,
            info.MonitorBounds.Right,
            info.MonitorBounds.Bottom);
        if (!monitorBounds.IsValid)
        {
            bounds = default;
            return false;
        }

        bounds = monitorBounds;
        return true;
    }

    private static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
