using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourceGuardTests
{
    [Fact]
    public void Resolve_VideoUnderSustainedPressure_EngagesResourceGuardAndDeepFreezes()
    {
        var input = new AdaptiveStateResolverInput
        {
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo,
            IsResourcePressureSustained = true,
            ConfiguredBudget = ResourceBudget.Automatic
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.True(state.ShouldPause);
        Assert.True(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Resource Guard", state.Reason);
    }

    [Fact]
    public void Resolve_ImageUnderSustainedPressure_DoesNotEngageResourceGuard()
    {
        var input = new AdaptiveStateResolverInput
        {
            CurrentWallpaperType = IpcConstants.WallpaperTypeImage,
            IsResourcePressureSustained = true,
            ConfiguredBudget = ResourceBudget.Automatic
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.False(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.True(state.AllowRotation);
        Assert.DoesNotContain("Resource Guard", state.Reason);
    }

    [Fact]
    public void Resolve_HighResourceBudget_BypassesResourceGuard()
    {
        var input = new AdaptiveStateResolverInput
        {
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo,
            IsResourcePressureSustained = true,
            ConfiguredBudget = ResourceBudget.High
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(ResourceBudget.High, state.ResourceBudget);
        Assert.False(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.DoesNotContain("Resource Guard", state.Reason);
    }
}
