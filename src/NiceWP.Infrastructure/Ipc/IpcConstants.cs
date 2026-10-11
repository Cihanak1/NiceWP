namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Constants and defaults for local IPC communication between App and Runtime.Host.
/// </summary>
public static class IpcConstants
{
    public const string PingCommand = "Ping";
    public const string ShutdownCommand = "Shutdown";
    public const string SetImageCommand = "SetImage";
    public const string SetVideoCommand = "SetVideo";
    public const string StopWallpaperCommand = "StopWallpaper";
    public const string GetStatusCommand = "GetStatus";

    public const string PongMessage = "Pong";
    public const string ShutdownMessage = "Shutting down";

    public const string WallpaperTypeNone = "None";
    public const string WallpaperTypeImage = "Image";
    public const string WallpaperTypeVideo = "Video";

    public const int DefaultTimeoutMilliseconds = 3000;
    public const int MaxMessageLength = 65536; // 64 KB limit

    public const string HostExecutableName = "NiceWP.Runtime.Host.exe";
    public const int DefaultHostStartupTimeoutMs = 5000;
    public const int DefaultHostStartupRetryIntervalMs = 100;
    public const int DefaultInitialPingTimeoutMs = 300;
    public const int DefaultRestoreWaitTimeoutMs = 2000;

    public static string GetDefaultPipeName() =>
        $"NiceWP.Runtime.Pipe.{Environment.UserName}";
}
