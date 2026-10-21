using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class GamingProfileBehaviorTests
{
    [Theory]
    [InlineData("cs2.exe", true)]
    [InlineData("CS2", true)]
    [InlineData("valorant.exe", true)]
    [InlineData("cyberpunk2077.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGamingProcess_IdentifiesGamingBinaries(string? processName, bool expected)
    {
        Assert.Equal(expected, GamingProfileBehavior.IsGamingProcess(processName));
    }

    [Fact]
    public void Resolve_WhenGamingProcessInForeground_ImmediatelyPausesVideoAndSuppressesRotation()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "cs2.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Gaming, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze); // Deep freeze awaits grace period
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Gaming application active", state.Reason);
    }

    [Fact]
    public void Resolve_WhenFullscreenActiveWithoutSpecificGameName_ResolvesGamingProfile()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsFullscreenActive = true,
            ForegroundProcessName = "customgame.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Gaming, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
    }
}
