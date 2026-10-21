using System.Text;
using System.Text.Json;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;

    public SettingsServiceTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_SettingsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        settingsFilePath = Path.Combine(tempDirectory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors in tests
        }
    }

    [Fact]
    public void GetDefaultSettingsPath_PointsToLocalAppDataNiceWPSettingsJson()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NiceWP",
            "settings.json");

        var service = new SettingsService();
        Assert.Equal(expected, SettingsService.GetDefaultSettingsPath());
        Assert.Equal(expected, service.FilePath);
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaultsWithoutCreatingFile()
    {
        var nonExistentPath = Path.Combine(tempDirectory, "missing", "settings.json");
        var service = new SettingsService(nonExistentPath);

        var settings = await service.LoadAsync();

        Assert.True(settings.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, settings.LastWallpaperType);
        Assert.Null(settings.LastWallpaperPath);
        Assert.False(File.Exists(nonExistentPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    public async Task LoadAsync_WhenFileIsEmptyOrWhitespace_ReturnsDefaultsAndPreservesFile(string content)
    {
        await File.WriteAllTextAsync(settingsFilePath, content, Encoding.UTF8);
        var service = new SettingsService(settingsFilePath);

        var settings = await service.LoadAsync();

        Assert.True(settings.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, settings.LastWallpaperType);
        Assert.Null(settings.LastWallpaperPath);
        Assert.True(File.Exists(settingsFilePath));
        Assert.Equal(content, await File.ReadAllTextAsync(settingsFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task LoadAsync_WithValidJson_LoadsExpectedValues()
    {
        const string json = """
        {
          "RestoreLastWallpaper": false,
          "LastWallpaperType": "Video",
          "LastWallpaperPath": "C:\\Wallpapers\\loop.mp4"
        }
        """;
        await File.WriteAllTextAsync(settingsFilePath, json, Encoding.UTF8);
        var service = new SettingsService(settingsFilePath);

        var settings = await service.LoadAsync();

        Assert.False(settings.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, settings.LastWallpaperType);
        Assert.Equal(@"C:\Wallpapers\loop.mp4", settings.LastWallpaperPath);
    }

    [Theory]
    [InlineData("{ broken json")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("{\"RestoreLastWallpaper\": \"invalid-bool\"}")]
    public async Task LoadAsync_WithCorruptJson_ReturnsDefaultsAndDoesNotModifyCorruptFile(string corruptContent)
    {
        await File.WriteAllTextAsync(settingsFilePath, corruptContent, Encoding.UTF8);
        var service = new SettingsService(settingsFilePath);

        var settings = await service.LoadAsync();

        Assert.True(settings.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, settings.LastWallpaperType);
        Assert.Null(settings.LastWallpaperPath);
        Assert.True(File.Exists(settingsFilePath));
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(settingsFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task LoadAsync_WithMissingProperties_AppliesSafeDefaults()
    {
        // Only LastWallpaperType and LastWallpaperPath provided; RestoreLastWallpaper missing -> defaults to true
        const string partialJson1 = """
        {
          "LastWallpaperType": "Image",
          "LastWallpaperPath": "C:\\Wallpapers\\scene.png"
        }
        """;
        await File.WriteAllTextAsync(settingsFilePath, partialJson1, Encoding.UTF8);
        var service = new SettingsService(settingsFilePath);

        var loaded1 = await service.LoadAsync();
        Assert.True(loaded1.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, loaded1.LastWallpaperType);
        Assert.Equal(@"C:\Wallpapers\scene.png", loaded1.LastWallpaperPath);

        // Empty JSON object -> all properties default safely
        await File.WriteAllTextAsync(settingsFilePath, "{}", Encoding.UTF8);
        var loaded2 = await service.LoadAsync();
        Assert.True(loaded2.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, loaded2.LastWallpaperType);
        Assert.Null(loaded2.LastWallpaperPath);
    }

    [Theory]
    [InlineData("Gif")]
    [InlineData("Unknown")]
    [InlineData("Shader")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadAsync_WithInvalidWallpaperType_NormalizesToSafeDefaults(string invalidType)
    {
        var json = $$"""
        {
          "RestoreLastWallpaper": false,
          "LastWallpaperType": "{{invalidType}}",
          "LastWallpaperPath": "C:\\Wallpapers\\file.dat"
        }
        """;
        await File.WriteAllTextAsync(settingsFilePath, json, Encoding.UTF8);
        var service = new SettingsService(settingsFilePath);

        var settings = await service.LoadAsync();

        Assert.False(settings.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, settings.LastWallpaperType);
        Assert.Null(settings.LastWallpaperPath);
    }

    [Fact]
    public async Task SaveAndLoadAsync_Roundtrip_PersistsSettingsInUtf8JsonWithoutAutostart()
    {
        var nestedSettingsPath = Path.Combine(tempDirectory, "sub", "dir", "settings.json");
        var service = new SettingsService(nestedSettingsPath);

        var toSave = new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = @"C:\Wallpapers\mountain.jpg"
        };

        var saved = await service.SaveAsync(toSave);
        Assert.True(saved);
        Assert.True(File.Exists(nestedSettingsPath));

        var rawJson = await File.ReadAllTextAsync(nestedSettingsPath, Encoding.UTF8);
        using (var doc = JsonDocument.Parse(rawJson))
        {
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("RestoreLastWallpaper", out _));
            Assert.True(root.TryGetProperty("LastWallpaperType", out _));
            Assert.True(root.TryGetProperty("LastWallpaperPath", out _));
            Assert.True(root.TryGetProperty("RotationMode", out _));
            Assert.True(root.TryGetProperty("RotationOrder", out _));
            Assert.True(root.TryGetProperty("RotationIntervalMinutes", out _));
            Assert.False(root.TryGetProperty("Autostart", out _));
            Assert.True(root.TryGetProperty("AppRules", out _));
            Assert.Equal(7, root.EnumerateObject().Count());
        }

        var loaded = await service.LoadAsync();
        Assert.True(loaded.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, loaded.LastWallpaperType);
        Assert.Equal(@"C:\Wallpapers\mountain.jpg", loaded.LastWallpaperPath);
    }

    [Fact]
    public async Task ResetAsync_RestoresDefaultSettingsOnDisk()
    {
        var service = new SettingsService(settingsFilePath);

        await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = false,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = @"C:\Wallpapers\clip.mp4"
        });

        var resetSucceeded = await service.ResetAsync();
        Assert.True(resetSucceeded);

        var loaded = await service.LoadAsync();
        Assert.True(loaded.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, loaded.LastWallpaperType);
        Assert.Null(loaded.LastWallpaperPath);
    }

    [Fact]
    public async Task SaveAsync_WritesViaTempFile_LeavesNoTempFilesAndCleansUpOnWriteError()
    {
        var service = new SettingsService(settingsFilePath);

        var saved = await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = @"C:\Wallpapers\first.png"
        });
        Assert.True(saved);

        // No temporary files should remain after a successful save
        var remainingTempFiles = Directory.GetFiles(tempDirectory, "*.tmp");
        Assert.Empty(remainingTempFiles);

        // Lock the target file exclusively to force File.Move replacement failure
        await using (var lockStream = new FileStream(
            settingsFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var failedSave = await service.SaveAsync(new AppSettings
            {
                RestoreLastWallpaper = false,
                LastWallpaperType = IpcConstants.WallpaperTypeVideo,
                LastWallpaperPath = @"C:\Wallpapers\second.mp4"
            });

            Assert.False(failedSave);
        }

        // Temporary file created during failed save must be cleaned up
        remainingTempFiles = Directory.GetFiles(tempDirectory, "*.tmp");
        Assert.Empty(remainingTempFiles);

        // Original settings file must still contain the previous valid state
        var preserved = await service.LoadAsync();
        Assert.True(preserved.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, preserved.LastWallpaperType);
        Assert.Equal(@"C:\Wallpapers\first.png", preserved.LastWallpaperPath);
    }

    [Fact]
    public async Task SaveAsync_WhenDirectoryCreationFails_ReturnsFalseWithoutThrowing()
    {
        // Create a file where the parent directory would need to be created
        var blockingFile = Path.Combine(tempDirectory, "not_a_directory");
        await File.WriteAllTextAsync(blockingFile, "block");

        var invalidTarget = Path.Combine(blockingFile, "settings.json");
        var service = new SettingsService(invalidTarget);

        var result = await service.SaveAsync(AppSettings.CreateDefault());
        Assert.False(result);
    }

    [Fact]
    public async Task SaveAndLoadAsync_ThemePreference_NormalizesAndPersistsLightAndDarkThemes()
    {
        var service = new SettingsService(settingsFilePath);

        Assert.True(await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeNone,
            Theme = "  dark "
        }));

        var loadedDark = await service.LoadAsync();
        Assert.Equal(AppSettings.ThemeDark, loadedDark.Theme);

        Assert.True(await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeNone,
            Theme = "light"
        }));

        var loadedLight = await service.LoadAsync();
        Assert.Equal(AppSettings.ThemeLight, loadedLight.Theme);

        Assert.True(await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeNone,
            Theme = "System"
        }));

        var loadedSystem = await service.LoadAsync();
        Assert.Null(loadedSystem.Theme);
    }

    [Fact]
    public async Task SaveAndLoadAsync_PresentationWallpaperId_RoundtripsSuccessfully()
    {
        var service = new SettingsService(settingsFilePath);

        Assert.True(await service.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            PresentationWallpaperId = "pres-item-123"
        }));

        var loaded = await service.LoadAsync();
        Assert.Equal("pres-item-123", loaded.PresentationWallpaperId);
    }
}
