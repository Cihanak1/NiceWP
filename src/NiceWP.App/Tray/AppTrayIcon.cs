using System.Diagnostics;
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

        var hIcon = TrayInterop.LoadIcon(IntPtr.Zero, (IntPtr)TrayInterop.IDI_APPLICATION);

        _nid = new TrayInterop.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = TrayInterop.NIF_MESSAGE | TrayInterop.NIF_ICON | TrayInterop.NIF_TIP,
            uCallbackMessage = TrayInterop.WM_TRAYICON,
            hIcon = hIcon,
            szTip = "NiceWP"
        };

        var added = TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_ADD, ref _nid);
        if (!added)
        {
            Trace.WriteLine("Failed to add Shell_NotifyIcon to system tray.");
        }

        TrayInterop.SetWindowSubclass(_hWnd, _subclassDelegate, SubclassId, 0);
        _isInitialized = true;
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
            TrayInterop.Shell_NotifyIcon(TrayInterop.NIM_ADD, ref _nid);
            return IntPtr.Zero;
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
    }
}
