using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using NiceWP.App.Tray;
using NiceWP.Infrastructure.Autostart;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Tray;

namespace NiceWP.App;

public sealed partial class DesktopTestWindow : Window
{
    private readonly RuntimeClient runtimeClient = new();
    private readonly TextWriterTraceListener? diagnostics;
    private readonly AppTrayIcon trayIcon;
    private readonly TrayLifecycleController controller;
    private readonly AutostartManager autostartManager = new();
    private string? activeWallpaperPath;
    private string activeWallpaperType = IpcConstants.WallpaperTypeNone;
    private bool closed;
    private bool selecting;
    private bool isSyncingAutostart;

    public DesktopTestWindow(bool isStartup = false)
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 260));
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;

        if (isStartup)
        {
            AppWindow.Hide();
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = (IntPtr)AppWindow.Id.Value;
        }

        controller = new TrayLifecycleController(
            runtimeClient.IpcClient,
            initialWindowVisible: !isStartup,
            restoreWindowAction: RestoreAndShowWindow,
            hideWindowAction: () => AppWindow.Hide(),
            exitAppAction: PerformAppExit,
            syncStatusAction: SyncRuntimeStatusAsync);

        trayIcon = new AppTrayIcon(hwnd);
        trayIcon.OpenRequested += OnTrayOpenRequested;
        trayIcon.StopWallpaperRequested += OnTrayStopWallpaperRequested;
        trayIcon.ExitRequested += OnTrayExitRequested;
        trayIcon.Initialize();

        try
        {
            diagnostics = new TextWriterTraceListener(Path.Combine(Path.GetTempPath(), $"NiceWP-image-test-{Environment.ProcessId}.log"));
            Trace.Listeners.Add(diagnostics);
            Trace.AutoFlush = true;
            Trace.WriteLine($"NiceWP session started: pid={Environment.ProcessId}");
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            StatusText.Text = "Diagnostic log could not be opened.";
        }

        RefreshAutostartState();

        _ = SyncRuntimeStatusAsync();
    }

    public async Task SyncRuntimeStatusAsync()
    {
        try
        {
            var ensure = await runtimeClient.EnsureHostRunningAsync();
            if (closed) return;

            if (!ensure.Success)
            {
                StatusText.Text = ensure.Message ?? "Runtime host could not be started.";
                PathText.Text = "No wallpaper";
                return;
            }

            var status = await runtimeClient.GetStatusAsync();
            if (closed) return;

            if (status.Success && status.HasActiveWallpaper && !string.IsNullOrWhiteSpace(status.ActivePath))
            {
                activeWallpaperPath = status.ActivePath;
                activeWallpaperType = status.WallpaperType;
                controller.SetActiveWallpaper(activeWallpaperPath, activeWallpaperType);
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
                StatusText.Text = string.Empty;
            }
            else
            {
                activeWallpaperPath = null;
                activeWallpaperType = IpcConstants.WallpaperTypeNone;
                controller.SetActiveWallpaper(null, IpcConstants.WallpaperTypeNone);
                PathText.Text = "No wallpaper";
                StatusText.Text = status.Success ? string.Empty : (status.Message ?? "Failed to query runtime status.");
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error synchronizing runtime status: {exception}");
            if (!closed)
            {
                StatusText.Text = "Failed to synchronize status with runtime host.";
                PathText.Text = "No wallpaper";
            }
        }
    }

    private static string FormatWallpaperDisplay(string? path, string? wallpaperType = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase))
        {
            return "No wallpaper";
        }

        var fileName = Path.GetFileName(path);
        var isVideo = string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
        return isVideo ? $"Active video: {fileName}" : $"Active image: {fileName}";
    }

    private async void OnSelectImage(object sender, RoutedEventArgs args)
    {
        if (selecting || closed) return;
        selecting = true;
        SelectImageButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".mp4");
            var file = await picker.PickSingleFileAsync();
            if (file == null || closed) return;

            var isVideo = string.Equals(Path.GetExtension(file.Path), ".mp4", StringComparison.OrdinalIgnoreCase);
            var result = isVideo
                ? await runtimeClient.SetVideoAsync(file.Path)
                : await runtimeClient.SetImageAsync(file.Path);

            if (closed) return;

            if (result.Success)
            {
                activeWallpaperPath = file.Path;
                activeWallpaperType = isVideo ? IpcConstants.WallpaperTypeVideo : IpcConstants.WallpaperTypeImage;
                controller.SetActiveWallpaper(activeWallpaperPath, activeWallpaperType);
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
                StatusText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = result.Message ?? "Failed to set wallpaper.";
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed) StatusText.Text = "The wallpaper could not be selected or sent to runtime host.";
        }
        finally
        {
            selecting = false;
            if (!closed) SelectImageButton.IsEnabled = true;
        }
    }

    private void RefreshAutostartState()
    {
        isSyncingAutostart = true;
        try
        {
            AutostartCheckBox.IsChecked = autostartManager.IsEnabled;
        }
        finally
        {
            isSyncingAutostart = false;
        }
    }

    private void OnAutostartChecked(object sender, RoutedEventArgs args)
    {
        if (isSyncingAutostart) return;

        var success = autostartManager.Enable();
        if (!success)
        {
            StatusText.Text = "Failed to enable autostart in registry.";
            RefreshAutostartState();
        }
        else
        {
            StatusText.Text = string.Empty;
        }
    }

    private void OnAutostartUnchecked(object sender, RoutedEventArgs args)
    {
        if (isSyncingAutostart) return;

        var success = autostartManager.Disable();
        if (!success)
        {
            StatusText.Text = "Failed to disable autostart in registry.";
            RefreshAutostartState();
        }
        else
        {
            StatusText.Text = string.Empty;
        }
    }

    private void RestoreAndShowWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show(activateWindow: true);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) hwnd = (IntPtr)AppWindow.Id.Value;

            if (TrayInterop.IsIconic(hwnd))
            {
                TrayInterop.ShowWindow(hwnd, TrayInterop.SW_RESTORE);
            }
            else
            {
                TrayInterop.ShowWindow(hwnd, TrayInterop.SW_SHOW);
            }
            TrayInterop.SetForegroundWindow(hwnd);

            RefreshAutostartState();
            _ = SyncRuntimeStatusAsync();
        });
    }

    private void PerformAppExit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            trayIcon.Dispose();
            App.ReleaseSingleInstance();
            closed = true;
            AppWindow.Closing -= OnClosing;
            Close();
            Application.Current.Exit();
            Environment.Exit(0);
        });
    }

    private void OnTrayOpenRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await controller.RequestOpenWindowAsync();
        });
    }

    private void OnTrayStopWallpaperRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var result = await controller.RequestStopWallpaperAsync();
            if (result.Success)
            {
                activeWallpaperPath = null;
                activeWallpaperType = IpcConstants.WallpaperTypeNone;
                PathText.Text = "No wallpaper";
                StatusText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = result.Message ?? "Failed to stop wallpaper.";
            }
        });
    }

    private void OnTrayExitRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await controller.RequestExitAsync();
        });
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (closed || args.WindowActivationState == WindowActivationState.Deactivated) return;
        RefreshAutostartState();
        PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        controller.OnWindowCloseRequested(out var cancelClose);
        args.Cancel = cancelClose;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        trayIcon.Dispose();
        App.ReleaseSingleInstance();
        Trace.WriteLine("Control window closed; runtime host continues in background.");
        if (diagnostics != null)
        {
            Trace.Listeners.Remove(diagnostics);
            diagnostics.Dispose();
        }
    }
}
