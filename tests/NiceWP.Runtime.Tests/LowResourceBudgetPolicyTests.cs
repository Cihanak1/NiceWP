using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class LowResourceBudgetPolicyTests
{
    [Fact]
    public void LowBudget_ShortensGracePeriodAndLengthensPollInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ResourceBudgetPolicy.GetGracePeriod(ResourceBudget.Low));
        Assert.Equal(5000, ResourceBudgetPolicy.GetBackgroundPollIntervalMs(ResourceBudget.Low));
    }

    [Fact]
    public void LowBudget_PausesAndDeepFreezesVideoOnBackground()
    {
        Assert.True(ResourceBudgetPolicy.ShouldPauseOnFocusLoss(ResourceBudget.Low, IpcConstants.WallpaperTypeVideo));
        Assert.False(ResourceBudgetPolicy.ShouldPauseOnFocusLoss(ResourceBudget.Low, IpcConstants.WallpaperTypeImage));
        Assert.True(ResourceBudgetPolicy.ShouldDeepFreezeOnBackground(ResourceBudget.Low, IpcConstants.WallpaperTypeVideo));
    }
}
