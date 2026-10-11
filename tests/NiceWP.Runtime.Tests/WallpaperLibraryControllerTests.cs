using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperLibraryControllerTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string libraryFilePath;
    private readonly WallpaperLibraryService service;
    private readonly WallpaperLibraryController controller;

    public WallpaperLibraryControllerTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_LibraryControllerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        libraryFilePath = Path.Combine(tempDirectory, "library.json");
        service = new WallpaperLibraryService(libraryFilePath);
        controller = new WallpaperLibraryController(service);
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
    public async Task LoadAsync_ReturnsPersistedItems_AndRepeatedReloadDoesNotCreateDuplicates()
    {
        var imagePath = CreateTempFile("sunset.png");
        var videoPath = CreateTempFile("loop.mp4");
        await service.AddAsync(imagePath);
        await service.AddAsync(videoPath);

        var firstLoad = await controller.LoadAsync();
        Assert.Equal(2, firstLoad.Count);
        Assert.Equal(2, controller.Items.Count);
        Assert.Equal("sunset.png", controller.Items[0].DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeImage, controller.Items[0].Type);
        Assert.False(controller.Items[0].IsMissing);
        Assert.Equal("loop.mp4", controller.Items[1].DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, controller.Items[1].Type);
        Assert.False(controller.Items[1].IsMissing);

        var secondLoad = await controller.LoadAsync();
        Assert.Equal(2, secondLoad.Count);
        Assert.Equal(2, controller.Items.Count);
    }

    [Fact]
    public async Task AddFileAsync_WithValidImage_AddsItemToListAndSelectsIt()
    {
        var imagePath = CreateTempFile("mountain.jpg");

        var result = await controller.AddFileAsync(imagePath);

        Assert.True(result.Success);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(WallpaperError.None, result.Error);
        Assert.NotNull(result.Entry);
        Assert.Equal("mountain.jpg", result.Entry.DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeImage, result.Entry.Type);
        Assert.False(result.Entry.IsMissing);
        Assert.Single(controller.Items);
        Assert.Equal(result.Entry.Id, controller.SelectedItemId);
        Assert.True(controller.CanRemoveSelected);
    }

    [Fact]
    public async Task AddFileAsync_WithValidVideo_AddsItemToListAndSelectsIt()
    {
        var videoPath = CreateTempFile("ocean.mp4");

        var result = await controller.AddFileAsync(videoPath);

        Assert.True(result.Success);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(WallpaperError.None, result.Error);
        Assert.NotNull(result.Entry);
        Assert.Equal("ocean.mp4", result.Entry.DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, result.Entry.Type);
        Assert.False(result.Entry.IsMissing);
        Assert.Single(controller.Items);
        Assert.Equal(result.Entry.Id, controller.SelectedItemId);
        Assert.True(controller.CanRemoveSelected);
    }

    [Fact]
    public async Task AddFileAsync_WithDuplicatePath_KeepsSingleItemAndSelectsExisting()
    {
        var imagePath = CreateTempFile("forest.png");
        var firstAdd = await controller.AddFileAsync(imagePath);
        Assert.True(firstAdd.Success);
        Assert.NotNull(firstAdd.Entry);

        var secondAdd = await controller.AddFileAsync(imagePath.ToUpperInvariant());

        Assert.True(secondAdd.Success);
        Assert.True(secondAdd.AlreadyExisted);
        Assert.NotNull(secondAdd.Entry);
        Assert.Equal(firstAdd.Entry.Id, secondAdd.Entry.Id);
        Assert.Single(controller.Items);
        Assert.Equal(firstAdd.Entry.Id, controller.SelectedItemId);
    }

    [Fact]
    public async Task LoadAsync_WhenPhysicalFileIsDeleted_PreservesItemAndMarksIsMissingTrue()
    {
        var imagePath = CreateTempFile("ephemeral.png");
        var addResult = await controller.AddFileAsync(imagePath);
        Assert.True(addResult.Success);
        Assert.False(controller.Items[0].IsMissing);

        File.Delete(imagePath);

        var reloaded = await controller.LoadAsync();
        Assert.Single(reloaded);
        Assert.Equal("ephemeral.png", reloaded[0].DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeImage, reloaded[0].Type);
        Assert.True(reloaded[0].IsMissing);
        Assert.Equal("Missing", reloaded[0].StatusText);
        Assert.Contains("Missing", reloaded[0].DisplayLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveSelectedAsync_RemovesItemFromList_ClearSelection_AndLeavesPhysicalFileOnDisk()
    {
        var imagePath = CreateTempFile("keep_on_disk.png");
        var addResult = await controller.AddFileAsync(imagePath);
        Assert.True(addResult.Success);
        Assert.Single(controller.Items);
        Assert.True(controller.CanRemoveSelected);

        var removeResult = await controller.RemoveSelectedAsync();

        Assert.True(removeResult.Success);
        Assert.Equal(WallpaperError.None, removeResult.Error);
        Assert.Empty(controller.Items);
        Assert.Null(controller.SelectedItemId);
        Assert.Null(controller.SelectedItem);
        Assert.False(controller.CanRemoveSelected);
        Assert.True(File.Exists(imagePath));

        var persisted = await service.LoadAsync();
        Assert.Empty(persisted.Items);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WhenNoItemSelected_ReturnsInvalidPathAndPreservesItems()
    {
        var imagePath = CreateTempFile("scene.png");
        await controller.AddFileAsync(imagePath);
        controller.SelectItem(null);

        var removeResult = await controller.RemoveSelectedAsync();

        Assert.False(removeResult.Success);
        Assert.Equal(WallpaperError.InvalidPath, removeResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(removeResult.Message));
        Assert.Single(controller.Items);
        Assert.True(File.Exists(imagePath));
    }

    [Fact]
    public async Task LoadAsync_WhenLibraryJsonIsCorrupt_DoesNotCrashAndReturnsEmptyList()
    {
        await File.WriteAllTextAsync(libraryFilePath, "{corrupt json!!!");

        var items = await controller.LoadAsync();

        Assert.NotNull(items);
        Assert.Empty(items);
        Assert.Empty(controller.Items);
        Assert.Null(controller.SelectedItemId);
        Assert.False(controller.CanRemoveSelected);
    }

    [Fact]
    public async Task AddFileAsync_WhenValidationFails_DoesNotModifyLibraryAndReturnsErrorMessage()
    {
        var existingImage = CreateTempFile("valid.png");
        var added = await controller.AddFileAsync(existingImage);
        Assert.True(added.Success);
        Assert.Single(controller.Items);

        var unsupportedFile = CreateTempFile("notes.txt");
        var unsupportedResult = await controller.AddFileAsync(unsupportedFile);

        Assert.False(unsupportedResult.Success);
        Assert.Equal(WallpaperError.UnsupportedFormat, unsupportedResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(unsupportedResult.Message));
        Assert.Single(controller.Items);
        Assert.Equal("valid.png", controller.Items[0].DisplayName);

        var missingFile = Path.Combine(tempDirectory, "does_not_exist.mp4");
        var missingResult = await controller.AddFileAsync(missingFile);

        Assert.False(missingResult.Success);
        Assert.Equal(WallpaperError.FileNotFound, missingResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(missingResult.Message));
        Assert.Single(controller.Items);
        Assert.Equal("valid.png", controller.Items[0].DisplayName);
    }
}
