using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class ProductizationAndDailyUseTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string libraryFilePath;
    private readonly string settingsFilePath;
    private readonly WallpaperLibraryService libraryService;
    private readonly SettingsService settingsService;

    public ProductizationAndDailyUseTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_ProductizationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        libraryFilePath = Path.Combine(tempDirectory, "library.json");
        settingsFilePath = Path.Combine(tempDirectory, "settings.json");
        libraryService = new WallpaperLibraryService(libraryFilePath);
        settingsService = new SettingsService(settingsFilePath);
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
            // Ignore temp cleanup errors in test teardown
        }
    }

    private string CreateTempFile(string fileName, byte[]? content = null)
    {
        var path = Path.Combine(tempDirectory, fileName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, content ?? [0xFF, 0xD8, 0xFF, 0xE0]);
        return path;
    }

    [Fact]
    public async Task AddBatchAsync_PersistsMultipleItemsAtomically()
    {
        var f1 = CreateTempFile("wp1.png");
        var f2 = CreateTempFile("wp2.jpg");
        var f3 = CreateTempFile("wp3.mp4");

        var result = await libraryService.AddBatchAsync([f1, f2, f3]);

        Assert.True(result.Success);
        Assert.Equal(3, result.AddedCount);
        Assert.Equal(0, result.AlreadyExistedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(3, result.AddedItems.Count);

        // Verify fresh reload from disk confirms atomic persistence
        var freshService = new WallpaperLibraryService(libraryFilePath);
        var library = await freshService.LoadAsync();
        Assert.Equal(3, library.Items.Count);
        Assert.Contains(library.Items, item => string.Equals(item.FilePath, f1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(library.Items, item => string.Equals(item.FilePath, f2, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(library.Items, item => string.Equals(item.FilePath, f3, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddBatchAsync_DeduplicatesAgainstExistingAndWithinBatchCaseInsensitively()
    {
        var f1 = CreateTempFile("unique1.png");
        var f2 = CreateTempFile("unique2.jpg");
        var f3 = CreateTempFile("unique3.mp4");

        // Seed with f1
        await libraryService.AddAsync(f1);

        // Batch contains:
        // - f1 with different casing (already exists)
        // - f2 (new)
        // - f2 with uppercase extension (internal duplicate within batch)
        // - f3 (new)
        var f1DifferentCasing = f1.ToLowerInvariant();
        var f2Duplicate = Path.Combine(Path.GetDirectoryName(f2)!, Path.GetFileNameWithoutExtension(f2) + ".JPG");

        var result = await libraryService.AddBatchAsync([f1DifferentCasing, f2, f2Duplicate, f3]);

        Assert.True(result.Success);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, result.AlreadyExistedCount); // f1 pre-existing, and f2 duplicate
        Assert.Equal(0, result.SkippedCount);

        var freshService = new WallpaperLibraryService(libraryFilePath);
        var library = await freshService.LoadAsync();
        Assert.Equal(3, library.Items.Count); // f1, f2, f3
    }

    [Fact]
    public async Task AddBatchAsync_SkipsUnsupportedExtensionsAndMissingFiles()
    {
        var textFile = CreateTempFile("notes.txt");
        var exeFile = CreateTempFile("setup.exe");
        var missingPath = Path.Combine(tempDirectory, "does_not_exist.png");
        var validFile = CreateTempFile("valid.png");

        var result = await libraryService.AddBatchAsync([textFile, exeFile, missingPath, validFile]);

        Assert.True(result.Success);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(3, result.SkippedCount); // notes.txt, setup.exe, missingPath

        var freshService = new WallpaperLibraryService(libraryFilePath);
        var library = await freshService.LoadAsync();
        Assert.Single(library.Items);
        Assert.Equal(validFile, library.Items[0].FilePath);
    }

    [Fact]
    public async Task AddBatchAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var f1 = CreateTempFile("cancel1.png");
        var f2 = CreateTempFile("cancel2.png");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await libraryService.AddBatchAsync([f1, f2], cancellationToken: cts.Token);
        });

        var library = await libraryService.LoadAsync();
        Assert.Empty(library.Items);
    }

    [Fact]
    public async Task SetFavoriteBatchAsync_UpdatesMultipleFavoritesAtomically()
    {
        var f1 = CreateTempFile("fav1.png");
        var f2 = CreateTempFile("fav2.png");
        var f3 = CreateTempFile("fav3.png");

        var batchResult = await libraryService.AddBatchAsync([f1, f2, f3]);
        var id1 = batchResult.AddedItems[0].Id;
        var id2 = batchResult.AddedItems[1].Id;
        var id3 = batchResult.AddedItems[2].Id;

        var updatedCount = await libraryService.SetFavoriteBatchAsync([id1, id2], isFavorite: true);
        Assert.Equal(2, updatedCount);

        var freshService = new WallpaperLibraryService(libraryFilePath);
        var library = await freshService.LoadAsync();
        Assert.True(library.Items.First(i => i.Id == id1).IsFavorite);
        Assert.True(library.Items.First(i => i.Id == id2).IsFavorite);
        Assert.False(library.Items.First(i => i.Id == id3).IsFavorite);

        // Unfavorite id1
        var unfavoriteCount = await libraryService.SetFavoriteBatchAsync([id1], isFavorite: false);
        Assert.Equal(1, unfavoriteCount);

        library = await freshService.LoadAsync();
        Assert.False(library.Items.First(i => i.Id == id1).IsFavorite);
        Assert.True(library.Items.First(i => i.Id == id2).IsFavorite);
    }

    [Fact]
    public async Task RemoveBatchAsync_RemovesMultipleItemsWithoutDeletingFilesOnDisk()
    {
        var f1 = CreateTempFile("remove1.png");
        var f2 = CreateTempFile("remove2.jpg");

        var batchResult = await libraryService.AddBatchAsync([f1, f2]);
        var id1 = batchResult.AddedItems[0].Id;
        var id2 = batchResult.AddedItems[1].Id;

        var removedCount = await libraryService.RemoveBatchAsync([id1, id2]);
        Assert.Equal(2, removedCount);

        var freshService = new WallpaperLibraryService(libraryFilePath);
        var library = await freshService.LoadAsync();
        Assert.Empty(library.Items);

        // Critical safety constraint: Physical files MUST NOT be deleted!
        Assert.True(File.Exists(f1), "Physical file 1 must remain on disk!");
        Assert.True(File.Exists(f2), "Physical file 2 must remain on disk!");
    }

    [Fact]
    public void WallpaperFolderScanner_ScanNonRecursive_ReturnsMatchingFilesInDirectory()
    {
        var folder = Path.Combine(tempDirectory, "ScanNonRecursiveTest");
        Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "img1.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "img2.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "img3.jpeg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "vid1.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "readme.txt"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "app.exe"), [1, 2, 3]);

        var subDir = Path.Combine(folder, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(subDir, "sub_img.png"), [1, 2, 3]);

        var scanned = WallpaperFolderScanner.Scan(folder, recursive: false);

        Assert.Equal(4, scanned.Count);
        Assert.Contains(scanned, f => Path.GetFileName(f) == "img1.png");
        Assert.Contains(scanned, f => Path.GetFileName(f) == "img2.jpg");
        Assert.Contains(scanned, f => Path.GetFileName(f) == "img3.jpeg");
        Assert.Contains(scanned, f => Path.GetFileName(f) == "vid1.mp4");
        Assert.DoesNotContain(scanned, f => Path.GetFileName(f) == "sub_img.png");
        Assert.DoesNotContain(scanned, f => Path.GetFileName(f) == "readme.txt");
        Assert.DoesNotContain(scanned, f => Path.GetFileName(f) == "app.exe");
    }

    [Fact]
    public void WallpaperFolderScanner_ScanRecursive_RespectsMaxDepthAndMaxFiles()
    {
        var root = Path.Combine(tempDirectory, "ScanRecursiveLimits");
        var d1 = Path.Combine(root, "d1");
        var d2 = Path.Combine(d1, "d2");
        var d3 = Path.Combine(d2, "d3");

        Directory.CreateDirectory(d3);

        File.WriteAllBytes(Path.Combine(root, "r.png"), [1]);
        File.WriteAllBytes(Path.Combine(d1, "1.png"), [1]);
        File.WriteAllBytes(Path.Combine(d2, "2.png"), [1]);
        File.WriteAllBytes(Path.Combine(d3, "3.png"), [1]);

        // Max depth 1: root (depth 0) + d1 (depth 1)
        var depth1Files = WallpaperFolderScanner.Scan(root, recursive: true, maxDepth: 1);
        Assert.Equal(2, depth1Files.Count);
        Assert.Contains(depth1Files, f => Path.GetFileName(f) == "r.png");
        Assert.Contains(depth1Files, f => Path.GetFileName(f) == "1.png");

        // Max files 2
        var max2Files = WallpaperFolderScanner.Scan(root, recursive: true, maxFiles: 2);
        Assert.Equal(2, max2Files.Count);
    }

    [Fact]
    public void WallpaperFolderScanner_ScanRecursive_AvoidsDirectoryJunctionLoops()
    {
        var root = Path.Combine(tempDirectory, "ScanJunctionTest");
        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "real.png"), [1, 2, 3]);

        var junctionPath = Path.Combine(root, "junction_loop");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch
        {
            // If junction creation isn't supported, test will still verify scanner robustness
        }

        var scanned = WallpaperFolderScanner.Scan(root, recursive: true);

        // Even with junction loop, it must terminate cleanly and return the single real file
        Assert.Single(scanned);
        Assert.Contains(scanned, f => Path.GetFileName(f) == "real.png");
    }

    [Fact]
    public void WallpaperLibraryController_MultiSelect_Transitions()
    {
        using var controller = new WallpaperLibraryController(libraryService);

        Assert.False(controller.IsSelectionMode);
        Assert.Equal(0, controller.SelectedCount);

        controller.EnterSelectionMode();
        Assert.True(controller.IsSelectionMode);

        controller.ToggleItemSelection("item1");
        controller.ToggleItemSelection("item2");
        Assert.Equal(2, controller.SelectedCount);
        Assert.True(controller.IsItemSelected("item1"));
        Assert.True(controller.IsItemSelected("item2"));
        Assert.False(controller.IsItemSelected("item3"));

        controller.ToggleItemSelection("item1"); // Toggle off
        Assert.Equal(1, controller.SelectedCount);
        Assert.False(controller.IsItemSelected("item1"));

        controller.SelectItemForMultiSelect("item3", true);
        Assert.Equal(2, controller.SelectedCount);
        Assert.True(controller.IsItemSelected("item3"));

        controller.ClearSelection();
        Assert.Equal(0, controller.SelectedCount);

        controller.ExitSelectionMode();
        Assert.False(controller.IsSelectionMode);
        Assert.Equal(0, controller.SelectedCount);
    }

    [Fact]
    public async Task WallpaperLibraryController_BulkRemoveSelectedAsync_UpdatesRotationEligibility()
    {
        var f1 = CreateTempFile("rot1.png");
        var f2 = CreateTempFile("rot2.png");

        using var controller = new WallpaperLibraryController(libraryService);
        var addResult = await controller.AddFilesBatchAsync([f1, f2]);
        Assert.True(addResult.Success);

        // Configure rotation
        await controller.Rotation.UpdateSettingsAsync("All", "Sequential", 30);
        Assert.Equal(2, controller.Rotation.GetStatus().TotalEligibleCount);

        // Select item 1 and bulk remove
        controller.EnterSelectionMode();
        controller.ToggleItemSelection(controller.Items[0].Id);
        Assert.Equal(1, controller.SelectedCount);

        var removeResult = await controller.BulkRemoveSelectedAsync();
        Assert.True(removeResult.Success);
        Assert.Single(controller.Items);
        Assert.Equal(0, controller.SelectedCount);

        // Rotation eligible count must be updated automatically
        Assert.Equal(1, controller.Rotation.GetStatus().TotalEligibleCount);
    }

    [Fact]
    public async Task WallpaperLibraryController_BulkSetFavoriteSelectedAsync_UpdatesRotationEligibility()
    {
        var f1 = CreateTempFile("rot_fav1.png");
        var f2 = CreateTempFile("rot_fav2.png");

        using var controller = new WallpaperLibraryController(libraryService);
        await controller.AddFilesBatchAsync([f1, f2]);

        await controller.Rotation.UpdateSettingsAsync("Favorites", "Sequential", 30);
        Assert.Equal(0, controller.Rotation.GetStatus().TotalEligibleCount);

        // Select item 1 and mark as favorite
        controller.EnterSelectionMode();
        controller.ToggleItemSelection(controller.Items[0].Id);

        var favResult = await controller.BulkSetFavoriteSelectedAsync(isFavorite: true);
        Assert.True(favResult.Success);

        // Rotation in Favorites mode now sees the newly favorited item
        Assert.Equal(1, controller.Rotation.GetStatus().TotalEligibleCount);
    }

    [Fact]
    public async Task WallpaperLibraryController_AddFolderAsync_HandlesMissingAndEmptyFolders()
    {
        using var controller = new WallpaperLibraryController(libraryService);

        // Missing folder
        var missingResult = await controller.AddFolderAsync(Path.Combine(tempDirectory, "non_existent_folder"));
        Assert.False(missingResult.Success);
        Assert.Equal(WallpaperError.FileNotFound, missingResult.Error);

        // Empty folder (no supported wallpapers)
        var emptyFolder = Path.Combine(tempDirectory, "EmptyFolder");
        Directory.CreateDirectory(emptyFolder);
        File.WriteAllText(Path.Combine(emptyFolder, "readme.txt"), "hello");

        var emptyResult = await controller.AddFolderAsync(emptyFolder);
        Assert.False(emptyResult.Success);
        Assert.Equal(WallpaperError.UnsupportedFormat, emptyResult.Error);
        Assert.Equal("No supported images or MP4 videos found in folder.", emptyResult.Message);
    }

    private sealed class FlakyDesktopHost : IDesktopHost
    {
        private int resolveCallCount;
        private readonly int failuresBeforeSuccess;

        public FlakyDesktopHost(int failuresBeforeSuccess)
        {
            this.failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref resolveCallCount);
            if (count <= failuresBeforeSuccess)
            {
                return Task.FromResult(new DesktopHostResult(0, DesktopHostError.WorkerUnavailable));
            }

            return Task.FromResult(new DesktopHostResult(100, DesktopHostError.None));
        }

        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class SimpleTestRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 42;
        public int LoadCallCount { get; private set; }
        public string? LoadedPath { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path)
        {
            LoadCallCount++;
            LoadedPath = path;
            return Task.FromResult(new WallpaperResult());
        }

        public void Show() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task RuntimeHostApp_StartupRestore_BoundedRetryOnDesktopHostUnavailable()
    {
        var imagePath = CreateTempFile("startup_retry.png");

        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = imagePath
        });

        // Fails twice with WorkerWNotFound, succeeds on 3rd attempt
        var flakyHost = new FlakyDesktopHost(failuresBeforeSuccess: 2);
        SimpleTestRenderer? createdRenderer = null;

        using var composition = new RuntimeCompositionRoot(
            desktopHost: flakyHost,
            imageRendererFactory: () =>
            {
                createdRenderer = new SimpleTestRenderer();
                return createdRenderer;
            },
            videoRendererFactory: () => new SimpleTestRenderer(),
            settingsService: settingsService);

        var pipeName = $@"NiceWP.Test.RetryPipe.{Guid.NewGuid():N}";
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);

        app.Start();
        await app.RestoreTask;

        // Startup restore succeeded on the 3rd attempt!
        Assert.NotNull(createdRenderer);
        Assert.Equal(imagePath, createdRenderer.LoadedPath);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
    }
}
