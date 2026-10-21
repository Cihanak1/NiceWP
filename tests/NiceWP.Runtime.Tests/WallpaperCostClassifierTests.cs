using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperCostClassifierTests
{
    [Fact]
    public void Classify_ImageWallpapers_AlwaysClassifiedAsLight()
    {
        Assert.Equal(WallpaperCost.Light, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeImage));
        Assert.Equal(WallpaperCost.Light, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeImage, 7680, 4320, 500_000_000));
        Assert.Equal(WallpaperCost.Light, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeNone));
    }

    [Fact]
    public void Classify_StandardVideo_ClassifiedAsModerate()
    {
        Assert.Equal(WallpaperCost.Moderate, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, 1920, 1080, 20_000_000));
        Assert.Equal(WallpaperCost.Moderate, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, 1280, 720, 10_000_000));
    }

    [Fact]
    public void Classify_4KOrHighBitrateVideo_ClassifiedAsHeavy()
    {
        // 4K resolution
        Assert.Equal(WallpaperCost.Heavy, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, 3840, 2160, 50_000_000));

        // 1440p resolution
        Assert.Equal(WallpaperCost.Heavy, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, 2560, 1440, 30_000_000));

        // High file size (> 200MB)
        Assert.Equal(WallpaperCost.Heavy, WallpaperCostClassifier.Classify(IpcConstants.WallpaperTypeVideo, 1920, 1080, 250_000_000));
    }
}
