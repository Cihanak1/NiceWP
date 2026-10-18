namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Represents the progress of a remote wallpaper download and validation operation.
/// </summary>
public sealed record RemoteDownloadProgress(
    string Phase,
    long BytesReceived,
    long TotalBytes,
    int? Percentage)
{
    public string DisplayText => Percentage.HasValue
        ? $"Downloading {Percentage.Value}%"
        : (Phase == "Downloading" ? "Downloading..." : Phase);
}
