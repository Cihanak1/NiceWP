using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class HighResourceBudgetPolicyTests
{
    [Fact]
    public void HighBudget_ProvidesExtendedGraceAndRapidPolling()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), ResourceBudgetPolicy.GetGracePeriod(ResourceBudget.High));
        Assert.Equal(1000, ResourceBudgetPolicy.GetBackgroundPollIntervalMs(ResourceBudget.High));
    }

    [Fact]
    public void HighBudget_NeverPausesOnFocusLossOrBackground()
    {
        Assert.False(ResourceBudgetPolicy.ShouldPauseOnFocusLoss(ResourceBudget.High, IpcConstants.WallpaperTypeVideo));
        Assert.False(ResourceBudgetPolicy.ShouldDeepFreezeOnBackground(ResourceBudget.High, IpcConstants.WallpaperTypeVideo));
    }

    [Fact]
    public void HighBudget_OverridesResourceGuardSustainedPressure()
    {
        var input = new AdaptiveStateResolverInput
        {
            ConfiguredBudget = ResourceBudget.High,
            IsResourcePressureSustained = true,
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(ResourceBudget.High, state.ResourceBudget);
        Assert.False(state.ShouldPause); // Sustained pressure does not pause under High budget
        Assert.False(state.ShouldDeepFreeze);
    }
}
