using System.Runtime.InteropServices;
using System.Text;

namespace NiceWP.Platform.Windows;

internal static class DesktopNativeMethods
{
    internal const string ProgmanClass = "Progman";
    internal const string WorkerClass = "WorkerW";
    internal const string DesktopViewClass = "SHELLDLL_DefView";
    internal const string ShellTrayClass = "Shell_TrayWnd";
    internal const string SecondaryShellTrayClass = "Shell_SecondaryTrayWnd";
    internal const uint PrepareWorkerMessage = 0x052C;
    internal const nuint PrepareWorkerWParam = 0x000D;
    internal const nint PrepareWorkerLParam = 0x0001;
    internal const uint SpiGetClientAreaAnimation = 0x1042;
    internal const uint SpiSetClientAreaAnimation = 0x1043;
    internal const uint MessageTimeoutMilliseconds = 1000;
    internal const uint AbortIfHung = 0x0002;
    internal const uint ErrorOnExit = 0x0020;
    internal const int StyleIndex = -16;
    internal const long ChildStyle = 0x40000000;
    internal const long PopupStyle = 0x80000000;
    internal const long WindowFrameStyle = 0x00CF0000;
    internal const uint FrameChanged = 0x0020;
    internal const uint NoActivate = 0x0010;
    internal const uint NoZOrder = 0x0004;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;
    internal const byte AcLineOffline = 0;
    internal const byte AcLineOnline = 1;
    internal const byte BatteryFlagNoSystemBattery = 0x80;
    internal const byte BatteryStatusUnknown = 0xFF;
    internal const byte SystemStatusBatterySaverOn = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal WindowRect MonitorBounds;
        internal WindowRect WorkBounds;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal int BatteryLifeTime;
        internal int BatteryFullLifeTime;
    }

    internal delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nuint wParam,
        nint lParam, uint flags, uint timeout, out nuint result);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint uiParam, ref bool pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint uiParam, nint pvParam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint window, nint parent);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out WindowRect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);
}
