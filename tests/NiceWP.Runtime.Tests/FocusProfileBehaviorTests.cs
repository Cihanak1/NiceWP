using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class FocusProfileBehaviorTests
{
    [Theory]
    [InlineData("devenv.exe", true)]
    [InlineData("code.exe", true)]
    [InlineData("DEVENV", true)]
    [InlineData("rider64.exe", true)]
    [InlineData("obsidian.exe", true)]
    [InlineData("notion.exe", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFocusProcess_DetectsProductivityAndDevelopmentApps(string? processName, bool expected)
    {
        Assert.Equal(expected, FocusProfileBehavior.IsFocusProcess(processName));
    }

    [Fact]
    public void Resolve_WhenFocusAppActiveAndVideoPlaying_PausesMotionAndPostponesRotation()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "devenv.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Focus, state.EffectiveProfile);
        Assert.True(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.True(state.AllowScheduleApply);
        Assert.Contains("Focus application active", state.Reason);
    }

    [Fact]
    public void Resolve_WhenFocusAppActiveAndImageWallpaper_PreservesVisualRendering()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "code.exe",
            CurrentWallpaperType = IpcConstants.WallpaperTypeImage
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Focus, state.EffectiveProfile);
        Assert.False(state.ShouldPause); // Static image remains displayed
        Assert.False(state.AllowRotation);
    }
}
