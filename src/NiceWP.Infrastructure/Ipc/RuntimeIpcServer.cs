using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Lightweight named pipe server for receiving local IPC requests in Runtime Host.
/// Handles single client at a time, responds to Ping/Shutdown, and delegates application commands.
/// </summary>
public sealed class RuntimeIpcServer : IDisposable
{
    private readonly string pipeName;
    private readonly Action? onShutdownRequested;
    private readonly Func<IpcRequest, Task<IpcResponse>>? onCommandHandler;
    private readonly CancellationTokenSource cts = new();
    private readonly object syncRoot = new();
    private Task? listenTask;
    private NamedPipeServerStream? currentServerStream;
    private bool disposed;

    public string PipeName => pipeName;
    public bool IsRunning => listenTask != null && !cts.IsCancellationRequested;

    public RuntimeIpcServer(
        string? pipeName = null,
        Action? onShutdownRequested = null,
        Func<IpcRequest, Task<IpcResponse>>? onCommandHandler = null)
    {
        this.pipeName = pipeName ?? IpcConstants.GetDefaultPipeName();
        this.onShutdownRequested = onShutdownRequested;
        this.onCommandHandler = onCommandHandler;
    }

    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed) throw new ObjectDisposedException(nameof(RuntimeIpcServer));
            if (listenTask != null) return;

            listenTask = Task.Run(ListenLoopAsync);
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeSecurityHelper.CreateServerStream(pipeName, 1);
                lock (syncRoot)
                {
                    if (cts.IsCancellationRequested)
                    {
                        server.Dispose();
                        break;
                    }
                    currentServerStream = server;
                }

                await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                await HandleClientAsync(server, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"IPC server connection error: {exception.Message}");
            }
            finally
            {
                lock (syncRoot)
                {
                    if (currentServerStream == server)
                    {
                        currentServerStream = null;
                    }
                }

                if (server != null)
                {
                    try
                    {
                        if (server.IsConnected)
                        {
                            server.Disconnect();
                        }
                    }
                    catch
                    {
                        // Ignore disconnect failures during teardown
                    }
                    server.Dispose();
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Client connection lost
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null)
            {
                // End of stream; client disconnected cleanly
                break;
            }

            if (line.Length > IpcConstants.MaxMessageLength)
            {
                var oversizedResponse = IpcResponse.Fail(string.Empty, "Message exceeds allowed length.");
                var oversizedJson = JsonSerializer.Serialize(oversizedResponse);
                try
                {
                    await writer.WriteLineAsync(oversizedJson.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                continue;
            }

            var (response, shouldShutdown) = await ProcessMessageAsync(line).ConfigureAwait(false);
            var responseJson = JsonSerializer.Serialize(response);
            try
            {
                await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!shouldShutdown)
                {
                    break;
                }
            }

            if (shouldShutdown)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        if (!disposed)
                        {
                            onShutdownRequested?.Invoke();
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Error invoking IPC shutdown callback: {ex}");
                    }
                });
                break;
            }
        }
    }

    public async Task<(IpcResponse Response, bool ShouldShutdown)> ProcessMessageAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (IpcResponse.Fail(string.Empty, "Malformed request: empty message."), false);
        }

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return (IpcResponse.Fail(string.Empty, "Malformed request: invalid JSON."), false);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return (IpcResponse.Fail(request?.Id ?? string.Empty, "Malformed request: missing command."), false);
        }

        if (string.Equals(request.Command, IpcConstants.PingCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Ok(request.Id, IpcConstants.PongMessage), false);
        }

        if (string.Equals(request.Command, IpcConstants.ShutdownCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Ok(request.Id, IpcConstants.ShutdownMessage), true);
        }

        if (onCommandHandler != null)
        {
            try
            {
                var response = await onCommandHandler(request).ConfigureAwait(false);
                if (string.IsNullOrEmpty(response.RequestId))
                {
                    response.RequestId = request.Id;
                }
                return (response, false);
            }
            catch (Exception exception)
            {
                return (IpcResponse.Fail(request.Id, $"Command failed: {exception.Message}"), false);
            }
        }

        if (string.Equals(request.Command, IpcConstants.GetStatusCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Status(request.Id, isRunning: true, hasActiveWallpaper: false, wallpaperType: IpcConstants.WallpaperTypeNone, activePath: null), false);
        }

        return (IpcResponse.Fail(request.Id, $"Unknown command: '{request.Command}'"), false);
    }

    public static (IpcResponse Response, bool ShouldShutdown) ProcessMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (IpcResponse.Fail(string.Empty, "Malformed request: empty message."), false);
        }

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return (IpcResponse.Fail(string.Empty, "Malformed request: invalid JSON."), false);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return (IpcResponse.Fail(request?.Id ?? string.Empty, "Malformed request: missing command."), false);
        }

        if (string.Equals(request.Command, IpcConstants.PingCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Ok(request.Id, IpcConstants.PongMessage), false);
        }

        if (string.Equals(request.Command, IpcConstants.ShutdownCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Ok(request.Id, IpcConstants.ShutdownMessage), true);
        }

        if (string.Equals(request.Command, IpcConstants.GetStatusCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (IpcResponse.Status(request.Id, isRunning: true, hasActiveWallpaper: false, wallpaperType: IpcConstants.WallpaperTypeNone, activePath: null), false);
        }

        return (IpcResponse.Fail(request.Id, $"Unknown command: '{request.Command}'"), false);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;

            cts.Cancel();
            try
            {
                currentServerStream?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
            cts.Dispose();
        }
    }
}
