using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperLibraryCostExposureTests
{
    [Fact]
    public void WallpaperLibraryEntryView_CostLabel_LocalizesProperly()
    {
        var lightView = new WallpaperLibraryEntryView(
            Id: "img1",
            DisplayName: "Nature",
            Type: IpcConstants.WallpaperTypeImage,
            FilePath: @"C:\nature.png",
            AddedAtUtc: DateTimeOffset.UtcNow,
            IsMissing: false,
            Cost: WallpaperCost.Light);

        Assert.Equal("Light", lightView.CostLabel);
        Assert.Equal("Light", lightView.GetLocalizedCostLabel(isTurkish: false));
        Assert.Equal("Hafif", lightView.GetLocalizedCostLabel(isTurkish: true));

        var heavyView = lightView with { Cost = WallpaperCost.Heavy };
        Assert.Equal("Heavy", heavyView.CostLabel);
        Assert.Equal("Heavy", heavyView.GetLocalizedCostLabel(isTurkish: false));
        Assert.Equal("Ağır", heavyView.GetLocalizedCostLabel(isTurkish: true));
    }

    [Fact]
    public void FromItem_AssignsCostTierAccurately()
    {
        var imageItem = new WallpaperLibraryItem
        {
            Id = "item-img",
            Type = IpcConstants.WallpaperTypeImage,
            FilePath = @"C:\wall.jpg",
            DisplayName = "Wall"
        };

        var entry = WallpaperLibraryEntryView.FromItem(imageItem);
        Assert.Equal(WallpaperCost.Light, entry.Cost);
    }
}
