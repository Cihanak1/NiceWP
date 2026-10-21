using System.Diagnostics;
using System.Runtime.InteropServices;
using NiceWP.Core;
using static NiceWP.Platform.Windows.DesktopNativeMethods;

namespace NiceWP.Platform.Windows;

/// <summary>
/// Native Windows implementation of IRuntimeEventNotifier.
/// Uses a lightweight hidden window to receive WM_WTSSESSION_CHANGE and WM_POWERBROADCAST notifications,
/// and SetWinEventHook for EVENT_SYSTEM_FOREGROUND.
/// Fully failure-isolated: any Win32 registration failure is caught, logged, and surfaced via flags
/// so the caller can fall back to polling.
/// </summary>
public sealed class WindowsRuntimeEventNotifier : IRuntimeEventNotifier
{
    private readonly WndProc wndProcDelegate;
    private readonly WinEventProc winEventProcDelegate;
    private readonly string windowClassName;
    private readonly nint hInstance;
    private nint windowHandle;
    private nint winEventHook;
    private nint displayNotificationHandle;
    private nint acDcNotificationHandle;
    private nint powerSavingNotificationHandle;
    private bool sessionNotificationRegistered;
    private bool classRegistered;
    private bool disposed;

    public event EventHandler? ForegroundWindowChanged;
    public event Action<string?>? ForegroundProcessChanged;
    public event Action<bool>? SessionLockChanged;
    public event Action<bool>? DisplayPowerChanged;
    public event EventHandler? PowerStatusChanged;

    public bool IsForegroundHookActive => winEventHook != 0;
    public bool IsSessionNotificationActive => sessionNotificationRegistered;
    public bool IsDisplayNotificationActive => displayNotificationHandle != 0;
    public bool IsPowerNotificationActive => acDcNotificationHandle != 0 || powerSavingNotificationHandle != 0;
    public string? CurrentForegroundProcessName { get; private set; }

    public WindowsRuntimeEventNotifier()
    {
        wndProcDelegate = CustomWndProc;
        winEventProcDelegate = CustomWinEventProc;
        hInstance = GetModuleHandle(null);
        windowClassName = $"NiceWP_Notifier_{Guid.NewGuid():N}";

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            var wndClass = new WindowClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
                hInstance = hInstance,
                lpszClassName = windowClassName,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate)
            };

            var classAtom = RegisterClassEx(ref wndClass);
            if (classAtom != 0)
            {
                classRegistered = true;
                windowHandle = CreateWindowEx(
                    0,
                    windowClassName,
                    "NiceWP Event Notifier Window",
                    0, // Invisible top-level window
                    0, 0, 0, 0,
                    0, 0, hInstance, 0);

                if (windowHandle != 0)
                {
                    try
                    {
                        sessionNotificationRegistered = WTSRegisterSessionNotification(windowHandle, NotifyForThisSession);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"WTSRegisterSessionNotification failed: {ex.Message}");
                    }

                    try
                    {
                        displayNotificationHandle = RegisterPowerSettingNotification(
                            windowHandle,
                            in GuidConsoleDisplayState,
                            DeviceNotifyWindowHandle);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"RegisterPowerSettingNotification (Display) failed: {ex.Message}");
                    }

                    try
                    {
                        acDcNotificationHandle = RegisterPowerSettingNotification(
                            windowHandle,
                            in GuidAcDcPowerSource,
                            DeviceNotifyWindowHandle);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"RegisterPowerSettingNotification (ACDC) failed: {ex.Message}");
                    }

                    try
                    {
                        powerSavingNotificationHandle = RegisterPowerSettingNotification(
                            windowHandle,
                            in GuidPowerSavingStatus,
                            DeviceNotifyWindowHandle);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"RegisterPowerSettingNotification (PowerSaving) failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to initialize native message window: {ex}");
        }

        try
        {
            winEventHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                0,
                winEventProcDelegate,
                0,
                0,
                WinEventOutOfContext);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SetWinEventHook failed: {ex.Message}");
        }
    }

    private nint CustomWndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam)
    {
        try
        {
            if (uMsg == WmWtsSessionChange)
            {
                if (wParam == WtsSessionLock)
                {
                    Trace.WriteLine("Native session lock notification received (Locked).");
                    SessionLockChanged?.Invoke(true);
                }
                else if (wParam == WtsSessionUnlock)
                {
                    Trace.WriteLine("Native session unlock notification received (Unlocked).");
                    SessionLockChanged?.Invoke(false);
                }
            }
            else if (uMsg == WmPowerBroadcast)
            {
                if (wParam == (nuint)PbtPowerSettingChange && lParam != 0)
                {
                    try
                    {
                        var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                        if (setting.PowerSetting == GuidConsoleDisplayState)
                        {
                            var isDisplayOn = setting.Data != 0;
                            Trace.WriteLine($"Native display power setting notification received (DisplayOn={isDisplayOn}, Data={setting.Data}).");
                            DisplayPowerChanged?.Invoke(isDisplayOn);
                        }
                        else if (setting.PowerSetting == GuidAcDcPowerSource || setting.PowerSetting == GuidPowerSavingStatus)
                        {
                            Trace.WriteLine($"Native power setting notification received ({setting.PowerSetting}).");
                            PowerStatusChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Error reading power broadcast setting: {ex.Message}");
                    }
                }
                else if (wParam == (nuint)PbtApmPowerStatusChange)
                {
                    Trace.WriteLine("Native power status change notification received (APM).");
                    PowerStatusChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error handling native message 0x{uMsg:X4}: {ex}");
        }

        return DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    private void CustomWinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (eventType == EventSystemForeground)
            {
                ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);

                if (hwnd != 0)
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid != 0)
                    {
                        var procName = TryGetProcessExeName(pid);
                        if (!string.Equals(procName, CurrentForegroundProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            CurrentForegroundProcessName = procName;
                            ForegroundProcessChanged?.Invoke(procName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error handling foreground win event: {ex}");
        }
    }

    private static string? TryGetProcessExeName(uint processId)
    {
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            if (string.IsNullOrEmpty(name)) return null;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (winEventHook != 0)
        {
            try
            {
                UnhookWinEvent(winEventHook);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unhooking win event: {ex.Message}");
            }
            winEventHook = 0;
        }

        if (displayNotificationHandle != 0)
        {
            try
            {
                UnregisterPowerSettingNotification(displayNotificationHandle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unregistering display notification: {ex.Message}");
            }
            displayNotificationHandle = 0;
        }

        if (acDcNotificationHandle != 0)
        {
            try
            {
                UnregisterPowerSettingNotification(acDcNotificationHandle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unregistering ACDC notification: {ex.Message}");
            }
            acDcNotificationHandle = 0;
        }

        if (powerSavingNotificationHandle != 0)
        {
            try
            {
                UnregisterPowerSettingNotification(powerSavingNotificationHandle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unregistering power saving notification: {ex.Message}");
            }
            powerSavingNotificationHandle = 0;
        }

        if (sessionNotificationRegistered && windowHandle != 0)
        {
            try
            {
                WTSUnRegisterSessionNotification(windowHandle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unregistering session notification: {ex.Message}");
            }
            sessionNotificationRegistered = false;
        }

        if (windowHandle != 0)
        {
            try
            {
                DestroyWindow(windowHandle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error destroying notifier window: {ex.Message}");
            }
            windowHandle = 0;
        }

        if (classRegistered)
        {
            try
            {
                UnregisterClass(windowClassName, hInstance);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error unregistering notifier window class: {ex.Message}");
            }
            classRegistered = false;
        }
    }
}
