using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class EnvironmentalContextTests
{
    [Fact]
    public void DisplayOff_OverridesAllOtherConditions()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsDisplayOff = true,
            IsFullscreenActive = true,
            ForegroundProcessName = "powerpnt.exe",
            IsOnBattery = true,
            IsBatterySaverActive = true,
            IsResourcePressureSustained = true
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.True(state.ShouldPause);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Display is turned off", state.Reason);
    }

    [Fact]
    public void SessionLocked_OverridesManualPauseAndProfiles()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsSessionLocked = true,
            IsManualPaused = true,
            ForegroundProcessName = "powerpnt.exe",
            IsFullscreenActive = true
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.True(state.ShouldPause);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Session is locked", state.Reason);
    }

    [Fact]
    public void ManualPause_TakesPrecedenceOverActiveProfiles()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsManualPaused = true,
            ForegroundProcessName = "powerpnt.exe"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.True(state.ShouldPause);
        Assert.Equal(AdaptiveDesktopProfile.Presentation, state.EffectiveProfile);
        Assert.Contains("Manually paused", state.Reason);
    }

    [Fact]
    public void PresentationProcess_TakesPrecedenceOverGamingFullscreen()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsFullscreenActive = true,
            ForegroundProcessName = "powerpnt.exe"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Presentation, state.EffectiveProfile);
        Assert.False(state.ShouldPause);
    }

    [Fact]
    public void BatterySaver_ForcesLowBudgetAndPause()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsOnBattery = true,
            IsBatterySaverActive = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(ResourceBudget.Low, state.ResourceBudget);
        Assert.True(state.ShouldPause);
    }
}
