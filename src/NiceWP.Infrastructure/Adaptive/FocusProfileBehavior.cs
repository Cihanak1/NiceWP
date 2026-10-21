namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Implements behavioral policies and detection heuristics for Focus Profile.
/// During focused work, video animation is paused to eliminate peripheral distractions,
/// automated rotation is postponed to avoid abrupt visual shifts, and serene static images are preserved.
/// </summary>
public static class FocusProfileBehavior
{
    private static readonly HashSet<string> KnownFocusProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv.exe",
        "code.exe",
        "rider64.exe",
        "idea64.exe",
        "obsidian.exe",
        "notion.exe",
        "winword.exe",
        "wordpad.exe"
    };

    /// <summary>
    /// Checks whether the given process name represents a recognized productivity, IDE, or focus application.
    /// </summary>
    public static bool IsFocusProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = AppRuleNormalizer.NormalizeProcessName(processName);
        return KnownFocusProcesses.Contains(normalized);
    }
}
