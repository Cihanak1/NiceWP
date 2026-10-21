using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class CinemaProfileBehaviorTests
{
    [Theory]
    [InlineData("vlc.exe", true)]
    [InlineData("VLC", true)]
    [InlineData("mpc-hc64.exe", true)]
    [InlineData("potplayermini64.exe", true)]
    [InlineData("netflix.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCinemaProcess_IdentifiesMediaPlayers(string? processName, bool expected)
    {
        Assert.Equal(expected, CinemaProfileBehavior.IsCinemaProcess(processName));
    }

    [Fact]
    public void Resolve_WhenCinemaAppInForeground_PausesVideoWallpaperAndSuppressesRotation()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "vlc.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Cinema, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Cinema application active", state.Reason);
    }
}
