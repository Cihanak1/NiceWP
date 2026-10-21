namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements behavioral policies and detection heuristics for Cinema Profile.
/// During media playback, background wallpaper motion and audio are muted or halted,
/// and automated rotation is held to deliver an uninterrupted viewing experience.
/// </summary>
public static class CinemaProfileBehavior
{
    private static readonly HashSet<string> KnownCinemaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc.exe",
        "mpc-hc64.exe",
        "mpc-hc.exe",
        "mpc-be64.exe",
        "mpc-be.exe",
        "potplayermini64.exe",
        "potplayer64.exe",
        "kmplayer.exe",
        "kodi.exe",
        "plex.exe",
        "netflix.exe"
    };

    /// <summary>
    /// Checks whether the given process name represents a recognized media player or streaming app.
    /// </summary>
    public static bool IsCinemaProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = AppRuleNormalizer.NormalizeProcessName(processName);
        return KnownCinemaProcesses.Contains(normalized);
    }
}
