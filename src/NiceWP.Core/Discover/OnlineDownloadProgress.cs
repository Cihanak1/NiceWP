namespace NiceWP.Core.Discover;

/// <summary>
/// Reports progress updates for online wallpaper downloads and library imports.
/// </summary>
public sealed record OnlineDownloadProgress(
    string Phase,
    long BytesReceived,
    long TotalBytes,
    int? Percentage)
{
    public string DisplayText => Percentage.HasValue
        ? $"{Phase} {Percentage.Value}%"
        : Phase;
}
