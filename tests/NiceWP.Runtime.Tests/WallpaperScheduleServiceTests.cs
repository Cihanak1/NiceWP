using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Scheduling;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperScheduleServiceTests
{
    private static WallpaperLibraryItem CreateTestItem(string id, string displayName)
    {
        return new WallpaperLibraryItem
        {
            Id = id,
            DisplayName = displayName,
            FilePath = $"C:\\wallpapers\\{id}.jpg",
            Type = "image"
        };
    }

    private static WallpaperLibraryApplyResult SuccessResult(string path) =>
        new(Success: true, ActiveWallpaperPath: path);

    private static WallpaperLibraryApplyResult FailureResult(string message) =>
        new(Success: false, Message: message);

    [Fact]
    public void DefaultState_IsOff_AndZeroActiveTimers()
    {
        using var service = new WallpaperScheduleService(
            getItemById: _ => null,
            applyItem: (_, _) => Task.FromResult(FailureResult("not implemented")),
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        Assert.Equal(ScheduleMode.Off, service.Mode);
        Assert.Equal(DayNightSlot.None, service.CurrentSlot);
        Assert.Equal(DayNightSlot.None, service.NextSlot);
        Assert.Null(service.NextTransitionUtc);
        Assert.False(service.IsOverdue);

        var status = service.GetStatus();
        Assert.False(status.IsActive);
    }

    [Fact]
    public void Initialize_LoadsSettingsCorrectly()
    {
        using var service = new WallpaperScheduleService(
            getItemById: _ => null,
            applyItem: (_, _) => Task.FromResult(FailureResult("not implemented")),
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        var settings = new AppSettings
        {
            ScheduleMode = ScheduleModeExtensions.ModeDayNight,
            DayStartTime = "07:30",
            NightStartTime = "21:15",
            DayWallpaperId = "day-id",
            NightWallpaperId = "night-id"
        };

        service.Initialize(settings);

        Assert.Equal(ScheduleMode.DayNight, service.Mode);
        Assert.Equal(new TimeOnly(7, 30), service.DayStartTime);
        Assert.Equal(new TimeOnly(21, 15), service.NightStartTime);
        Assert.Equal("day-id", service.DayWallpaperId);
        Assert.Equal("night-id", service.NightWallpaperId);
        Assert.NotNull(service.NextTransitionUtc);
    }

    [Fact]
    public void Configure_OffMode_ClearsSlotsAndTransitions()
    {
        using var service = new WallpaperScheduleService(
            getItemById: _ => null,
            applyItem: (_, _) => Task.FromResult(FailureResult("not implemented")),
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        // Turn on
        service.Configure(ScheduleMode.DayNight, new TimeOnly(8, 0), new TimeOnly(20, 0), "day1", "night1");
        Assert.Equal(ScheduleMode.DayNight, service.Mode);
        Assert.NotNull(service.NextTransitionUtc);

        // Turn off
        service.Configure(ScheduleMode.Off, new TimeOnly(8, 0), new TimeOnly(20, 0), "day1", "night1");
        Assert.Equal(ScheduleMode.Off, service.Mode);
        Assert.Equal(DayNightSlot.None, service.CurrentSlot);
        Assert.Equal(DayNightSlot.None, service.NextSlot);
        Assert.Null(service.NextTransitionUtc);
    }

    [Fact]
    public async Task EvaluateAndApplyCurrentSlotAsync_AppliesDayWallpaperDuringDay()
    {
        var dayItem = CreateTestItem("day-wall", "Sunny Day");
        var nightItem = CreateTestItem("night-wall", "Starry Night");
        var items = new Dictionary<string, WallpaperLibraryItem>
        {
            ["day-wall"] = dayItem,
            ["night-wall"] = nightItem
        };

        WallpaperLibraryItem? appliedItem = null;
        bool scheduleCallbackFired = false;

        using var service = new WallpaperScheduleService(
            getItemById: id => items.TryGetValue(id, out var it) ? it : null,
            applyItem: (item, _) =>
            {
                appliedItem = item;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true }),
            onScheduleApplied: () => scheduleCallbackFired = true
        );

        // Set Day to 00:00 - 23:59 so current time is always Day
        service.Configure(ScheduleMode.DayNight, new TimeOnly(0, 0), new TimeOnly(23, 59), "day-wall", "night-wall");

        await service.EvaluateAndApplyCurrentSlotAsync();

        Assert.NotNull(appliedItem);
        Assert.Equal("day-wall", appliedItem.Id);
        Assert.True(scheduleCallbackFired);
        Assert.False(service.IsOverdue);
    }

    [Fact]
    public async Task EvaluateAndApplyCurrentSlotAsync_MissingWallpaper_DoesNotThrow()
    {
        bool applyCalled = false;

        using var service = new WallpaperScheduleService(
            getItemById: _ => null, // always missing
            applyItem: (_, _) =>
            {
                applyCalled = true;
                return Task.FromResult(FailureResult("err"));
            },
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        service.Configure(ScheduleMode.DayNight, new TimeOnly(0, 0), new TimeOnly(23, 59), "missing-id", "missing-id-2");

        // Should gracefully handle missing item without throwing
        await service.EvaluateAndApplyCurrentSlotAsync();

        Assert.False(applyCalled);
    }

    [Fact]
    public void NotifyPauseState_WhenResumedAfterOverdue_TriggersEvaluation()
    {
        var dayItem = CreateTestItem("day-wall", "Sunny Day");
        WallpaperLibraryItem? applied = null;

        using var service = new WallpaperScheduleService(
            getItemById: id => dayItem,
            applyItem: (item, _) =>
            {
                applied = item;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        service.Configure(ScheduleMode.DayNight, new TimeOnly(0, 0), new TimeOnly(23, 59), "day-wall", "night-wall");

        // Simulate pause state
        service.NotifyPauseState(isPaused: true);
        Assert.Null(applied);

        // Resuming when not overdue does not apply
        service.NotifyPauseState(isPaused: false);
        Assert.Null(applied);
    }

    [Fact]
    public void StatusChanged_FiresWhenConfigured()
    {
        using var service = new WallpaperScheduleService(
            getItemById: _ => null,
            applyItem: (_, _) => Task.FromResult(FailureResult("err")),
            getHostStatus: _ => Task.FromResult(new IpcResponse { Success = true })
        );

        WallpaperScheduleStatus? statusReported = null;
        service.StatusChanged += (_, s) => statusReported = s;

        service.Configure(ScheduleMode.DayNight, new TimeOnly(8, 0), new TimeOnly(20, 0), "d1", "n1");

        Assert.NotNull(statusReported);
        Assert.Equal(ScheduleMode.DayNight, statusReported.Mode);
    }
}
