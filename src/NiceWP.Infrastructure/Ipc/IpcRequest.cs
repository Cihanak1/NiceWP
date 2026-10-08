namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Simple JSON-serializable request model for local IPC commands.
/// </summary>
public sealed class IpcRequest
{
    public string Id { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Path { get; set; }

    public IpcRequest()
    {
    }

    public IpcRequest(string command, string? path = null, string? id = null)
    {
        Command = command;
        Path = path;
        Id = id ?? Guid.NewGuid().ToString("N");
    }
}
