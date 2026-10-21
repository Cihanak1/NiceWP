using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourceGuardHysteresisTests
{
    [Fact]
    public void RecordSample_Hysteresis_PreventsRapidOscillation()
    {
        var coordinator = new ResourceGuardCoordinator(
            sustainedWindow: TimeSpan.FromSeconds(15),
            recoveryWindow: TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UtcNow;

        // 1. High load for 15 seconds -> Resource Guard Engaged
        coordinator.RecordSample(85.0, t0);
        Assert.False(coordinator.IsActive);
        coordinator.RecordSample(85.0, t0.AddSeconds(15));
        Assert.True(coordinator.IsActive);

        // 2. Load drops to 40% for 5 seconds (< 10s recovery window) -> Still active!
        coordinator.RecordSample(40.0, t0.AddSeconds(16));
        coordinator.RecordSample(40.0, t0.AddSeconds(20));
        Assert.True(coordinator.IsActive);

        // 3. Load spikes to 65% at second 21 -> resets recovery timer
        coordinator.RecordSample(65.0, t0.AddSeconds(21));
        Assert.True(coordinator.IsActive);
        Assert.Null(coordinator.ReleaseStartedUtc);

        // 4. Load drops to 30% for full 10 consecutive seconds (22 to 32) -> Safely disengages
        coordinator.RecordSample(30.0, t0.AddSeconds(22));
        Assert.True(coordinator.IsActive);
        coordinator.RecordSample(30.0, t0.AddSeconds(31));
        Assert.True(coordinator.IsActive);

        coordinator.RecordSample(30.0, t0.AddSeconds(32));
        Assert.False(coordinator.IsActive); // Released!
    }
}
