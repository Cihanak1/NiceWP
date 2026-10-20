using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NiceWP.App.Tray;

/// <summary>
/// Encapsulates the Windows Shell NotifyIcon lifecycle, context menu, and window message routing.
/// Keeps Win32 interop completely separated from the WinUI views.
/// </summary>
public sealed class AppTrayIcon : IDisposable
{
    private const nuint SubclassId = 101;

    private readonly IntPtr _hWnd;
    private readonly TrayInterop.SubclassProc _subclassDelegate;
    private readonly int _taskbarCreatedMsg;

    private TrayInterop.NOTIFYICONDATA _nid;
    private bool _isInitialized;
    private bool _isDisposed;
    private IntPtr _ownedIcon;

    public event Action? OpenRequested;
    public event Action? StopWallpaperRequested;
    public event Action? ExitRequested;

    public AppTrayIcon(IntPtr hWnd)
    {
        _hWnd = hWnd;
        _subclassDelegate = SubclassProcCallback;
        _taskbarCreatedMsg = TrayInterop.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>
    /// Initializes and displays the system tray icon, attaching the window subclass.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized || _isDisposed) return;

        _ownedIcon = LoadBrandIcon();
        if (_ownedIcon == IntPtr.Zero)
        {
            Trace.WriteLine("NiceWP tray icon could not be loaded from assets or executable.");
            return;
        }

        _nid = new TrayInterop.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = TrayInterop.NIF_MESSAGE | TrayInterop.NIF_ICON | TrayInterop.NIF_TIP,
            uCallbackMessage = TrayInterop.WM_TRAYICON,
            hIcon = _ownedIcon,
            szTip = "NiceWP"
        };

        var added = TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_ADD, ref _nid);
        if (!added)
        {
            Trace.WriteLine("Failed to add Shell_NotifyIcon to system tray.");
        }

        if (!TrayInterop.SetWindowSubclass(_hWnd, _subclassDelegate, SubclassId, 0))
        {
            TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_DELETE, ref _nid);
            TrayInterop.DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
            Trace.WriteLine("NiceWP tray window subclass could not be installed.");
            return;
        }
        _isInitialized = true;
    }

    private IntPtr LoadBrandIcon()
    {
        var dpi = TrayInterop.GetDpiForWindow(_hWnd);
        if (dpi == 0) dpi = 96;
        var icon = TrayInterop.LoadImage(IntPtr.Zero,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "nicewp-tray.ico"),
            TrayInterop.IMAGE_ICON,
            TrayInterop.GetSystemMetricsForDpi(TrayInterop.SM_CXSMICON, dpi),
            TrayInterop.GetSystemMetricsForDpi(TrayInterop.SM_CYSMICON, dpi),
            TrayInterop.LR_LOADFROMFILE);
        if (icon != IntPtr.Zero) return icon;

        // An incomplete asset deployment still uses the embedded NiceWP mark.
        TrayInterop.ExtractIconEx(Path.Combine(AppContext.BaseDirectory, "NiceWP.App.exe"),
            0, out var largeIcon, out var smallIcon, 1);
        if (smallIcon == IntPtr.Zero) return largeIcon;
        if (largeIcon != IntPtr.Zero) TrayInterop.DestroyIcon(largeIcon);
        return smallIcon;
    }

    private void RefreshBrandIcon(int operation)
    {
        var replacement = LoadBrandIcon();
        var previous = _ownedIcon;
        if (replacement != IntPtr.Zero)
        {
            _ownedIcon = replacement;
            _nid.hIcon = replacement;
        }
        TrayInterop.Shell_NotifyIcon(operation, ref _nid);
        // Shell copies the image. Keep the current HICON for Explorer recreation,
        // releasing only the superseded handle (never use LR_SHARED).
        if (replacement != IntPtr.Zero && previous != IntPtr.Zero)
            TrayInterop.DestroyIcon(previous);
    }

    private IntPtr SubclassProcCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == TrayInterop.WM_TRAYICON)
        {
            var eventMsg = (int)lParam;
            if (eventMsg == TrayInterop.WM_RBUTTONUP || eventMsg == TrayInterop.WM_CONTEXTMENU)
            {
                ShowContextMenu(hWnd);
                return IntPtr.Zero;
            }

            if (eventMsg == TrayInterop.WM_LBUTTONDBLCLK)
            {
                OpenRequested?.Invoke();
                return IntPtr.Zero;
            }
        }
        else if (uMsg == (uint)TrayInterop.WmActivateApp)
        {
            OpenRequested?.Invoke();
            return IntPtr.Zero;
        }
        else if (uMsg == (uint)_taskbarCreatedMsg && _isInitialized && !_isDisposed)
        {
            RefreshBrandIcon(TrayInterop.NIM_ADD);
            return IntPtr.Zero;
        }
        else if (_isInitialized && !_isDisposed &&
            (uMsg == TrayInterop.WM_DPICHANGED || uMsg == TrayInterop.WM_SETTINGCHANGE || uMsg == TrayInterop.WM_DISPLAYCHANGE))
        {
            RefreshBrandIcon(TrayInterop.NIM_MODIFY);
        }

        return TrayInterop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        var hMenu = TrayInterop.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdOpen, "Open NiceWP");
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdStop, "Stop Wallpaper");
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdExit, "Exit NiceWP");

            TrayInterop.GetCursorPos(out var pt);
            TrayInterop.SetForegroundWindow(hWnd);

            var cmd = TrayInterop.TrackPopupMenuEx(
                hMenu,
                TrayInterop.TPM_RETURNCMD | TrayInterop.TPM_RIGHTBUTTON,
                pt.X,
                pt.Y,
                hWnd,
                IntPtr.Zero);

            TrayInterop.PostMessage(hWnd, TrayInterop.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case TrayInterop.CmdOpen:
                    OpenRequested?.Invoke();
                    break;
                case TrayInterop.CmdStop:
                    StopWallpaperRequested?.Invoke();
                    break;
                case TrayInterop.CmdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            TrayInterop.DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_isInitialized)
        {
            TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_DELETE, ref _nid);
            TrayInterop.RemoveWindowSubclass(_hWnd, _subclassDelegate, SubclassId);
            _isInitialized = false;
        }
        if (_ownedIcon != IntPtr.Zero)
        {
            TrayInterop.DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
            _nid.hIcon = IntPtr.Zero;
        }
    }
}
