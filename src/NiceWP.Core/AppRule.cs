namespace NiceWP.Core;

/// <summary>
/// User-defined rule mapping an executable process name to a custom desktop behavior or profile.
/// Privacy-first: strictly evaluates local process names without window titles or file path arguments.
/// </summary>
public sealed record AppRule
{
    /// <summary>
    /// The executable name of the application (e.g. "devenv.exe", "POWERPNT.EXE").
    /// </summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// The behavior to engage when this process is the foreground window.
    /// </summary>
    public AppRuleBehavior Behavior { get; init; } = AppRuleBehavior.NoAction;

    /// <summary>
    /// Optional designated wallpaper ID applied when this rule matches (used with UseWallpaper or Presentation).
    /// </summary>
    public string? OptionalWallpaperId { get; init; }
}
