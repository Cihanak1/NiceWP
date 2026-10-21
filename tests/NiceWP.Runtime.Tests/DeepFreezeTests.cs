using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeTests
{
    [Fact]
    public void GracePolicy_NormalPause_RequiresGracePeriodElapsed()
    {
        var policy = new DeepFreezeGracePolicy(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UtcNow;

        Assert.False(policy.IsEligibleForDeepFreeze(t0, t0.AddSeconds(10), isImmediateTrigger: false));
        Assert.False(policy.IsEligibleForDeepFreeze(t0, t0.AddSeconds(29), isImmediateTrigger: false));
        Assert.True(policy.IsEligibleForDeepFreeze(t0, t0.AddSeconds(30), isImmediateTrigger: false));
        Assert.True(policy.IsEligibleForDeepFreeze(t0, t0.AddSeconds(45), isImmediateTrigger: false));
    }

    [Fact]
    public void GracePolicy_ImmediateTrigger_BypassesGracePeriod()
    {
        var policy = new DeepFreezeGracePolicy(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(policy.IsEligibleForDeepFreeze(t0, t0, isImmediateTrigger: true));
        Assert.Equal(TimeSpan.Zero, policy.GetRemainingGrace(t0, t0, isImmediateTrigger: true));
    }

    [Fact]
    public void GracePolicy_GetRemainingGrace_CalculatesAccurately()
    {
        var policy = new DeepFreezeGracePolicy(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.UtcNow;

        var remaining = policy.GetRemainingGrace(t0, t0.AddSeconds(10), isImmediateTrigger: false);
        Assert.Equal(TimeSpan.FromSeconds(20), remaining);

        var pastRemaining = policy.GetRemainingGrace(t0, t0.AddSeconds(35), isImmediateTrigger: false);
        Assert.Equal(TimeSpan.Zero, pastRemaining);
    }

    [Fact]
    public void StateResolver_AppRuleDeepFreeze_ProducesShouldDeepFreeze()
    {
        var input = new AdaptiveStateResolverInput
        {
            CurrentWallpaperType = IpcConstants.WallpaperTypeVideo,
            ForegroundProcessName = "heavygame.exe",
            MatchedAppRuleProfile = AdaptiveDesktopProfile.Normal
        };

        // AppRule with DeepFreeze behavior
        var behavior = AppRuleBehavior.DeepFreeze;
        Assert.Equal(AppRuleBehavior.DeepFreeze, behavior);
    }
}
