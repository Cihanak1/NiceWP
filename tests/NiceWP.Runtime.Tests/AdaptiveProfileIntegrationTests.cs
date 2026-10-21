using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class AdaptiveProfileIntegrationTests
{
    [Fact]
    public void Resolve_DefaultInput_ProducesNormalProfileWithPlaybackAllowed()
    {
        var input = new AdaptiveStateResolverInput();
        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Normal, state.EffectiveProfile);
        Assert.False(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.True(state.AllowRotation);
        Assert.True(state.AllowScheduleApply);
        Assert.Equal(ResourceBudget.Balanced, state.ResourceBudget);
    }

    [Fact]
    public void Resolve_PresentationProfile_InhibitsRotationAndSetsPreferredWallpaper()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "powerpnt.exe",
            PresentationWallpaperId = "pres-123"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Presentation, state.EffectiveProfile);
        Assert.False(state.ShouldPause);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Equal("pres-123", state.PreferredWallpaperId);
    }

    [Fact]
    public void Resolve_GamingProfile_DisablesRotationAndSchedule()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsFullscreenActive = true,
            ForegroundProcessName = "steam.exe"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Gaming, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
    }

    [Fact]
    public void Resolve_BatterySaverProfile_PausesAndAppliesLowBudget()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsOnBattery = true,
            IsBatterySaverActive = true
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.BatterySaver, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.Equal(ResourceBudget.Low, state.ResourceBudget);
    }

    [Fact]
    public void Resolve_CinemaProfile_ActiveForMediaPlayers()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "vlc.exe"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Cinema, state.EffectiveProfile);
        Assert.False(state.AllowRotation);
    }

    [Fact]
    public void Resolve_FocusProfile_ActiveForDevelopmentTools()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "devenv.exe"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Focus, state.EffectiveProfile);
        Assert.False(state.AllowRotation);
    }
}
