using NiceWP.Core;
using NiceWP.Infrastructure.Scheduling;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DayNightScheduleResolverTests
{
    [Fact]
    public void ResolveCurrentSlot_EqualDayAndNight_ReturnsDay()
    {
        var time = new TimeOnly(12, 0);
        var slot = DayNightScheduleResolver.ResolveCurrentSlot(time, new TimeOnly(8, 0), new TimeOnly(8, 0));
        Assert.Equal(DayNightSlot.Day, slot);
    }

    [Theory]
    [InlineData(7, 59, DayNightSlot.Night)]
    [InlineData(8, 0, DayNightSlot.Day)]
    [InlineData(12, 0, DayNightSlot.Day)]
    [InlineData(19, 59, DayNightSlot.Day)]
    [InlineData(20, 0, DayNightSlot.Night)]
    [InlineData(23, 59, DayNightSlot.Night)]
    [InlineData(0, 0, DayNightSlot.Night)]
    public void ResolveCurrentSlot_StandardSchedule_ResolvesCorrectly(int hour, int minute, DayNightSlot expected)
    {
        // Day starts 08:00, Night starts 20:00
        var current = new TimeOnly(hour, minute);
        var dayStart = new TimeOnly(8, 0);
        var nightStart = new TimeOnly(20, 0);

        var result = DayNightScheduleResolver.ResolveCurrentSlot(current, dayStart, nightStart);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(21, 59, DayNightSlot.Night)]
    [InlineData(22, 0, DayNightSlot.Day)]
    [InlineData(23, 30, DayNightSlot.Day)]
    [InlineData(0, 0, DayNightSlot.Day)]
    [InlineData(5, 59, DayNightSlot.Day)]
    [InlineData(6, 0, DayNightSlot.Night)]
    [InlineData(12, 0, DayNightSlot.Night)]
    public void ResolveCurrentSlot_MidnightWrapSchedule_ResolvesCorrectly(int hour, int minute, DayNightSlot expected)
    {
        // Day starts 22:00, Night starts 06:00
        var current = new TimeOnly(hour, minute);
        var dayStart = new TimeOnly(22, 0);
        var nightStart = new TimeOnly(6, 0);

        var result = DayNightScheduleResolver.ResolveCurrentSlot(current, dayStart, nightStart);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateNextTransition_DuringDay_SchedulesNightTransitionToday()
    {
        // 14:00 local time, Day: 08:00, Night: 20:00
        var now = new DateTimeOffset(2026, 10, 21, 14, 0, 0, TimeSpan.FromHours(3));
        var dayStart = new TimeOnly(8, 0);
        var nightStart = new TimeOnly(20, 0);

        var info = DayNightScheduleResolver.CalculateNextTransition(now, dayStart, nightStart);

        Assert.Equal(DayNightSlot.Day, info.CurrentSlot);
        Assert.Equal(DayNightSlot.Night, info.NextSlot);
        Assert.Equal(TimeSpan.FromHours(6), info.DueIn);

        var localTransition = info.NextTransitionUtc.ToLocalTime();
        Assert.Equal(20, localTransition.Hour);
        Assert.Equal(0, localTransition.Minute);
        Assert.Equal(21, localTransition.Day);
    }

    [Fact]
    public void CalculateNextTransition_DuringNight_SchedulesDayTransitionTomorrow()
    {
        // 22:00 local time, Day: 08:00, Night: 20:00
        var now = new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.FromHours(3));
        var dayStart = new TimeOnly(8, 0);
        var nightStart = new TimeOnly(20, 0);

        var info = DayNightScheduleResolver.CalculateNextTransition(now, dayStart, nightStart);

        Assert.Equal(DayNightSlot.Night, info.CurrentSlot);
        Assert.Equal(DayNightSlot.Day, info.NextSlot);
        Assert.Equal(TimeSpan.FromHours(10), info.DueIn);

        var localTransition = info.NextTransitionUtc.ToLocalTime();
        Assert.Equal(8, localTransition.Hour);
        Assert.Equal(0, localTransition.Minute);
        Assert.Equal(22, localTransition.Day); // next day
    }

    [Fact]
    public void CalculateNextTransition_MidnightWrap_SchedulesCorrectly()
    {
        // Day starts 22:00, Night starts 06:00
        // Currently 23:00 -> Current is Day, next is Night at 06:00 tomorrow
        var now = new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.FromHours(3));
        var dayStart = new TimeOnly(22, 0);
        var nightStart = new TimeOnly(6, 0);

        var info = DayNightScheduleResolver.CalculateNextTransition(now, dayStart, nightStart);

        Assert.Equal(DayNightSlot.Day, info.CurrentSlot);
        Assert.Equal(DayNightSlot.Night, info.NextSlot);
        Assert.Equal(TimeSpan.FromHours(7), info.DueIn);

        var localTransition = info.NextTransitionUtc.ToLocalTime();
        Assert.Equal(6, localTransition.Hour);
        Assert.Equal(22, localTransition.Day);
    }
}
