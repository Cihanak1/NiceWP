using NiceWP.Core;

namespace NiceWP.Infrastructure.Scheduling;

/// <summary>
/// Information about current and next schedule transition times.
/// </summary>
public readonly record struct ScheduleTransitionInfo(
    DayNightSlot CurrentSlot,
    DayNightSlot NextSlot,
    DateTimeOffset NextTransitionUtc,
    TimeSpan DueIn
);

/// <summary>
/// Pure, thread-safe resolver for Day/Night schedule slot determination, midnight wrapping,
/// and transition due time calculations. Zero periodic polling overhead.
/// </summary>
public static class DayNightScheduleResolver
{
    /// <summary>
    /// Determines the active slot (Day or Night) for a given time of day.
    /// </summary>
    public static DayNightSlot ResolveCurrentSlot(TimeOnly current, TimeOnly dayStart, TimeOnly nightStart)
    {
        if (dayStart == nightStart)
        {
            return DayNightSlot.Day;
        }

        if (dayStart < nightStart)
        {
            // Standard daytime: e.g. Day from 08:00 to 20:00
            return current >= dayStart && current < nightStart
                ? DayNightSlot.Day
                : DayNightSlot.Night;
        }
        else
        {
            // Midnight wrap: e.g. Day from 22:00 to 06:00
            return current >= dayStart || current < nightStart
                ? DayNightSlot.Day
                : DayNightSlot.Night;
        }
    }

    /// <summary>
    /// Calculates the next transition details from a local time representation.
    /// </summary>
    public static ScheduleTransitionInfo CalculateNextTransition(
        DateTimeOffset now,
        TimeOnly dayStart,
        TimeOnly nightStart)
    {
        var localNow = now.ToLocalTime();
        var currentTime = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        var currentSlot = ResolveCurrentSlot(currentTime, dayStart, nightStart);

        DateTime localTransition;
        DayNightSlot nextSlot;

        if (currentSlot == DayNightSlot.Day)
        {
            nextSlot = DayNightSlot.Night;
            var targetToday = localNow.Date.Add(nightStart.ToTimeSpan());
            localTransition = targetToday > localNow.DateTime
                ? targetToday
                : targetToday.AddDays(1);
        }
        else
        {
            nextSlot = DayNightSlot.Day;
            var targetToday = localNow.Date.Add(dayStart.ToTimeSpan());
            localTransition = targetToday > localNow.DateTime
                ? targetToday
                : targetToday.AddDays(1);
        }

        var transitionOffset = new DateTimeOffset(localTransition, localNow.Offset);
        var transitionUtc = transitionOffset.ToUniversalTime();
        var dueIn = transitionUtc > now ? transitionUtc - now : TimeSpan.Zero;

        return new ScheduleTransitionInfo(
            CurrentSlot: currentSlot,
            NextSlot: nextSlot,
            NextTransitionUtc: transitionUtc,
            DueIn: dueIn
        );
    }
}
