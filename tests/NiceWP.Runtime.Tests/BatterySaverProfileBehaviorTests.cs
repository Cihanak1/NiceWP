using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class BatterySaverProfileBehaviorTests
{
    [Fact]
    public void ShouldImmediatelyDeepFreeze_ForVideoOnBatterySaver_ReturnsTrue()
    {
        Assert.True(BatterySaverProfileBehavior.ShouldImmediatelyDeepFreeze(true, IpcConstants.WallpaperTypeVideo));
        Assert.False(BatterySaverProfileBehavior.ShouldImmediatelyDeepFreeze(true, IpcConstants.WallpaperTypeImage));
        Assert.False(BatterySaverProfileBehavior.ShouldImmediatelyDeepFreeze(false, IpcConstants.WallpaperTypeVideo));
    }

    [Fact]
    public void GetRecommendedTimerIntervalMs_ScalesWithPowerMode()
    {
        Assert.Equal(5000, BatterySaverProfileBehavior.GetRecommendedTimerIntervalMs(true, true));
        Assert.Equal(2000, BatterySaverProfileBehavior.GetRecommendedTimerIntervalMs(false, true));
        Assert.Equal(1000, BatterySaverProfileBehavior.GetRecommendedTimerIntervalMs(false, false));
    }

    [Fact]
    public void Resolve_WhenBatterySaverActive_ResolvesLowBudgetAndDeepFreezesVideo()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsOnBattery = true,
            IsBatterySaverActive = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.BatterySaver, state.EffectiveProfile);
        Assert.Equal(ResourceBudget.Low, state.ResourceBudget);
        Assert.True(state.ShouldPause);
        Assert.True(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
    }
}
