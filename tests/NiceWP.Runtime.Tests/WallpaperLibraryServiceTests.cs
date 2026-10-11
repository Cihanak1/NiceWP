using System.Text;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperLibraryServiceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string libraryFilePath;
    private readonly WallpaperLibraryService service;

    public WallpaperLibraryServiceTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_LibraryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        libraryFilePath = Path.Combine(tempDirectory, "library.json");
        service = new WallpaperLibraryService(libraryFilePath);
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

    private string CreateTempFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact]
    public void GetDefaultLibraryPath_PointsToLocalAppDataNiceWPLibraryJson_SeparateFromSettingsJson()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NiceWP",
            "library.json");

        var defaultService = new WallpaperLibraryService();
        Assert.Equal(expected, WallpaperLibraryService.GetDefaultLibraryPath());
        Assert.Equal(expected, defaultService.FilePath);
        Assert.NotEqual(SettingsService.GetDefaultSettingsPath(), WallpaperLibraryService.GetDefaultLibraryPath());
    }

    [Fact]
    public async Task LoadAsync_WhenLibraryFileDoesNotExist_ReturnsEmptyVersion1WithoutCreatingFile()
    {
        var missingPath = Path.Combine(tempDirectory, "missing_folder", "library.json");
        var missingService = new WallpaperLibraryService(missingPath);

        var library = await missingService.LoadAsync();

        Assert.Equal(WallpaperLibrary.CurrentVersion, library.Version);
        Assert.NotNull(library.Items);
        Assert.Empty(library.Items);
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public async Task LoadAsync_WithValidLibraryJson_LoadsAllItems()
    {
        var imagePath = Path.Combine(tempDirectory, "sunset.png");
        var videoPath = Path.Combine(tempDirectory, "ocean.mp4");
        var json = $$"""
        {
          "Version": 1,
          "Items": [
            {
              "Id": "item-1",
              "Type": "Image",
              "FilePath": {{JsonSerializer.Serialize(imagePath)}},
              "DisplayName": "sunset.png",
              "AddedAtUtc": "2026-10-11T12:00:00+00:00"
            },
            {
              "Id": "item-2",
              "Type": "Video",
              "FilePath": {{JsonSerializer.Serialize(videoPath)}},
              "DisplayName": "ocean.mp4",
              "AddedAtUtc": "2026-10-11T12:05:00+00:00"
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(libraryFilePath, json, Encoding.UTF8);

        var library = await service.LoadAsync();

        Assert.Equal(1, library.Version);
        Assert.Equal(2, library.Items.Count);
        Assert.Equal("item-1", library.Items[0].Id);
        Assert.Equal(IpcConstants.WallpaperTypeImage, library.Items[0].Type);
        Assert.Equal(Path.GetFullPath(imagePath), library.Items[0].FilePath);
        Assert.Equal("sunset.png", library.Items[0].DisplayName);
        Assert.Equal("item-2", library.Items[1].Id);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, library.Items[1].Type);
        Assert.Equal(Path.GetFullPath(videoPath), library.Items[1].FilePath);
    }

    [Fact]
    public async Task SaveAndLoadAsync_Roundtrip_PersistsExpectedSchemaWithoutIsMissingProperty()
    {
        var nestedLibraryPath = Path.Combine(tempDirectory, "nested", "library.json");
        var nestedService = new WallpaperLibraryService(nestedLibraryPath);
        var imagePath = CreateTempFile("forest.jpg");
        var timestamp = new DateTimeOffset(2026, 10, 11, 15, 30, 0, TimeSpan.Zero);

        var libraryToSave = new WallpaperLibrary
        {
            Version = WallpaperLibrary.CurrentVersion,
            Items =
            [
                new WallpaperLibraryItem
                {
                    Id = "abc12345",
                    Type = IpcConstants.WallpaperTypeImage,
                    FilePath = imagePath,
                    DisplayName = "forest.jpg",
                    AddedAtUtc = timestamp
                }
            ]
        };

        Assert.True(await nestedService.SaveAsync(libraryToSave));
        Assert.True(File.Exists(nestedLibraryPath));

        var rawJson = await File.ReadAllTextAsync(nestedLibraryPath, Encoding.UTF8);
        using (var doc = JsonDocument.Parse(rawJson))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("Version").GetInt32());
            var itemElement = root.GetProperty("Items")[0];
            Assert.True(itemElement.TryGetProperty("Id", out _));
            Assert.True(itemElement.TryGetProperty("Type", out _));
            Assert.True(itemElement.TryGetProperty("FilePath", out _));
            Assert.True(itemElement.TryGetProperty("DisplayName", out _));
            Assert.True(itemElement.TryGetProperty("AddedAtUtc", out _));
            Assert.False(itemElement.TryGetProperty("IsMissing", out _));
            Assert.Equal(5, itemElement.EnumerateObject().Count());
        }

        var loaded = await nestedService.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.Equal("abc12345", loaded.Items[0].Id);
        Assert.Equal(IpcConstants.WallpaperTypeImage, loaded.Items[0].Type);
        Assert.Equal(Path.GetFullPath(imagePath), loaded.Items[0].FilePath);
        Assert.Equal("forest.jpg", loaded.Items[0].DisplayName);
        Assert.Equal(timestamp, loaded.Items[0].AddedAtUtc);
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    public async Task AddAsync_SupportedImageFormats_AddsItemWithImageType(string fileName)
    {
        var filePath = CreateTempFile(fileName);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await service.AddAsync(filePath);

        Assert.True(result.Success);
        Assert.False(result.AlreadyExisted);
        Assert.NotNull(result.Item);
        Assert.False(string.IsNullOrWhiteSpace(result.Item!.Id));
        Assert.Equal(IpcConstants.WallpaperTypeImage, result.Item.Type);
        Assert.Equal(Path.GetFullPath(filePath), result.Item.FilePath);
        Assert.Equal(fileName, result.Item.DisplayName);
        Assert.True(result.Item.AddedAtUtc >= before);

        var loaded = await service.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.Equal(result.Item.Id, loaded.Items[0].Id);
    }

    [Fact]
    public async Task AddAsync_SupportedVideoFormat_AddsItemWithVideoType()
    {
        var videoPath = CreateTempFile("animation.mp4");

        var result = await service.AddAsync(videoPath);

        Assert.True(result.Success);
        Assert.False(result.AlreadyExisted);
        Assert.NotNull(result.Item);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, result.Item!.Type);
        Assert.Equal(Path.GetFullPath(videoPath), result.Item.FilePath);
        Assert.Equal("animation.mp4", result.Item.DisplayName);

        var loaded = await service.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, loaded.Items[0].Type);
    }

    [Theory]
    [InlineData("animated.gif")]
    [InlineData("bitmap.bmp")]
    [InlineData("video.mkv")]
    [InlineData("video.webm")]
    [InlineData("notes.txt")]
    public async Task AddAsync_UnsupportedExtension_RejectsAndDoesNotAddToLibrary(string fileName)
    {
        var unsupportedFile = CreateTempFile(fileName);

        var result = await service.AddAsync(unsupportedFile);

        Assert.False(result.Success);
        Assert.Null(result.Item);
        Assert.Equal(WallpaperError.UnsupportedFormat, result.Error);

        var loaded = await service.LoadAsync();
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task AddAsync_WhenSourceFileDoesNotExist_RejectsWithFileNotFound()
    {
        var missingFile = Path.Combine(tempDirectory, "does_not_exist.png");

        var result = await service.AddAsync(missingFile);

        Assert.False(result.Success);
        Assert.Null(result.Item);
        Assert.Equal(WallpaperError.FileNotFound, result.Error);

        var loaded = await service.LoadAsync();
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task AddAsync_DuplicatePath_AndCaseInsensitiveWindowsPath_DoesNotCreateDuplicateAndReturnsExistingItem()
    {
        var imagePath = CreateTempFile("MountainView.png");

        var firstAdd = await service.AddAsync(imagePath);
        Assert.True(firstAdd.Success);
        Assert.False(firstAdd.AlreadyExisted);
        Assert.NotNull(firstAdd.Item);

        // Exact duplicate path
        var secondAdd = await service.AddAsync(imagePath);
        Assert.True(secondAdd.Success);
        Assert.True(secondAdd.AlreadyExisted);
        Assert.NotNull(secondAdd.Item);
        Assert.Equal(firstAdd.Item!.Id, secondAdd.Item!.Id);

        // Case-insensitive Windows path variant (upper/lower differences + relative segment)
        var upperCaseVariant = Path.Combine(tempDirectory.ToUpperInvariant(), ".", "mountainview.PNG");
        var thirdAdd = await service.AddAsync(upperCaseVariant);
        Assert.True(thirdAdd.Success);
        Assert.True(thirdAdd.AlreadyExisted);
        Assert.NotNull(thirdAdd.Item);
        Assert.Equal(firstAdd.Item.Id, thirdAdd.Item!.Id);

        var loaded = await service.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.True(await service.ContainsPathAsync(upperCaseVariant));
    }

    [Fact]
    public async Task AddAsync_MultipleDistinctFiles_AssignsUniqueIds()
    {
        var file1 = CreateTempFile("one.png");
        var file2 = CreateTempFile("two.jpg");
        var file3 = CreateTempFile("three.mp4");

        var r1 = await service.AddAsync(file1);
        var r2 = await service.AddAsync(file2);
        var r3 = await service.AddAsync(file3);

        Assert.True(r1.Success && r2.Success && r3.Success);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            r1.Item!.Id,
            r2.Item!.Id,
            r3.Item!.Id
        };
        Assert.Equal(3, ids.Count);

        var loaded = await service.LoadAsync();
        Assert.Equal(3, loaded.Items.Count);
    }

    [Fact]
    public async Task RemoveAsync_RemovesLibraryEntry_AndLeavesPhysicalFileUntouched()
    {
        var imagePath = CreateTempFile("keep_me_on_disk.png");
        var originalBytes = await File.ReadAllBytesAsync(imagePath);

        var addResult = await service.AddAsync(imagePath);
        Assert.True(addResult.Success);
        var itemId = addResult.Item!.Id;

        var removed = await service.RemoveAsync(itemId);
        Assert.True(removed);

        // Removing again returns false
        Assert.False(await service.RemoveAsync(itemId));

        // Library entry is gone
        var loaded = await service.LoadAsync();
        Assert.Empty(loaded.Items);
        Assert.Null(await service.FindByIdAsync(itemId));

        // Physical file on disk is completely untouched
        Assert.True(File.Exists(imagePath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(imagePath));
    }

    [Fact]
    public async Task LoadAsync_WhenWallpaperFileDeletedAfterSave_DoesNotCrashAndPreservesItem()
    {
        var videoPath = CreateTempFile("deleted_later.mp4");
        var addResult = await service.AddAsync(videoPath);
        Assert.True(addResult.Success);
        Assert.True(addResult.Item!.FileExists());

        // Delete physical file from disk
        File.Delete(videoPath);
        Assert.False(File.Exists(videoPath));

        // LoadAsync must not crash and must preserve the entry
        var loaded = await service.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.Equal(addResult.Item.Id, loaded.Items[0].Id);
        Assert.Equal(Path.GetFullPath(videoPath), loaded.Items[0].FilePath);
        Assert.False(loaded.Items[0].FileExists());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("{ broken json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("{\"Version\": 0, \"Items\": []}")]
    [InlineData("{\"Version\": 2, \"Items\": []}")]
    [InlineData("{\"Version\": \"invalid\", \"Items\": []}")]
    [InlineData("{\"Version\": 1, \"Items\": null}")]
    public async Task LoadAsync_WithCorruptOrInvalidLibraryFile_ReturnsEmptySafeLibraryWithoutOverwritingCorruptFile(string content)
    {
        await File.WriteAllTextAsync(libraryFilePath, content, Encoding.UTF8);

        var loaded = await service.LoadAsync();

        Assert.Equal(WallpaperLibrary.CurrentVersion, loaded.Version);
        Assert.NotNull(loaded.Items);
        Assert.Empty(loaded.Items);

        // Corrupt file on disk must not be deleted or overwritten by LoadAsync
        Assert.True(File.Exists(libraryFilePath));
        Assert.Equal(content, await File.ReadAllTextAsync(libraryFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task LoadAsync_WithDuplicateIdsDuplicatePathsAndMalformedItems_SanitizesAndKeepsValidItems()
    {
        var validImage = Path.Combine(tempDirectory, "valid1.png");
        var validVideo = Path.Combine(tempDirectory, "valid2.mp4");
        var duplicatePathUpper = validImage.ToUpperInvariant();

        var json = $$"""
        {
          "Version": 1,
          "Items": [
            null,
            {
              "Id": "",
              "Type": "Image",
              "FilePath": "C:\\Wallpapers\\missing_id.png"
            },
            {
              "Id": "bad-ext",
              "Type": "Image",
              "FilePath": "C:\\Wallpapers\\not_supported.exe"
            },
            {
              "Id": "type-mismatch",
              "Type": "Video",
              "FilePath": "C:\\Wallpapers\\actually_an_image.png"
            },
            {
              "Id": "keep-1",
              "Type": "Image",
              "FilePath": {{JsonSerializer.Serialize(validImage)}},
              "DisplayName": "valid1.png",
              "AddedAtUtc": "2026-10-11T10:00:00+00:00"
            },
            {
              "Id": "keep-1",
              "Type": "Video",
              "FilePath": {{JsonSerializer.Serialize(validVideo)}},
              "DisplayName": "duplicate_id.mp4",
              "AddedAtUtc": "2026-10-11T10:01:00+00:00"
            },
            {
              "Id": "dup-path-id",
              "Type": "Image",
              "FilePath": {{JsonSerializer.Serialize(duplicatePathUpper)}},
              "DisplayName": "duplicate_path.png",
              "AddedAtUtc": "2026-10-11T10:02:00+00:00"
            },
            {
              "Id": "keep-2",
              "Type": "Video",
              "FilePath": {{JsonSerializer.Serialize(validVideo)}},
              "DisplayName": "",
              "AddedAtUtc": "2026-10-11T10:03:00+00:00"
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(libraryFilePath, json, Encoding.UTF8);

        var loaded = await service.LoadAsync();

        Assert.Equal(1, loaded.Version);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("keep-1", loaded.Items[0].Id);
        Assert.Equal(Path.GetFullPath(validImage), loaded.Items[0].FilePath);
        Assert.Equal("keep-2", loaded.Items[1].Id);
        Assert.Equal(Path.GetFullPath(validVideo), loaded.Items[1].FilePath);
        Assert.Equal("valid2.mp4", loaded.Items[1].DisplayName);
    }

    [Fact]
    public async Task ResetAsync_ClearsLibraryOnDiskToEmptyVersion1()
    {
        var imagePath = CreateTempFile("to_reset.png");
        Assert.True((await service.AddAsync(imagePath)).Success);
        Assert.Single((await service.LoadAsync()).Items);

        Assert.True(await service.ResetAsync());

        var loaded = await service.LoadAsync();
        Assert.Equal(WallpaperLibrary.CurrentVersion, loaded.Version);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task SaveAsync_WhenTargetFileLocked_CleansUpTempFileAndPreservesPreviousLibrary()
    {
        var firstImage = CreateTempFile("first.png");
        var secondVideo = CreateTempFile("second.mp4");

        Assert.True((await service.AddAsync(firstImage)).Success);
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));

        // Lock library.json exclusively so File.Move fails during SaveAsync / AddAsync
        await using (var lockStream = new FileStream(
            libraryFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var failedSave = await service.SaveAsync(WallpaperLibrary.CreateDefault());
            Assert.False(failedSave);

            var failedAdd = await service.AddAsync(secondVideo);
            Assert.False(failedAdd.Success);
        }

        // No orphaned .tmp files left behind
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));

        // Original library content is still intact
        var loaded = await service.LoadAsync();
        Assert.Single(loaded.Items);
        Assert.Equal(Path.GetFullPath(firstImage), loaded.Items[0].FilePath);
    }
}
