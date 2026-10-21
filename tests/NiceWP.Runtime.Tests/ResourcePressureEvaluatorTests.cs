using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourcePressureEvaluatorTests
{
    [Fact]
    public void Constructor_ValidatesThresholdHierarchy()
    {
        Assert.Throws<ArgumentException>(() => new ResourcePressureEvaluator(50.0, 80.0));
        Assert.Throws<ArgumentException>(() => new ResourcePressureEvaluator(60.0, 60.0));
    }

    [Theory]
    [InlineData(85.0, true)]
    [InlineData(80.0, true)]
    [InlineData(79.9, false)]
    [InlineData(40.0, false)]
    public void IsInstantaneousPressureExceeded_EvaluatesAgainstHighThreshold(double cpu, bool expected)
    {
        var evaluator = new ResourcePressureEvaluator(80.0, 50.0);
        Assert.Equal(expected, evaluator.IsInstantaneousPressureExceeded(cpu));
    }

    [Theory]
    [InlineData(45.0, true)]
    [InlineData(50.0, true)]
    [InlineData(50.1, false)]
    [InlineData(75.0, false)]
    public void IsInstantaneousReleaseAchieved_EvaluatesAgainstReleaseThreshold(double cpu, bool expected)
    {
        var evaluator = new ResourcePressureEvaluator(80.0, 50.0);
        Assert.Equal(expected, evaluator.IsInstantaneousReleaseAchieved(cpu));
    }
}
