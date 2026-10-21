using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class AdaptiveResourceSavingsTests
{
    [Fact]
    public void WallpaperCostClassifier_AccuratelyClassifiesMedia()
    {
        // Image wallpaper
        Assert.Equal(WallpaperCost.Light, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeImage, fileSizeBytes: 1024 * 1024));
        Assert.Equal(WallpaperCost.Moderate, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeImage, fileSizeBytes: 20 * 1024 * 1024));

        // Video wallpaper
        Assert.Equal(WallpaperCost.Moderate, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, fileSizeBytes: 15 * 1024 * 1024));
        Assert.Equal(WallpaperCost.Heavy, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, fileSizeBytes: 120 * 1024 * 1024));

        // None
        Assert.Equal(WallpaperCost.Light, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeNone));
    }

    [Fact]
    public void ResourceBudgetPolicy_GracePeriodsAndIntervals_ReflectSavingsTiers()
    {
        // Low budget: aggressive 10s grace, 5000ms polling
        Assert.Equal(TimeSpan.FromSeconds(10), ResourceBudgetPolicy.GetGracePeriod(ResourceBudget.Low));
        Assert.Equal(5000, ResourceBudgetPolicy.GetBackgroundPollIntervalMs(ResourceBudget.Low));

        // Balanced budget: 30s grace, 2000ms polling
        Assert.Equal(TimeSpan.FromSeconds(30), ResourceBudgetPolicy.GetGracePeriod(ResourceBudget.Balanced));
        Assert.Equal(2000, ResourceBudgetPolicy.GetBackgroundPollIntervalMs(ResourceBudget.Balanced));

        // High budget: 120s grace, 1000ms polling
        Assert.Equal(TimeSpan.FromMinutes(2), ResourceBudgetPolicy.GetGracePeriod(ResourceBudget.High));
        Assert.Equal(1000, ResourceBudgetPolicy.GetBackgroundPollIntervalMs(ResourceBudget.High));
    }

    [Fact]
    public void RuntimeAuditTrail_RingBuffer_BoundedMemoryAndChronologicalOrder()
    {
        var audit = new RuntimeAuditTrail(capacity: 5);

        for (int i = 0; i < 10; i++)
        {
            audit.Record("TestEvent", $"Message {i}");
        }

        Assert.Equal(5, audit.Count);

        var recent = audit.GetRecentEvents();
        Assert.Equal(5, recent.Count);
        Assert.Equal("Message 5", recent[0].Message);
        Assert.Equal("Message 9", recent[4].Message);
    }
}
