using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourceGuardVideoActivationTests
{
    [Fact]
    public void Resolve_WhenResourceGuardActive_PausesAndDeepFreezesVideo()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsResourcePressureSustained = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.True(state.ShouldPause);
        Assert.True(state.ShouldDeepFreeze);
        Assert.False(state.AllowRotation);
        Assert.False(state.AllowScheduleApply);
        Assert.Contains("Resource Guard active", state.Reason);
    }

    [Fact]
    public void Resolve_WhenResourceGuardActive_PreservesStaticImage()
    {
        var input = new AdaptiveStateResolverInput
        {
            IsResourcePressureSustained = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeImage
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.False(state.ShouldPause);
        Assert.False(state.ShouldDeepFreeze);
        Assert.True(state.AllowRotation);
    }

    [Fact]
    public void Reconciler_WhenPressureEnds_RestoresPlaybackCleanly()
    {
        var reconciler = new AdaptiveContextReconciler(new AdaptiveStateResolverInput
        {
            IsResourcePressureSustained = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        });

        Assert.True(reconciler.CurrentState.ShouldPause);
        Assert.True(reconciler.CurrentState.ShouldDeepFreeze);

        // Pressure subsides
        var recovered = reconciler.Reconcile(current => current with { IsResourcePressureSustained = false });

        Assert.False(recovered.ShouldPause);
        Assert.False(recovered.ShouldDeepFreeze);
        Assert.True(recovered.AllowRotation);
    }
}
