namespace NiceWP.Runtime.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NiceWP.Core;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Infrastructure.SmartScenes;
using Xunit;

public sealed class PersonalizationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _profileFilePath;

    public PersonalizationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NiceWP_PersonalizationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _profileFilePath = Path.Combine(_tempDirectory, "personalization.json");
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
    public void PersonalizationProfile_CreateDefault_HasExpectedInitialValues()
    {
        var profile = PersonalizationProfile.CreateDefault();

        Assert.Equal(PersonalizationProfile.CurrentVersion, profile.Version);
        Assert.True(profile.IsEnabled);
        Assert.NotNull(profile.WallpaperPreferences);
        Assert.Empty(profile.WallpaperPreferences);
        Assert.NotNull(profile.FeatureWeights);
        Assert.Equal(0.0, profile.FeatureWeights.DarkPreference);
        Assert.Equal(0.0, profile.FeatureWeights.LightPreference);
        Assert.NotNull(profile.ExcludedWallpaperIds);
        Assert.Empty(profile.ExcludedWallpaperIds);
        Assert.NotNull(profile.InteractionSummary);
        Assert.Equal(0, profile.InteractionSummary.TotalExplicitInteractions);
    }

    [Fact]
    public async Task PersonalizationStore_SaveAndLoad_RoundtripsProfileAccurately()
    {
        var store = new PersonalizationStore(_profileFilePath);
        store.Profile.FeatureWeights.DarkPreference = 0.65;
        store.Profile.FeatureWeights.StaticPreference = 0.40;
        store.Profile.ExcludedWallpaperIds.Add("wp-exclude-1");
        store.Profile.WallpaperPreferences["wp-1"] = new WallpaperPreferenceRecord
        {
            WallpaperId = "wp-1",
            Weight = 0.50,
            Level = WallpaperPreferenceLevel.Positive,
            LastInteractedAtUtc = DateTimeOffset.UtcNow
        };

        await store.SaveAsync();
        Assert.True(File.Exists(_profileFilePath));

        var secondStore = new PersonalizationStore(_profileFilePath);
        var loaded = await secondStore.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(0.65, loaded.FeatureWeights.DarkPreference, precision: 2);
        Assert.Equal(0.40, loaded.FeatureWeights.StaticPreference, precision: 2);
        Assert.Contains("wp-exclude-1", loaded.ExcludedWallpaperIds);
        Assert.True(loaded.WallpaperPreferences.ContainsKey("wp-1"));
        Assert.Equal(0.50, loaded.WallpaperPreferences["wp-1"].Weight, precision: 2);
    }

    [Fact]
    public async Task PersonalizationStore_CorruptedJson_QuarantinesFileAndReturnsDefault()
    {
        await File.WriteAllTextAsync(_profileFilePath, "{ malformed json content !!! not valid json }");

        var store = new PersonalizationStore(_profileFilePath);
        var profile = await store.LoadAsync();

        Assert.NotNull(profile);
        Assert.Equal(PersonalizationProfile.CurrentVersion, profile.Version);
        Assert.Empty(profile.WallpaperPreferences);

        // Verify quarantined backup file was created
        var backupFiles = Directory.GetFiles(_tempDirectory, "*.bak");
        Assert.Single(backupFiles);
    }

    [Fact]
    public async Task PersonalizationStore_EmptyFile_RecoversSafelyWithoutThrowing()
    {
        await File.WriteAllTextAsync(_profileFilePath, "   ");

        var store = new PersonalizationStore(_profileFilePath);
        var profile = await store.LoadAsync();

        Assert.NotNull(profile);
        Assert.True(profile.IsEnabled);
    }

    [Fact]
    public void PersonalizationDecayPolicy_CalculatesAccurateDecayFactors()
    {
        var now = DateTimeOffset.UtcNow;

        // 10 days old (grace period)
        var mGrace = PersonalizationDecayPolicy.CalculateDecayFactor(now.AddDays(-10), now);
        Assert.Equal(1.0, mGrace, precision: 2);

        // 90 days old (end of moderate decay)
        var m90 = PersonalizationDecayPolicy.CalculateDecayFactor(now.AddDays(-90), now);
        Assert.Equal(0.85, m90, precision: 2);

        // 180 days old (end of long term decay)
        var m180 = PersonalizationDecayPolicy.CalculateDecayFactor(now.AddDays(-180), now);
        Assert.Equal(0.60, m180, precision: 2);

        // 1000 days old (asymptotic to floor of 0.20)
        var mFloor = PersonalizationDecayPolicy.CalculateDecayFactor(now.AddDays(-1000), now);
        Assert.True(mFloor >= PersonalizationDecayPolicy.MinimumDecayFloor);
        Assert.True(mFloor <= 0.25);
    }

    [Fact]
    public void PersonalizationDecayPolicy_GetDecayedWeight_PreservesRecentAndDecaysOld()
    {
        var now = DateTimeOffset.UtcNow;

        var recent = PersonalizationDecayPolicy.GetDecayedWeight(0.80, now.AddDays(-5), now);
        var old = PersonalizationDecayPolicy.GetDecayedWeight(0.80, now.AddDays(-180), now);

        Assert.Equal(0.80, recent, precision: 2);
        Assert.Equal(0.80 * 0.60, old, precision: 2);
    }

    [Fact]
    public void WallpaperPersonalizationScorer_ComputesBoundedContributions()
    {
        var item = new WallpaperLibraryItem
        {
            Id = "item-1",
            FilePath = @"C:\Wallpapers\dark_nature.jpg",
            Type = "Image"
        };

        var meta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "item-1",
            IsDark = true,
            Brightness = 0.20,
            ColorTemperature = WallpaperColorTemperature.Warm,
            ResourceClass = WallpaperCost.Light,
            MotionLevel = WallpaperMotionLevel.None
        };

        var profile = PersonalizationProfile.CreateDefault();
        profile.FeatureWeights.DarkPreference = 1.0;
        profile.FeatureWeights.WarmPreference = 1.0;
        profile.FeatureWeights.StaticPreference = 1.0;
        profile.WallpaperPreferences["item-1"] = new WallpaperPreferenceRecord
        {
            WallpaperId = "item-1",
            Weight = 1.0,
            LastInteractedAtUtc = DateTimeOffset.UtcNow
        };

        var score = WallpaperPersonalizationScorer.Score(item, meta, profile);

        Assert.True(score.Total > 0.0);
        Assert.True(score.Total <= 20.0); // Within bounds
        Assert.NotEmpty(score.ReasonsEn);
        Assert.NotEmpty(score.ReasonsTr);
    }

    [Fact]
    public void WallpaperSceneScorer_SceneHardConstraints_OverridePersonalPreferences()
    {
        var videoItem = new WallpaperLibraryItem
        {
            Id = "heavy-vid",
            FilePath = @"C:\Wallpapers\video.mp4",
            Type = "Video"
        };

        var heavyMeta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "heavy-vid",
            ResourceClass = WallpaperCost.Heavy,
            MotionLevel = WallpaperMotionLevel.High,
            MediaType = WallpaperMediaType.Video
        };

        // User strongly prefers this video (+1.0 item weight, +1.0 video weight)
        var profile = PersonalizationProfile.CreateDefault();
        profile.FeatureWeights.VideoPreference = 1.0;
        profile.WallpaperPreferences["heavy-vid"] = new WallpaperPreferenceRecord
        {
            WallpaperId = "heavy-vid",
            Weight = 1.0,
            LastInteractedAtUtc = DateTimeOffset.UtcNow
        };

        var batteryContext = new SmartSceneContext(SmartSceneMode.BatterySaver, IsOnBattery: true);

        var score = WallpaperSceneScorer.Score(videoItem, heavyMeta, batteryContext, profile);

        // In Battery Saver mode, positive personalization contribution MUST be suppressed (<= 0.0)
        Assert.True(score.PersonalizationScore <= 0.0);
        // Total score heavily penalized (clamped to floor of 0.0)
        Assert.True(score.TotalScore <= 10.0);
    }

    [Fact]
    public void PersonalizationService_RecordFeedback_MoreAndLessLikeThis_AdjustsWeightsAndRaisesAudit()
    {
        var store = new PersonalizationStore(_profileFilePath);
        var service = new PersonalizationService(store);

        var events = new List<PersonalizationAuditEvent>();
        service.AuditEventLogged += (s, e) => events.Add(e);

        var meta = new WallpaperAnalysisMetadata
        {
            WallpaperId = "wp-100",
            IsDark = true,
            ColorTemperature = WallpaperColorTemperature.Warm
        };

        // More Like This
        service.RecordFeedback("wp-100", WallpaperPreferenceAction.MoreLikeThis, meta);

        Assert.True(service.Profile.WallpaperPreferences["wp-100"].Weight > 0.0);
        Assert.True(service.Profile.FeatureWeights.DarkPreference > 0.0);
        Assert.Contains(events, e => e.EventType == PersonalizationAuditEventType.PreferenceIncreased);

        // Less Like This
        service.RecordFeedback("wp-100", WallpaperPreferenceAction.LessLikeThis, meta);
        Assert.Contains(events, e => e.EventType == PersonalizationAuditEventType.PreferenceDecreased);
    }

    [Fact]
    public void PersonalizationService_SetExcluded_FiltersCandidatesInSmartSelection()
    {
        var fileA = Path.Combine(_tempDirectory, "ex_a.jpg");
        var fileB = Path.Combine(_tempDirectory, "ex_b.jpg");
        File.WriteAllBytes(fileA, [1, 2, 3]);
        File.WriteAllBytes(fileB, [1, 2, 3]);

        var store = new PersonalizationStore(_profileFilePath);
        var service = new PersonalizationService(store);

        service.SetExcluded("excluded-wp", true);
        Assert.True(service.IsExcluded("excluded-wp"));
        Assert.False(service.IsExcluded("normal-wp"));

        var items = new List<WallpaperLibraryItem>
        {
            new() { Id = "excluded-wp", FilePath = fileA, Type = "Image" },
            new() { Id = "normal-wp", FilePath = fileB, Type = "Image" }
        };

        var selected = SmartWallpaperSelector.Select(
            items,
            _ => null,
            new SmartSceneContext(SmartSceneMode.Focus),
            null,
            service.Profile,
            id => service.IsExcluded(id));

        Assert.NotNull(selected);
        Assert.Equal("normal-wp", selected.SelectedItem.Id);
    }

    [Fact]
    public void SmartWallpaperSelector_AllCandidatesExcluded_ReturnsNullSafely()
    {
        var fileA = Path.Combine(_tempDirectory, "all_ex_a.jpg");
        var fileB = Path.Combine(_tempDirectory, "all_ex_b.jpg");
        File.WriteAllBytes(fileA, [1, 2, 3]);
        File.WriteAllBytes(fileB, [1, 2, 3]);

        var items = new List<WallpaperLibraryItem>
        {
            new() { Id = "ex-1", FilePath = fileA, Type = "Image" },
            new() { Id = "ex-2", FilePath = fileB, Type = "Image" }
        };

        var selected = SmartWallpaperSelector.Select(
            items,
            _ => null,
            new SmartSceneContext(SmartSceneMode.Focus),
            null,
            null,
            _ => true); // All excluded

        Assert.Null(selected);
    }

    [Fact]
    public async Task PersonalizationService_ResetAsync_ClearsProfileAndRaisesResetCompleted()
    {
        var store = new PersonalizationStore(_profileFilePath);
        var service = new PersonalizationService(store);

        service.SetExcluded("ex-1", true);
        service.RecordFeedback("wp-1", WallpaperPreferenceAction.MoreLikeThis);

        bool resetRaised = false;
        service.ResetCompleted += (s, e) => resetRaised = true;

        await service.ResetAsync();

        Assert.True(resetRaised);
        Assert.Empty(service.Profile.ExcludedWallpaperIds);
        Assert.Empty(service.Profile.WallpaperPreferences);
        Assert.Equal(0.0, service.Profile.FeatureWeights.DarkPreference);
    }

    [Fact]
    public void PersonalizationSummaryFormatter_ReturnsTopTendenciesWhenSignificant()
    {
        var weights = new PersonalizationFeatureWeights
        {
            DarkPreference = 0.60,
            LowMotionPreference = 0.45,
            WarmPreference = 0.30,
            StaticPreference = 0.10 // Below 0.25 significance threshold
        };

        var en = PersonalizationSummaryFormatter.GetTopTendencies(weights, isTr: false);
        var tr = PersonalizationSummaryFormatter.GetTopTendencies(weights, isTr: true);

        Assert.Equal(3, en.Count);
        Assert.Equal(3, tr.Count);
        Assert.Contains(en, s => s.Contains("Dark"));
        Assert.Contains(tr, s => s.Contains("Koyu"));
    }

    [Fact]
    public void PersonalizationService_RecordManualSelection_IncrementsBoundedCount()
    {
        var store = new PersonalizationStore(_profileFilePath);
        var service = new PersonalizationService(store);

        var item = new WallpaperLibraryItem { Id = "manual-wp", FilePath = @"C:\wp\m.jpg", Type = "Image" };

        service.RecordManualSelection(item);
        service.RecordManualSelection(item);

        Assert.Equal(2, service.Profile.InteractionSummary.ManualSelectionCount);
        Assert.True(service.Profile.WallpaperPreferences["manual-wp"].Weight > 0.0);
    }

    [Fact]
    public void PersonalizationService_FavoriteSignal_AdjustsWeight()
    {
        var store = new PersonalizationStore(_profileFilePath);
        var service = new PersonalizationService(store);

        service.RecordFavoriteSignal("wp-fav", isFavorite: true);
        Assert.True(service.Profile.WallpaperPreferences["wp-fav"].Weight > 0.0);

        service.RecordFavoriteSignal("wp-fav", isFavorite: false);
        Assert.Equal(0.0, service.Profile.WallpaperPreferences["wp-fav"].Weight, precision: 2);
    }
}
