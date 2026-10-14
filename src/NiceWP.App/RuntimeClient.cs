using NiceWP.Infrastructure.Ipc;

namespace NiceWP.App;

/// <summary>
/// Minimal IPC client service for NiceWP.App to interact with the separate NiceWP.Runtime.Host process.
/// Encapsulates connection lifecycle, timeouts, and error handling away from WinUI views and code-behind.
/// </summary>
public sealed class RuntimeClient
{
    private readonly RuntimeIpcClient ipcClient;
    private readonly RuntimeHostLauncher launcher;

    public string PipeName => ipcClient.PipeName;
    public RuntimeHostLauncher Launcher => launcher;
    public RuntimeIpcClient IpcClient => ipcClient;

    public RuntimeClient(string? pipeName = null, string? customHostExePath = null)
    {
        ipcClient = new RuntimeIpcClient(pipeName);
        launcher = new RuntimeHostLauncher(ipcClient, customHostExePath);
    }

    /// <summary>
    /// Ensures that the runtime host process is running. If not already active, launches it in the background
    /// without a window and waits until the IPC endpoint is responsive.
    /// </summary>
    public Task<IpcResponse> EnsureHostRunningAsync(
        int timeoutMs = IpcConstants.DefaultHostStartupTimeoutMs,
        int retryIntervalMs = IpcConstants.DefaultHostStartupRetryIntervalMs,
        CancellationToken cancellationToken = default) =>
        launcher.EnsureHostRunningAsync(timeoutMs, retryIntervalMs, cancellationToken);

    /// <summary>
    /// Sends a Ping request to verify that the runtime host is responsive.
    /// </summary>
    public Task<IpcResponse> PingAsync(
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.PingAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Sends a Shutdown command to trigger a graceful shutdown in the runtime host.
    /// </summary>
    public Task<IpcResponse> ShutdownAsync(
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.ShutdownAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Requests the runtime host to render and display an image wallpaper.
    /// </summary>
    public Task<IpcResponse> SetImageAsync(
        string path,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        ipcClient.SetImageAsync(path, timeoutMs, cancellationToken);

    /// <summary>
    /// Requests the runtime host to render and play a video wallpaper.
    /// </summary>
    public Task<IpcResponse> SetVideoAsync(
        string path,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default) =>
        ipcClient.SetVideoAsync(path, timeoutMs, cancellationToken);

    /// <summary>
    /// Requests the runtime host to stop and detach the active wallpaper.
    /// </summary>
    public Task<IpcResponse> StopWallpaperAsync(
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        ipcClient.StopWallpaperAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Requests the current wallpaper runtime status (running state, active wallpaper, type, and path).
    /// </summary>
    public Task<IpcResponse> GetStatusAsync(
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.GetStatusAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Requests current performance metrics and rolling short-term statistics from the runtime host.
    /// </summary>
    public Task<IpcResponse> GetPerformanceStatusAsync(
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.GetPerformanceStatusAsync(timeoutMs, cancellationToken);

    /// <summary>
    /// Sends a raw command request to the runtime host.
    /// </summary>
    public Task<IpcResponse> SendCommandAsync(
        string command,
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.SendCommandAsync(command, timeoutMs, cancellationToken);
}
