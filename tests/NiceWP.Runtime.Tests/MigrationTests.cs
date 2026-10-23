namespace NiceWP.Runtime.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NiceWP.Core;
using NiceWP.Infrastructure.Analysis;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Tests.Fixtures.Migration;
using Xunit;

public sealed class MigrationTests : IDisposable
{
    private readonly string _tempDirectory;

    public MigrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NiceWP_MigrationTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task SettingsMigration_From_v0_15_LoadsSuccessfullyWithDefaults()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings_015.json");
        await File.WriteAllTextAsync(settingsPath, MigrationFixtures.Settings_v0_15, Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync();

        Assert.NotNull(settings);
        // Preserved legacy fields
        Assert.True(settings.RestoreLastWallpaper);
        Assert.Equal("Image", settings.LastWallpaperType);
        Assert.Equal("Dark", settings.Theme);
        // Safe modern defaults for unrepresented fields
        Assert.False(settings.SmartScenesEnabled);
        Assert.True(settings.PersonalizationEnabled);
        Assert.True(settings.OnlineOpenverseEnabled);
        Assert.True(settings.OnlineWikimediaEnabled);
        Assert.True(settings.OnlineNasaEnabled);
        Assert.False(settings.InteractiveTransitionEnabled);
    }

    [Fact]
    public async Task SettingsMigration_From_v0_18_LoadsSuccessfullyWithDefaults()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings_018.json");
        await File.WriteAllTextAsync(settingsPath, MigrationFixtures.Settings_v0_18, Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync();

        Assert.NotNull(settings);
        Assert.Equal("Sequential", settings.RotationOrder);
        Assert.Equal(15, settings.RotationIntervalMinutes);
        // Safe modern defaults
        Assert.True(settings.PersonalizationEnabled);
        Assert.True(settings.PersonalizationUseFavorites);
    }

    [Fact]
    public async Task SettingsMigration_From_v0_20_LoadsSuccessfullyWithDefaults()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings_020.json");
        await File.WriteAllTextAsync(settingsPath, MigrationFixtures.Settings_v0_20, Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync();

        Assert.NotNull(settings);
        Assert.Equal("Turkish", settings.Language);
        Assert.True(settings.OnlineOpenverseEnabled);
        Assert.True(settings.PersonalizationEnabled);
    }

    [Fact]
    public async Task SettingsMigration_From_v0_22_LoadsSuccessfullyWithDefaults()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings_022.json");
        await File.WriteAllTextAsync(settingsPath, MigrationFixtures.Settings_v0_22, Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync();

        Assert.NotNull(settings);
        Assert.Equal("English", settings.Language);
        Assert.True(settings.SmartScenesEnabled);
        Assert.Equal(SmartSceneMode.Automatic, settings.EffectiveSmartScene);
        Assert.True(settings.PersonalizationEnabled);
        Assert.True(settings.PersonalizationUseFavorites);
        Assert.True(settings.PersonalizationLearnManualSelections);
        Assert.True(settings.PersonalizationAvoidExcluded);
    }

    [Fact]
    public async Task LibraryMigration_From_v0_15_LoadsAllItemsLosslessly()
    {
        var libraryPath = Path.Combine(_tempDirectory, "library_015.json");
        await File.WriteAllTextAsync(libraryPath, MigrationFixtures.Library_v0_15, Encoding.UTF8);

        var service = new WallpaperLibraryService(libraryPath);
        var library = await service.LoadAsync();

        Assert.NotNull(library);
        Assert.Equal(2, library.Items.Count);

        var forest = library.FindById("item-legacy-1");
        Assert.NotNull(forest);
        Assert.Equal("Forest Morning", forest.DisplayName);
        Assert.Equal("Image", forest.Type);
        Assert.False(forest.IsFavorite);
        Assert.Null(forest.Origin);

        var ocean = library.FindById("item-legacy-2");
        Assert.NotNull(ocean);
        Assert.Equal("Calm Waves", ocean.DisplayName);
        Assert.Equal("Video", ocean.Type);
        Assert.True(ocean.IsFavorite);
    }

    [Fact]
    public async Task LibraryMigration_From_v0_20_PreservesOnlineProvenance()
    {
        var libraryPath = Path.Combine(_tempDirectory, "library_020.json");
        await File.WriteAllTextAsync(libraryPath, MigrationFixtures.Library_v0_20, Encoding.UTF8);

        var service = new WallpaperLibraryService(libraryPath);
        var library = await service.LoadAsync();

        Assert.NotNull(library);
        Assert.Single(library.Items);

        var nebula = library.FindById("item-online-1");
        Assert.NotNull(nebula);
        Assert.NotNull(nebula.Origin);
        Assert.Equal("NASA", nebula.Origin.Provider);
        Assert.Equal("PIA12345", nebula.Origin.RemoteId);
        Assert.Equal("NASA / JPL", nebula.Origin.Creator);
        Assert.Equal("Public Domain", nebula.Origin.License);
    }

    [Fact]
    public async Task AnalysisMigration_From_Legacy_LoadsAccurately()
    {
        var analysisPath = Path.Combine(_tempDirectory, "analysis_legacy.json");
        await File.WriteAllTextAsync(analysisPath, MigrationFixtures.Analysis_Legacy, Encoding.UTF8);

        var store = new WallpaperAnalysisStore(analysisPath);
        await store.LoadAsync();

        Assert.True(store.TryGet("item-legacy-1", out var meta));
        Assert.NotNull(meta);
        Assert.True(meta.IsDark);
        Assert.False(meta.IsLight);
        Assert.Equal(WallpaperColorTemperature.Warm, meta.ColorTemperature);
        Assert.Equal(WallpaperCost.Light, meta.ResourceClass);
    }

    [Fact]
    public async Task Settings_Corrupted_QuarantinesFile_AndReturnsSafeDefaults()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings_corrupt.json");
        await File.WriteAllTextAsync(settingsPath, MigrationFixtures.Settings_Corrupt, Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var settings = await service.LoadAsync();

        Assert.NotNull(settings);
        Assert.True(settings.RestoreLastWallpaper);

        // Verify quarantine backup was generated
        var backups = Directory.GetFiles(_tempDirectory, "settings_corrupt.json.corrupt.*.bak");
        Assert.Single(backups);
    }

    [Fact]
    public async Task Library_Corrupted_QuarantinesFile_AndReturnsSafeDefaults()
    {
        var libraryPath = Path.Combine(_tempDirectory, "library_corrupt.json");
        await File.WriteAllTextAsync(libraryPath, MigrationFixtures.Library_Corrupt, Encoding.UTF8);

        var service = new WallpaperLibraryService(libraryPath);
        var library = await service.LoadAsync();

        Assert.NotNull(library);
        Assert.Empty(library.Items);

        // Verify quarantine backup was generated
        var backups = Directory.GetFiles(_tempDirectory, "library_corrupt.json.corrupt.*.bak");
        Assert.Single(backups);
    }

    [Fact]
    public async Task Analysis_Corrupted_QuarantinesFile_Safely()
    {
        var analysisPath = Path.Combine(_tempDirectory, "analysis_corrupt.json");
        await File.WriteAllTextAsync(analysisPath, "{ broken analysis json content ...", Encoding.UTF8);

        var store = new WallpaperAnalysisStore(analysisPath);
        await store.LoadAsync();

        // Verify quarantine backup was generated
        var backups = Directory.GetFiles(_tempDirectory, "analysis_corrupt.json.corrupt.*.bak");
        Assert.Single(backups);
    }
}
