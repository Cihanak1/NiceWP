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
    public event Action? ShellRecovered;
    public event Action? DisplayTopologyChanged;

    public NiceWP.Core.AdaptiveDesktopProfile CurrentProfile { get; set; } = NiceWP.Core.AdaptiveDesktopProfile.Automatic;
    public NiceWP.Core.ResourceBudget CurrentBudget { get; set; } = NiceWP.Core.ResourceBudget.Automatic;

    public event Action<NiceWP.Core.AdaptiveDesktopProfile>? ProfileSelected;
    public event Action<NiceWP.Core.ResourceBudget>? BudgetSelected;

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
            ShellRecovered?.Invoke();
            return IntPtr.Zero;
        }
        else if (_isInitialized && !_isDisposed &&
            (uMsg == TrayInterop.WM_DPICHANGED || uMsg == TrayInterop.WM_SETTINGCHANGE || uMsg == TrayInterop.WM_DISPLAYCHANGE))
        {
            RefreshBrandIcon(TrayInterop.NIM_MODIFY);
            DisplayTopologyChanged?.Invoke();
        }

        return TrayInterop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        var hMenu = TrayInterop.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var loc = NiceWP.Infrastructure.Localization.LocalizationService.Instance;
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdOpen, loc.GetString(NiceWP.Infrastructure.Localization.LocalizationKeys.TrayOpen));
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdStop, loc.GetString(NiceWP.Infrastructure.Localization.LocalizationKeys.TrayStopWallpaper));
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_SEPARATOR, IntPtr.Zero, string.Empty);

            // Submenu for Adaptive Profiles
            var hProfileMenu = TrayInterop.CreatePopupMenu();
            if (hProfileMenu != IntPtr.Zero)
            {
                var isTr = loc.CurrentLanguage == NiceWP.Core.EffectiveLanguage.Turkish;
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileAuto, isTr ? "Otomatik" : "Automatic", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Automatic);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileNormal, isTr ? "Normal" : "Normal", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Normal);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileFocus, isTr ? "Odak" : "Focus", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Focus);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileGaming, isTr ? "Oyun" : "Gaming", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Gaming);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfilePresentation, isTr ? "Sunum" : "Presentation", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Presentation);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileBatterySaver, isTr ? "Pil Tasarrufu" : "Battery Saver", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.BatterySaver);
                AppendProfileItem(hProfileMenu, TrayInterop.CmdProfileCinema, isTr ? "Sinema" : "Cinema", CurrentProfile == NiceWP.Core.AdaptiveDesktopProfile.Cinema);

                TrayInterop.AppendMenu(hMenu, TrayInterop.MF_POPUP, hProfileMenu, isTr ? "Masaüstü Profili" : "Desktop Profile");
            }

            // Submenu for Resource Budget
            var hBudgetMenu = TrayInterop.CreatePopupMenu();
            if (hBudgetMenu != IntPtr.Zero)
            {
                var isTr = loc.CurrentLanguage == NiceWP.Core.EffectiveLanguage.Turkish;
                AppendBudgetItem(hBudgetMenu, TrayInterop.CmdBudgetAuto, isTr ? "Otomatik" : "Automatic", CurrentBudget == NiceWP.Core.ResourceBudget.Automatic);
                AppendBudgetItem(hBudgetMenu, TrayInterop.CmdBudgetLow, isTr ? "Düşük (Low)" : "Low", CurrentBudget == NiceWP.Core.ResourceBudget.Low);
                AppendBudgetItem(hBudgetMenu, TrayInterop.CmdBudgetBalanced, isTr ? "Dengeli (Balanced)" : "Balanced", CurrentBudget == NiceWP.Core.ResourceBudget.Balanced);
                AppendBudgetItem(hBudgetMenu, TrayInterop.CmdBudgetHigh, isTr ? "Yüksek (High)" : "High", CurrentBudget == NiceWP.Core.ResourceBudget.High);

                TrayInterop.AppendMenu(hMenu, TrayInterop.MF_POPUP, hBudgetMenu, isTr ? "Kaynak Bütçesi" : "Resource Budget");
            }

            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            TrayInterop.AppendMenu(hMenu, TrayInterop.MF_STRING, (IntPtr)TrayInterop.CmdExit, loc.GetString(NiceWP.Infrastructure.Localization.LocalizationKeys.TrayExit));

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
                case TrayInterop.CmdProfileAuto:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Automatic);
                    break;
                case TrayInterop.CmdProfileNormal:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Normal);
                    break;
                case TrayInterop.CmdProfileFocus:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Focus);
                    break;
                case TrayInterop.CmdProfileGaming:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Gaming);
                    break;
                case TrayInterop.CmdProfilePresentation:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Presentation);
                    break;
                case TrayInterop.CmdProfileBatterySaver:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.BatterySaver);
                    break;
                case TrayInterop.CmdProfileCinema:
                    ProfileSelected?.Invoke(NiceWP.Core.AdaptiveDesktopProfile.Cinema);
                    break;
                case TrayInterop.CmdBudgetAuto:
                    BudgetSelected?.Invoke(NiceWP.Core.ResourceBudget.Automatic);
                    break;
                case TrayInterop.CmdBudgetLow:
                    BudgetSelected?.Invoke(NiceWP.Core.ResourceBudget.Low);
                    break;
                case TrayInterop.CmdBudgetBalanced:
                    BudgetSelected?.Invoke(NiceWP.Core.ResourceBudget.Balanced);
                    break;
                case TrayInterop.CmdBudgetHigh:
                    BudgetSelected?.Invoke(NiceWP.Core.ResourceBudget.High);
                    break;
            }
        }
        finally
        {
            TrayInterop.DestroyMenu(hMenu);
        }
    }

    private static void AppendProfileItem(IntPtr hMenu, int cmd, string text, bool isChecked)
    {
        var flags = TrayInterop.MF_STRING | (isChecked ? TrayInterop.MF_CHECKED : TrayInterop.MF_UNCHECKED);
        TrayInterop.AppendMenu(hMenu, flags, (IntPtr)cmd, text);
    }

    private static void AppendBudgetItem(IntPtr hMenu, int cmd, string text, bool isChecked)
    {
        var flags = TrayInterop.MF_STRING | (isChecked ? TrayInterop.MF_CHECKED : TrayInterop.MF_UNCHECKED);
        TrayInterop.AppendMenu(hMenu, flags, (IntPtr)cmd, text);
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
