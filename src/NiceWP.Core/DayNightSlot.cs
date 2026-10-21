namespace NiceWP.Core;

/// <summary>
/// Identifies the active slot in a Day/Night schedule.
/// </summary>
public enum DayNightSlot
{
    /// <summary>
    /// No slot is active (e.g. scheduling is disabled).
    /// </summary>
    None,

    /// <summary>
    /// Daytime period.
    /// </summary>
    Day,

    /// <summary>
    /// Nighttime period.
    /// </summary>
    Night
}
