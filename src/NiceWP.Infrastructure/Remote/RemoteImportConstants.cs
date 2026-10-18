namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Central size limits, timeouts, and storage paths for safe remote wallpaper import.
/// </summary>
public static class RemoteImportConstants
{
    /// <summary>
    /// Maximum allowed file size for image wallpapers (100 MB).
    /// </summary>
    public const long MaxImageSizeBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum allowed file size for video wallpapers (1 GB).
    /// </summary>
    public const long MaxVideoSizeBytes = 1024L * 1024L * 1024L;

    /// <summary>
    /// Global streaming upper bound when media type is initially unknown (1 GB).
    /// </summary>
    public const long MaxGlobalStreamSizeBytes = 1024L * 1024L * 1024L;

    /// <summary>
    /// Maximum allowed HTTP redirects before aborting.
    /// </summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// Default network connect and download timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public const string DefaultFolderName = "NiceWP";
    public const string TempRelativePath = "temp\\remote";
    public const string StorageRelativePath = "library\\remote";

    public static readonly string[] AllowedSchemes = ["http", "https"];
}
