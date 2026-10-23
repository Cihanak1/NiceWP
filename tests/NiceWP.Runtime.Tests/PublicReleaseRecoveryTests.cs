namespace NiceWP.Runtime.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NiceWP.Core;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Infrastructure.SingleInstance;
using Xunit;

public sealed class PublicReleaseRecoveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public PublicReleaseRecoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NiceWP_ReleaseRecovery_" + Guid.NewGuid().ToString("N"));
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
    public async Task PersonalizationStore_UpgradeFromPre021_CreatesCleanProfileWithoutCrashing()
    {
        // Simulate user upgrading from 0.0.20/0.0.21 where personalization.json doesn't exist
        var profilePath = Path.Combine(_tempDirectory, "personalization.json");
        Assert.False(File.Exists(profilePath));

        var store = new PersonalizationStore(profilePath);
        var profile = await store.LoadAsync();

        Assert.NotNull(profile);
        Assert.True(profile.IsEnabled);
        Assert.Empty(profile.WallpaperPreferences);
        Assert.NotNull(profile.FeatureWeights);
        Assert.Equal(0.0, profile.FeatureWeights.DarkPreference);
    }

    [Fact]
    public async Task PersonalizationStore_LosslessUpgradeFrom022_RetainsExistingPreferences()
    {
        var profilePath = Path.Combine(_tempDirectory, "personalization_022.json");
        var existingStore = new PersonalizationStore(profilePath);
        existingStore.Profile.FeatureWeights.DarkPreference = 0.75;
        existingStore.Profile.WallpaperPreferences["wp-42"] = new WallpaperPreferenceRecord
        {
            WallpaperId = "wp-42",
            Weight = 0.60,
            Level = WallpaperPreferenceLevel.Positive,
            LastInteractedAtUtc = DateTimeOffset.UtcNow
        };
        await existingStore.SaveAsync();

        // New release loads the existing 0.0.22 profile
        var upgradeStore = new PersonalizationStore(profilePath);
        var loaded = await upgradeStore.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(0.75, loaded.FeatureWeights.DarkPreference, precision: 2);
        Assert.True(loaded.WallpaperPreferences.ContainsKey("wp-42"));
        Assert.Equal(0.60, loaded.WallpaperPreferences["wp-42"].Weight, precision: 2);
    }

    [Fact]
    public void SingleInstance_SecondaryAppLaunch_CorrectlySignalsCollision()
    {
        var mutexName = $@"Local\NiceWP.Test.ReleaseSingleInstance.{Guid.NewGuid():N}";
        using var primary = new AppSingleInstanceGuard(mutexName);
        Assert.True(primary.IsSingleInstance, "First instance must acquire mutex.");

        using var secondary = new AppSingleInstanceGuard(mutexName);
        Assert.False(secondary.IsSingleInstance, "Second instance must report collision.");
    }

    [Fact]
    public void LargeLibrary_1000Items_NormalizesAndFiltersQuicklyWithoutCrash()
    {
        var items = new List<WallpaperLibraryItem>(1000);
        for (int i = 0; i < 1000; i++)
        {
            var isVideo = i % 10 == 0;
            var ext = isVideo ? "mp4" : "jpg";
            items.Add(new WallpaperLibraryItem
            {
                Id = $"item-{i:D4}",
                DisplayName = $"Wallpaper {i}",
                FilePath = $@"C:\Wallpapers\wall_{i}.{ext}",
                Type = isVideo ? "Video" : "Image",
                IsFavorite = i % 5 == 0,
                AddedAtUtc = DateTimeOffset.UtcNow.AddHours(-i)
            });
        }

        var library = new WallpaperLibrary { Items = items };
        var normalized = library.Normalize();

        Assert.Equal(1000, normalized.Items.Count);

        var stopwatch = Stopwatch.StartNew();
        var found = library.FindById("item-0500");
        stopwatch.Stop();

        Assert.NotNull(found);
        Assert.Equal("Wallpaper 500", found.DisplayName);
        Assert.True(stopwatch.ElapsedMilliseconds < 50, "ID lookup on 1000 items should complete in under 50ms.");
    }
}
