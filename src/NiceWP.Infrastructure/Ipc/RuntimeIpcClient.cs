using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Low-level named pipe client for local IPC communication with NiceWP.Runtime.Host.
/// Enforces connection timeout, message framing, and structured error handling.
/// </summary>
public sealed class RuntimeIpcClient
{
    private readonly string pipeName;

    public string PipeName => pipeName;

    public RuntimeIpcClient(string? pipeName = null)
    {
        this.pipeName = pipeName ?? IpcConstants.GetDefaultPipeName();
    }

    public Task<IpcResponse> PingAsync(int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.PingCommand, timeoutMs, cancellationToken);

    public Task<IpcResponse> ShutdownAsync(int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.ShutdownCommand, timeoutMs, cancellationToken);

    public Task<IpcResponse> SetImageAsync(string path, int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        SendRequestAsync(new IpcRequest(IpcConstants.SetImageCommand, path), timeoutMs, cancellationToken);

    public Task<IpcResponse> SetVideoAsync(string path, int timeoutMs = 10000, CancellationToken cancellationToken = default) =>
        SendRequestAsync(new IpcRequest(IpcConstants.SetVideoCommand, path), timeoutMs, cancellationToken);

    public Task<IpcResponse> StopWallpaperAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.StopWallpaperCommand, timeoutMs, cancellationToken);

    public Task<IpcResponse> PauseWallpaperAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.PauseWallpaperCommand, timeoutMs, cancellationToken);

    public Task<IpcResponse> ResumeWallpaperAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.ResumeWallpaperCommand, timeoutMs, cancellationToken);

    public Task<IpcResponse> GetStatusAsync(int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default) =>
        SendCommandAsync(IpcConstants.GetStatusCommand, timeoutMs, cancellationToken);

    public async Task<IpcResponse> SendCommandAsync(string command, int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(command);
        return await SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcResponse> SendRequestAsync(IpcRequest request, int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var requestJson = JsonSerializer.Serialize(request);
        return await SendRawAsync(request.Id, requestJson, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcResponse> SendRawAsync(string requestId, string payload, int timeoutMs = IpcConstants.DefaultTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);

        try
        {
            using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await clientStream.ConnectAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IpcResponse.Fail(requestId, "Runtime host is unavailable (connection timed out).");
            }
            catch (TimeoutException)
            {
                return IpcResponse.Fail(requestId, "Runtime host is unavailable (connection timed out).");
            }

            using var writer = new StreamWriter(clientStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(clientStream, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync(payload.AsMemory(), linkedCts.Token).ConfigureAwait(false);

            string? responseLine;
            try
            {
                responseLine = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IpcResponse.Fail(requestId, "Runtime host response timed out.");
            }

            if (responseLine == null)
            {
                return IpcResponse.Fail(requestId, "Runtime host disconnected without a response.");
            }

            if (responseLine.Length > IpcConstants.MaxMessageLength)
            {
                return IpcResponse.Fail(requestId, "Response exceeded allowed length.");
            }

            try
            {
                var response = JsonSerializer.Deserialize<IpcResponse>(responseLine, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return response ?? IpcResponse.Fail(requestId, "Empty response received.");
            }
            catch (JsonException)
            {
                return IpcResponse.Fail(requestId, "Malformed response from runtime host.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IpcResponse.Fail(requestId, "Request was cancelled.");
        }
        catch (Exception exception)
        {
            return IpcResponse.Fail(requestId, $"IPC communication error: {exception.Message}");
        }
    }
}
