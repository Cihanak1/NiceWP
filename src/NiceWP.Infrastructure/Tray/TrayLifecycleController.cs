using System.Diagnostics;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Tray;

/// <summary>
/// Manages the window and application lifecycle when interacting with the system tray.
/// Decouples lifecycle policies (minimize/hide on close, open/restore, stop wallpaper, clean exit)
/// from the WinUI XAML view and native Win32 message handling.
/// </summary>
public sealed class TrayLifecycleController
{
    private readonly RuntimeIpcClient _ipcClient;
    private readonly Action? _restoreWindowAction;
    private readonly Action? _hideWindowAction;
    private readonly Action? _exitAppAction;
    private readonly Func<Task>? _syncStatusAction;

    public bool IsWindowVisible { get; private set; } = true;
    public bool IsExited { get; private set; }
    public string? ActiveWallpaperPath { get; private set; }
    public string ActiveWallpaperType { get; private set; } = IpcConstants.WallpaperTypeNone;

    public TrayLifecycleController(
        RuntimeIpcClient ipcClient,
        bool initialWindowVisible = true,
        Action? restoreWindowAction = null,
        Action? hideWindowAction = null,
        Action? exitAppAction = null,
        Func<Task>? syncStatusAction = null)
    {
        _ipcClient = ipcClient;
        IsWindowVisible = initialWindowVisible;
        _restoreWindowAction = restoreWindowAction;
        _hideWindowAction = hideWindowAction;
        _exitAppAction = exitAppAction;
        _syncStatusAction = syncStatusAction;
    }

    /// <summary>
    /// Updates the tracked active wallpaper path and type.
    /// </summary>
    public void SetActiveWallpaper(string? path, string type)
    {
        ActiveWallpaperPath = path;
        ActiveWallpaperType = string.IsNullOrWhiteSpace(type) ? IpcConstants.WallpaperTypeNone : type;
    }

    /// <summary>
    /// Handles the close request (e.g. clicking the title bar 'X').
    /// Cancels application termination and hides the window instead, keeping the app and runtime host alive.
    /// </summary>
    public void OnWindowCloseRequested(out bool cancelClose)
    {
        if (IsExited)
        {
            cancelClose = false;
            return;
        }

        cancelClose = true;
        IsWindowVisible = false;
        _hideWindowAction?.Invoke();
    }

    /// <summary>
    /// Restores and unhides the main window, then queries the runtime host for the latest status.
    /// </summary>
    public async Task RequestOpenWindowAsync()
    {
        if (IsExited) return;

        IsWindowVisible = true;
        _restoreWindowAction?.Invoke();

        if (_syncStatusAction != null)
        {
            await _syncStatusAction.Invoke();
        }
    }

    /// <summary>
    /// Sends a StopWallpaper request to the runtime host, updating the local state to "No wallpaper"
    /// without closing the host process or the UI app process.
    /// </summary>
    public async Task<IpcResponse> RequestStopWallpaperAsync(int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds)
    {
        if (IsExited)
        {
            return new IpcResponse { Success = false, Message = "Application has exited." };
        }

        var response = await _ipcClient.StopWallpaperAsync(timeoutMs);
        if (response.Success)
        {
            ActiveWallpaperPath = null;
            ActiveWallpaperType = IpcConstants.WallpaperTypeNone;
        }

        return response;
    }

    /// <summary>
    /// Performs a graceful application exit: stops any active wallpaper, sends Shutdown to the runtime host,
    /// and invokes the app exit callback.
    /// </summary>
    public async Task RequestExitAsync(int stopTimeoutMs = 1500, int shutdownTimeoutMs = 2000)
    {
        if (IsExited) return;
        IsExited = true;

        try
        {
            if (!string.IsNullOrEmpty(ActiveWallpaperPath) &&
                !string.Equals(ActiveWallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _ipcClient.StopWallpaperAsync(stopTimeoutMs);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to stop wallpaper during exit: {ex}");
                }
            }

            try
            {
                await _ipcClient.ShutdownAsync(shutdownTimeoutMs);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to shutdown runtime host during exit: {ex}");
            }
        }
        finally
        {
            _exitAppAction?.Invoke();
        }
    }
}
