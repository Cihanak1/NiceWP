using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class AppRulePrecedenceTests
{
    [Fact]
    public void AppRule_OverridesAutomaticProfileDetection()
    {
        var input = new AdaptiveStateResolverInput
        {
            ForegroundProcessName = "powerpnt.exe", // Normally Presentation
            MatchedAppRuleProfile = AdaptiveDesktopProfile.Focus // User overridden to Focus
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Focus, state.EffectiveProfile);
    }

    [Fact]
    public void ManualProfile_OverridesAppRuleMatch()
    {
        var input = new AdaptiveStateResolverInput
        {
            ConfiguredProfile = AdaptiveDesktopProfile.Gaming, // Explicit user override
            ForegroundProcessName = "code.exe",
            MatchedAppRuleProfile = AdaptiveDesktopProfile.Focus
        };

        var state = AdaptiveDesktopStateResolver.Resolve(input);

        Assert.Equal(AdaptiveDesktopProfile.Gaming, state.EffectiveProfile);
    }

    [Fact]
    public void AppRuleMatchingService_NormalizesProcessNamesAndMatches()
    {
        var service = new AppRuleMatchingService(new[]
        {
            new AppRule { ProcessName = "DEVENV.EXE", Behavior = AppRuleBehavior.Focus },
            new AppRule { ProcessName = "spotify", Behavior = AppRuleBehavior.Cinema }
        });

        var match1 = service.FindMatch("devenv.exe");
        Assert.NotNull(match1);
        Assert.Equal(AppRuleBehavior.Focus, match1.Behavior);

        var match2 = service.FindMatch("SPOTIFY.EXE");
        Assert.NotNull(match2);
        Assert.Equal(AppRuleBehavior.Cinema, match2.Behavior);

        var match3 = service.FindMatch("notepad.exe");
        Assert.Null(match3);
    }
}
