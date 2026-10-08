using NiceWP.Infrastructure.Ipc;

namespace NiceWP.App;

/// <summary>
/// Minimal IPC client service for NiceWP.App to interact with the separate NiceWP.Runtime.Host process.
/// Encapsulates connection lifecycle, timeouts, and error handling away from WinUI views and code-behind.
/// </summary>
public sealed class RuntimeClient
{
    private readonly RuntimeIpcClient ipcClient;

    public string PipeName => ipcClient.PipeName;

    public RuntimeClient(string? pipeName = null)
    {
        ipcClient = new RuntimeIpcClient(pipeName);
    }

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
    /// Sends a raw command request to the runtime host.
    /// </summary>
    public Task<IpcResponse> SendCommandAsync(
        string command,
        int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ipcClient.SendCommandAsync(command, timeoutMs, cancellationToken);
}
