namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements behavioral policies and detection heuristics for Presentation Profile.
/// During presentations, video wallpapers are paused, rotation and schedule shifts are suppressed,
/// and an optional presentation wallpaper is cleanly applied to prevent distractions.
/// </summary>
public static class PresentationProfileBehavior
{
    private static readonly HashSet<string> KnownPresentationProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "powerpnt.exe",
        "teams.exe",
        "zoom.exe",
        "webex.exe",
        "obs64.exe",
        "obs32.exe",
        "keynote.exe"
    };

    /// <summary>
    /// Checks whether the given process name represents a known presentation, meeting, or screen share application.
    /// </summary>
    public static bool IsPresentationProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = AppRuleNormalizer.NormalizeProcessName(processName);
        return KnownPresentationProcesses.Contains(normalized);
    }
}
