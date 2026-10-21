using NiceWP.Core;

namespace NiceWP.Infrastructure.Scheduling;

/// <summary>
/// Snapshot representing the current state and next transition of the wallpaper schedule.
/// </summary>
public sealed class WallpaperScheduleStatus
{
    public ScheduleMode Mode { get; init; } = ScheduleMode.Off;

    public DayNightSlot CurrentSlot { get; init; } = DayNightSlot.None;

    public DayNightSlot NextSlot { get; init; } = DayNightSlot.None;

    public DateTimeOffset? NextTransitionUtc { get; init; }

    public bool IsOverdue { get; init; }

    public string? CurrentWallpaperId { get; init; }

    public string? NextWallpaperId { get; init; }

    public bool IsActive => Mode != ScheduleMode.Off;

    public string FormattedState => Mode switch
    {
        ScheduleMode.DayNight => CurrentSlot == DayNightSlot.Day ? "Day" : CurrentSlot == DayNightSlot.Night ? "Night" : "Active",
        _ => "Off"
    };
}
