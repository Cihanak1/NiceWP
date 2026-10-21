using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class SustainedResourcePressureTests
{
    [Fact]
    public void RecordSample_TransientSpike_DoesNotTriggerSustainedPressure()
    {
        var tracker = new SustainedPressureTracker(sustainedWindow: TimeSpan.FromSeconds(15));
        var t0 = DateTimeOffset.UtcNow;

        // 95% CPU for 3 seconds
        tracker.RecordSample(95.0, t0);
        tracker.RecordSample(95.0, t0.AddSeconds(2));
        var result = tracker.RecordSample(95.0, t0.AddSeconds(3));

        Assert.False(result);
        Assert.False(tracker.IsPressureSustained);

        // Drops back to 40%
        tracker.RecordSample(40.0, t0.AddSeconds(4));
        Assert.False(tracker.IsPressureSustained);
        Assert.Null(tracker.PressureStartedUtc);
    }

    [Fact]
    public void RecordSample_SustainedElevatedLoad_ConfirmsPressureAfterWindow()
    {
        var tracker = new SustainedPressureTracker(sustainedWindow: TimeSpan.FromSeconds(15));
        var t0 = DateTimeOffset.UtcNow;

        // 85% CPU over 16 seconds
        Assert.False(tracker.RecordSample(85.0, t0));
        Assert.False(tracker.RecordSample(85.0, t0.AddSeconds(5)));
        Assert.False(tracker.RecordSample(85.0, t0.AddSeconds(14)));

        // At 15 seconds: sustained pressure confirmed
        var confirmed = tracker.RecordSample(85.0, t0.AddSeconds(15));
        Assert.True(confirmed);
        Assert.True(tracker.IsPressureSustained);

        // Still confirmed at 20 seconds
        Assert.True(tracker.RecordSample(85.0, t0.AddSeconds(20)));
    }
}
