using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class AutomaticResourceBudgetPolicyTests
{
    [Fact]
    public void ResolveAutomaticBudget_UnderStandardAcPower_ReturnsBalanced()
    {
        var budget = ResourceBudgetPolicy.ResolveAutomaticBudget(
            isOnBattery: false,
            isBatterySaver: false,
            isPressureSustained: false);

        Assert.Equal(ResourceBudget.Balanced, budget);
    }

    [Fact]
    public void ResolveAutomaticBudget_UnderBatterySaver_ReturnsLow()
    {
        var budget = ResourceBudgetPolicy.ResolveAutomaticBudget(
            isOnBattery: true,
            isBatterySaver: true,
            isPressureSustained: false);

        Assert.Equal(ResourceBudget.Low, budget);
    }

    [Fact]
    public void ResolveAutomaticBudget_UnderSustainedPressure_ReturnsLow()
    {
        var budget = ResourceBudgetPolicy.ResolveAutomaticBudget(
            isOnBattery: false,
            isBatterySaver: false,
            isPressureSustained: true);

        Assert.Equal(ResourceBudget.Low, budget);
    }
}
