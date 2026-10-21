namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements behavioral policies and detection heuristics for Gaming Profile.
/// When fullscreen games or gaming processes are active, wallpaper rendering is immediately paused,
/// automated rotation and schedule transitions are suppressed, and the system is cued for Deep Freeze
/// to yield maximum GPU/CPU performance to the game.
/// </summary>
public static class GamingProfileBehavior
{
    private static readonly HashSet<string> KnownGamingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe",
        "cs2.exe",
        "dota2.exe",
        "valorant.exe",
        "riotclientservices.exe",
        "leagueclient.exe",
        "overwatch.exe",
        "gta5.exe",
        "cyberpunk2077.exe",
        "fortniteclient-win64-shipping.exe",
        "minecraft.exe",
        "javaw.exe"
    };

    /// <summary>
    /// Checks whether the given process name represents a recognized game executable.
    /// </summary>
    public static bool IsGamingProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = AppRuleNormalizer.NormalizeProcessName(processName);
        return KnownGamingProcesses.Contains(normalized);
    }
}
