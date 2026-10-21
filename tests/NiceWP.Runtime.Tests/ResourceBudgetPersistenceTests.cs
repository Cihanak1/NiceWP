using NiceWP.Core;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourceBudgetPersistenceTests : IDisposable
{
    private readonly string tempSettingsPath;

    public ResourceBudgetPersistenceTests()
    {
        tempSettingsPath = Path.Combine(Path.GetTempPath(), $"nice_budget_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath);
        }
        catch { }
    }

    [Theory]
    [InlineData("Low", "Low")]
    [InlineData("low", "Low")]
    [InlineData("Balanced", "Balanced")]
    [InlineData("High", "High")]
    [InlineData("Automatic", "Automatic")]
    [InlineData("InvalidString", "Automatic")]
    [InlineData("", "Automatic")]
    [InlineData(null, "Automatic")]
    public void NormalizeResourceBudget_ReturnsExpectedValue(string? input, string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeResourceBudget(input));
    }

    [Fact]
    public async Task SaveAndLoad_PersistsResourceBudget()
    {
        var service = new SettingsService(tempSettingsPath);
        var settings = new AppSettings
        {
            ResourceBudget = "Low"
        };

        var saved = await service.SaveAsync(settings);
        Assert.True(saved);

        var loaded = await service.LoadAsync();
        Assert.Equal("Low", loaded.ResourceBudget);
        Assert.Equal(ResourceBudget.Low, loaded.EffectiveResourceBudget);
    }
}
