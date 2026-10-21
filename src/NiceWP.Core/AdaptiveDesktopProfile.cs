namespace NiceWP.Core;

/// <summary>
/// Defines the high-level behavioral profile under which NiceWP operates.
/// Profiles dictate how wallpaper rendering, rotation, scheduling, and power management adapt to PC activity.
/// </summary>
public enum AdaptiveDesktopProfile
{
    /// <summary>
    /// Automatically resolves the active profile based on running foreground applications,
    /// fullscreen state, battery/power source, and user-defined app rules.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Standard baseline profile. Normal wallpaper rendering, rotation, and scheduling.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Focus profile for work or study. Pauses or freezes motion-heavy video wallpapers
    /// while preserving serene static surfaces; postpones automated rotation.
    /// </summary>
    Focus = 2,

    /// <summary>
    /// High-performance gaming profile. Immediately pauses video rendering, postpones rotation and
    /// schedule transitions, and enters Deep Freeze after grace period to release decoder resources.
    /// </summary>
    Gaming = 3,

    /// <summary>
    /// Presentation profile for meetings and screen sharing. Motion is stopped, rotation postponed,
    /// and an optional designated presentation wallpaper is cleanly displayed.
    /// </summary>
    Presentation = 4,

    /// <summary>
    /// Maximum energy conservation profile for battery saver mode. Deep freezes video playback
    /// and minimizes background runtime activity.
    /// </summary>
    BatterySaver = 5,

    /// <summary>
    /// Cinema profile for video playback and full-screen entertainment. Silences background wallpaper
    /// resource drain without affecting foreground streaming.
    /// </summary>
    Cinema = 6
}
