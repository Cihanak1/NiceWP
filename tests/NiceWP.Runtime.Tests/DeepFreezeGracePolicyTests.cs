using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeGracePolicyTests
{
    [Fact]
    public void IsEligibleForDeepFreeze_WhenImmediateTrigger_ReturnsTrueWithoutDelay()
    {
        var policy = new DeepFreezeGracePolicy(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        var eligible = policy.IsEligibleForDeepFreeze(now, now, isImmediateTrigger: true);
        var remaining = policy.GetRemainingGrace(now, now, isImmediateTrigger: true);

        Assert.True(eligible);
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void IsEligibleForDeepFreeze_WhenDeferredTrigger_HonorsGraceWindow()
    {
        var policy = new DeepFreezeGracePolicy(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.UtcNow;

        // 10 seconds in: not eligible, 20s remaining
        var t10 = start.AddSeconds(10);
        Assert.False(policy.IsEligibleForDeepFreeze(start, t10, isImmediateTrigger: false));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.GetRemainingGrace(start, t10, isImmediateTrigger: false));

        // 29 seconds in: not eligible, 1s remaining
        var t29 = start.AddSeconds(29);
        Assert.False(policy.IsEligibleForDeepFreeze(start, t29, isImmediateTrigger: false));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetRemainingGrace(start, t29, isImmediateTrigger: false));

        // 30 seconds in: eligible, 0s remaining
        var t30 = start.AddSeconds(30);
        Assert.True(policy.IsEligibleForDeepFreeze(start, t30, isImmediateTrigger: false));
        Assert.Equal(TimeSpan.Zero, policy.GetRemainingGrace(start, t30, isImmediateTrigger: false));

        // 45 seconds in: still eligible, 0s remaining
        var t45 = start.AddSeconds(45);
        Assert.True(policy.IsEligibleForDeepFreeze(start, t45, isImmediateTrigger: false));
        Assert.Equal(TimeSpan.Zero, policy.GetRemainingGrace(start, t45, isImmediateTrigger: false));
    }
}
