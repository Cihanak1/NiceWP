namespace NiceWP.Core;

/// <summary>
/// Specifies the active wallpaper scheduling mode.
/// </summary>
public enum ScheduleMode
{
    /// <summary>
    /// Scheduling is disabled. Zero timers run.
    /// </summary>
    Off,

    /// <summary>
    /// Automatic Day / Night transition based on user-configured times.
    /// </summary>
    DayNight
}

/// <summary>
/// Extension methods for <see cref="ScheduleMode"/>.
/// </summary>
public static class ScheduleModeExtensions
{
    public const string ModeOff = "Off";
    public const string ModeDayNight = "DayNight";

    public static string ToConfigString(this ScheduleMode mode) => mode switch
    {
        ScheduleMode.DayNight => ModeDayNight,
        _ => ModeOff
    };

    public static string ToDisplayString(this ScheduleMode mode) => mode switch
    {
        ScheduleMode.DayNight => "Day & Night",
        _ => "Off"
    };

    public static ScheduleMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ScheduleMode.Off;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, ModeDayNight, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Day & Night", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Day/Night", StringComparison.OrdinalIgnoreCase))
        {
            return ScheduleMode.DayNight;
        }

        return ScheduleMode.Off;
    }
}
