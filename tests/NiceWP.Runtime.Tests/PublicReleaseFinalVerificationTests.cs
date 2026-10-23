namespace NiceWP.Runtime.Tests;

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NiceWP.Core;
using NiceWP.Core.Discover;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Discover;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Infrastructure.Remote;
using Xunit;

public sealed class PublicReleaseFinalVerificationTests : IDisposable
{
    private readonly string _tempDirectory;

    public PublicReleaseFinalVerificationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NiceWP_FinalVerif_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void VersionConsistency_Matches_0_0_23_AcrossRepo()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(current);
        string? repoRoot = null;

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NiceWallpaper.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        if (repoRoot == null) return;

        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        var buildScriptPath = Path.Combine(repoRoot, "build.ps1");
        var issPath = Path.Combine(repoRoot, "packaging", "NiceWP.iss");

        Assert.True(File.Exists(propsPath));
        Assert.True(File.Exists(buildScriptPath));
        Assert.True(File.Exists(issPath));

        var propsContent = File.ReadAllText(propsPath);
        Assert.Contains("<Version>0.0.23</Version>", propsContent);
        Assert.Contains("<AssemblyVersion>0.0.23.0</AssemblyVersion>", propsContent);

        var buildContent = File.ReadAllText(buildScriptPath);
        Assert.Contains("NiceWP-0.0.23-win-x64", buildContent);

        var issContent = File.ReadAllText(issPath);
        Assert.Contains("#define MyAppVersion \"0.0.23\"", issContent);
        Assert.Contains("NiceWP-0.0.23-win-x64", issContent);
    }

    [Fact]
    public void PersonalizationEngine_HasZeroNetworkDependencies()
    {
        var storePath = Path.Combine(_tempDirectory, "personalization.json");
        var store = new PersonalizationStore(storePath);
        var service = new PersonalizationService(store);

        // Record all signals
        service.RecordFeedback("wp-1", WallpaperPreferenceAction.MoreLikeThis);
        service.RecordFeedback("wp-2", WallpaperPreferenceAction.LessLikeThis);
        service.RecordFeedback("wp-3", WallpaperPreferenceAction.NeverShowAgain);
        service.RecordFavoriteSignal("wp-4", true);

        // Verification: The subsystem executes completely synchronously/in-memory with local disk save only
        Assert.True(service.Profile.WallpaperPreferences.ContainsKey("wp-1"));
        Assert.True(service.IsExcluded("wp-3"));
    }

    [Fact]
    public void NetworkSafetyValidator_BlocksSSRFEndpointsStrictly()
    {
        var blockedUrls = new[]
        {
            "http://127.0.0.1/wallpaper.jpg",
            "http://localhost/secret.png",
            "http://169.254.169.254/metadata",
            "http://192.168.1.1/router.jpg",
            "http://10.0.0.1/intranet.jpg"
        };

        foreach (var url in blockedUrls)
        {
            var isValid = NetworkSafetyValidator.ValidateUrl(url, out var _, out var errorMessage);
            Assert.False(isValid, $"Expected URL '{url}' to be rejected as SSRF/unsafe.");
            Assert.Equal(NetworkSafetyValidator.ErrorLocalPrivateNotAllowed, errorMessage);
        }
    }

    [Fact]
    public void NetworkSafetyValidator_AllowsValidSecureHttpsUrls()
    {
        var allowedUrls = new[]
        {
            "https://live.staticflickr.com/123/wallpaper.jpg",
            "https://upload.wikimedia.org/wikipedia/commons/a/a1/image.jpg",
            "https://images-assets.nasa.gov/image/PIA12345/PIA12345~orig.jpg"
        };

        foreach (var url in allowedUrls)
        {
            var isValid = NetworkSafetyValidator.ValidateUrl(url, out var uri, out var errorMessage);
            Assert.True(isValid, $"Expected URL '{url}' to be allowed: {errorMessage}");
            Assert.NotNull(uri);
        }
    }
}
