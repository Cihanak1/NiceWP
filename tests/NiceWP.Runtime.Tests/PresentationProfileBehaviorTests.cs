using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class PresentationProfileBehaviorTests
{
    [Theory]
    [InlineData("powerpnt.exe", true)]
    [InlineData("POWERPNT", true)]
    [InlineData("teams.exe", true)]
    [InlineData("zoom.exe", true)]
    [InlineData("webex.exe", true)]
    [InlineData("obs64.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPresentationProcess_DetectsKnownMeetingAndPresentationApps(string? processName, bool expected)
    {
        Assert.Equal(expected, PresentationProfileBehavior.IsPresentationProcess(processName));
    }

    [Fact]
    public void Resolve_WhenPresentationProcessInForeground_ResolvesPresentationProfileAndSuppressesMotion()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "powerpnt.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo,
            PresentationWallpaperId = "clean-office-wall"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Presentation, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Equal("clean-office-wall", state.PreferredWallpaperId);
    }

    [Fact]
    public void Resolve_WhenImageWallpaperDuringPresentation_DoesNotPauseVisual()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "teams.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeImage,
            PresentationWallpaperId = "clean-wall"
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Presentation, state.EffectiveProfile);
        Assert.False(state.ShouldPause); // Image stays rendered
        Assert.False(state.AllowRotation);
    }
}
