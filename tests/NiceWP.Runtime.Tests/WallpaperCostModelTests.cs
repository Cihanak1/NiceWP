using NiceWP.Core;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperCostModelTests
{
    [Fact]
    public void WallpaperCost_ValuesAndDisplayStrings_MatchExpectations()
    {
        Assert.Equal("Light", WallpaperCost.Light.ToDisplayString());
        Assert.Equal("Moderate", WallpaperCost.Moderate.ToDisplayString());
        Assert.Equal("Heavy", WallpaperCost.Heavy.ToDisplayString());
    }
}
