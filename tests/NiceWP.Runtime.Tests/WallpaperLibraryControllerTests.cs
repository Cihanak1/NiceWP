using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class WallpaperLibraryControllerTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string libraryFilePath;
    private readonly string settingsFilePath;
    private readonly WallpaperLibraryService service;
    private readonly SettingsService settingsService;
    private readonly WallpaperLibraryController controller;

    public WallpaperLibraryControllerTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_LibraryControllerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        libraryFilePath = Path.Combine(tempDirectory, "library.json");
        settingsFilePath = Path.Combine(tempDirectory, "settings.json");
        service = new WallpaperLibraryService(libraryFilePath);
        settingsService = new SettingsService(settingsFilePath);
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

    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.LibraryApplyPipe.{Guid.NewGuid():N}";

    private sealed class TestDesktopHost : IDesktopHost
    {
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));
        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class ConfigurableTestRenderer : IWallpaperRenderer
    {
        private readonly bool shouldSucceed;

        public ConfigurableTestRenderer(bool shouldSucceed = true)
        {
            this.shouldSucceed = shouldSucceed;
        }

        public nint WindowHandle => 42;
        public bool IsPaused { get; private set; }
        public int PauseCallCount { get; private set; }
        public int ResumeCallCount { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) =>
            Task.FromResult(shouldSucceed
                ? new WallpaperResult()
                : new WallpaperResult(WallpaperError.RendererFailed, "Simulated renderer failure."));

        public void Show() { }

        public WallpaperResult Pause()
        {
            PauseCallCount++;
            IsPaused = true;
            return new WallpaperResult();
        }

        public WallpaperResult Resume()
        {
            ResumeCallCount++;
            IsPaused = false;
            return new WallpaperResult();
        }

        public void Dispose() { }
    }

    private sealed class ConfigurableFullscreenDetector : IFullscreenWindowDetector
    {
        public bool IsFullscreen { get; set; }
        public bool IsFullscreenWindowActive() => IsFullscreen;
    }

    private sealed class ConfigurablePowerProvider : IPowerStatusProvider
    {
        public PowerStatus Current { get; set; } = PowerStatus.DesktopDefault;
        public PowerStatus GetPowerStatus() => Current;
    }

    private RuntimeHostApp CreateStartedHost(
        string pipeName,
        bool imageLoadSuccess = true,
        bool videoLoadSuccess = true,
        IFullscreenWindowDetector? fullscreenDetector = null,
        IPowerStatusProvider? powerProvider = null,
        int pollIntervalMs = 5000)
    {
        var composition = new RuntimeCompositionRoot(
            new TestDesktopHost(),
            () => new ConfigurableTestRenderer(imageLoadSuccess),
            () => new ConfigurableTestRenderer(videoLoadSuccess),
            settingsService,
            fullscreenDetector ?? new ConfigurableFullscreenDetector(),
            powerProvider ?? new ConfigurablePowerProvider());

        var host = new RuntimeHostApp(
            composition: composition,
            pipeName: pipeName,
            fullscreenDetector: fullscreenDetector ?? composition.FullscreenDetector,
            fullscreenPollIntervalMs: pollIntervalMs,
            powerStatusProvider: powerProvider ?? composition.PowerStatusProvider,
            powerPollIntervalMs: pollIntervalMs);

        host.Start();
        return host;
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
        Assert.True(controller.CanApplySelected);
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
        Assert.True(controller.CanApplySelected);
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
        Assert.True(controller.CanApplySelected);

        File.Delete(imagePath);

        var reloaded = await controller.LoadAsync();
        Assert.Single(reloaded);
        Assert.Equal("ephemeral.png", reloaded[0].DisplayName);
        Assert.Equal(IpcConstants.WallpaperTypeImage, reloaded[0].Type);
        Assert.True(reloaded[0].IsMissing);
        Assert.Equal("Missing", reloaded[0].StatusText);
        Assert.Contains("Missing", reloaded[0].DisplayLabel, StringComparison.Ordinal);
        Assert.False(controller.CanApplySelected);
        Assert.True(controller.CanRemoveSelected);
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
        Assert.False(controller.CanApplySelected);
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
        Assert.False(controller.CanApplySelected);
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

    [Fact]
    public async Task ApplySelectedAsync_WithSelectedImage_SendsSetImage_SyncsStatus_AndPersistsSettingsSeparatelyFromLibrary()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName);
        var ipcClient = new RuntimeIpcClient(pipeName);
        var applyController = new WallpaperLibraryController(service, ipcClient);

        var imagePath = CreateTempFile("library_image.png");
        var addResult = await applyController.AddFileAsync(imagePath);
        Assert.True(addResult.Success);
        Assert.True(applyController.CanApplySelected);

        var libraryJsonBeforeApply = await File.ReadAllTextAsync(libraryFilePath);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.True(applyResult.Success);
        Assert.Equal(WallpaperError.None, applyResult.Error);
        Assert.Equal(IpcConstants.WallpaperTypeImage, applyResult.ActiveWallpaperType);
        Assert.Equal(Path.GetFullPath(imagePath), applyResult.ActiveWallpaperPath);
        Assert.Equal(IpcConstants.PlaybackStateRunning, applyResult.PlaybackState);
        Assert.False(applyResult.IsPaused);

        // Verify Runtime Host state
        Assert.True(host.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, host.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(imagePath), host.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, host.CurrentState.PlaybackState);

        // Verify 0.0.7 settings.json persistence by Runtime Host and separation from library.json
        var savedSettings = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeImage, savedSettings.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(imagePath), savedSettings.LastWallpaperPath);

        var libraryJsonAfterApply = await File.ReadAllTextAsync(libraryFilePath);
        Assert.Equal(libraryJsonBeforeApply, libraryJsonAfterApply);
    }

    [Fact]
    public async Task ApplySelectedAsync_WithSelectedVideo_SendsSetVideo_SyncsStatus_AndInteractsWithPausePolicy()
    {
        var pipeName = CreateUniquePipeName();
        var fullscreenDetector = new ConfigurableFullscreenDetector { IsFullscreen = false };
        using var host = CreateStartedHost(
            pipeName,
            fullscreenDetector: fullscreenDetector,
            pollIntervalMs: 30);

        var ipcClient = new RuntimeIpcClient(pipeName);
        var applyController = new WallpaperLibraryController(service, ipcClient);

        var videoPath = CreateTempFile("library_video.mp4");
        var addResult = await applyController.AddFileAsync(videoPath);
        Assert.True(addResult.Success);
        Assert.True(applyController.CanApplySelected);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.True(applyResult.Success);
        Assert.Equal(WallpaperError.None, applyResult.Error);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, applyResult.ActiveWallpaperType);
        Assert.Equal(Path.GetFullPath(videoPath), applyResult.ActiveWallpaperPath);
        Assert.Equal(IpcConstants.PlaybackStateRunning, applyResult.PlaybackState);
        Assert.False(applyResult.IsPaused);

        // Trigger 0.0.8 fullscreen policy and verify runtime auto-pauses the applied video wallpaper
        fullscreenDetector.IsFullscreen = true;
        await host.EvaluateFullscreenPolicyAsync();
        Assert.Equal(IpcConstants.PlaybackStatePaused, host.CurrentState.PlaybackState);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenSelectedItemFileIsMissing_DoesNotSendRuntimeCommand_PreservesActiveWallpaperAndLibraryItem()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName);
        var ipcClient = new RuntimeIpcClient(pipeName);

        var activeImage = CreateTempFile("currently_active.png");
        var setActive = await ipcClient.SetImageAsync(activeImage);
        Assert.True(setActive.Success);

        var ensureCalled = false;
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                ensureCalled = true;
                return Task.FromResult(IpcResponse.Ok(string.Empty));
            });

        var libraryImage = CreateTempFile("deleted_later.png");
        var added = await applyController.AddFileAsync(libraryImage);
        Assert.True(added.Success);
        Assert.True(applyController.CanApplySelected);

        // Delete the physical file after it was added to the library
        File.Delete(libraryImage);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.FileNotFound, applyResult.Error);
        Assert.Equal("File not found.", applyResult.Message);
        Assert.False(ensureCalled);

        // Active wallpaper on Runtime Host must be untouched
        Assert.True(host.CurrentState.HasActiveWallpaper);
        Assert.Equal(Path.GetFullPath(activeImage), host.CurrentState.Path);

        // Item must remain in library and now be marked as Missing, disabling CanApplySelected
        Assert.Single(applyController.Items);
        Assert.True(applyController.Items[0].IsMissing);
        Assert.False(applyController.CanApplySelected);

        var persistedLibrary = await service.LoadAsync();
        Assert.Single(persistedLibrary.Items);
    }

    [Fact]
    public async Task ApplyItemAsync_WithInvalidType_DoesNotSendRuntimeCommand_AndReturnsUnsupportedFormat()
    {
        var pipeName = CreateUniquePipeName();
        var ipcClient = new RuntimeIpcClient(pipeName);
        var ensureCalled = false;
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                ensureCalled = true;
                return Task.FromResult(IpcResponse.Ok(string.Empty));
            });

        var imagePath = CreateTempFile("valid_file.png");
        var invalidTypeItem = new WallpaperLibraryItem
        {
            Id = "custom-id",
            DisplayName = "valid_file.png",
            FilePath = imagePath,
            Type = "HtmlWidget",
            AddedAtUtc = DateTimeOffset.UtcNow
        };

        var applyResult = await applyController.ApplyItemAsync(invalidTypeItem);

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.UnsupportedFormat, applyResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(applyResult.Message));
        Assert.False(ensureCalled);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenNothingSelected_DoesNotSendRuntimeCommand_AndCanApplySelectedIsFalse()
    {
        var pipeName = CreateUniquePipeName();
        var ipcClient = new RuntimeIpcClient(pipeName);
        var ensureCalled = false;
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                ensureCalled = true;
                return Task.FromResult(IpcResponse.Ok(string.Empty));
            });

        var imagePath = CreateTempFile("some_image.png");
        await applyController.AddFileAsync(imagePath);
        applyController.SelectItem(null);

        Assert.False(applyController.CanApplySelected);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.InvalidPath, applyResult.Error);
        Assert.False(ensureCalled);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenRuntimeHostUnavailable_ReturnsDesktopHostUnavailable_AndPreservesLibrary()
    {
        var pipeName = CreateUniquePipeName();
        var ipcClient = new RuntimeIpcClient(pipeName);
        var launcherWithMissingExe = new RuntimeHostLauncher(
            ipcClient,
            Path.Combine(tempDirectory, "NonExistentHost.exe"));

        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            launcherWithMissingExe);

        var imagePath = CreateTempFile("offline_test.png");
        await applyController.AddFileAsync(imagePath);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.DesktopHostUnavailable, applyResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(applyResult.Message));
        Assert.Single(applyController.Items);

        var persistedLibrary = await service.LoadAsync();
        Assert.Single(persistedLibrary.Items);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenRuntimeRendererFails_PreservesLibraryAndReturnsRendererFailed()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName, imageLoadSuccess: false);
        var ipcClient = new RuntimeIpcClient(pipeName);
        var applyController = new WallpaperLibraryController(service, ipcClient);

        var imagePath = CreateTempFile("failing_render.png");
        await applyController.AddFileAsync(imagePath);
        var librarySnapshotBefore = await File.ReadAllTextAsync(libraryFilePath);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.RendererFailed, applyResult.Error);
        Assert.False(string.IsNullOrWhiteSpace(applyResult.Message));
        Assert.Single(applyController.Items);

        var librarySnapshotAfter = await File.ReadAllTextAsync(libraryFilePath);
        Assert.Equal(librarySnapshotBefore, librarySnapshotAfter);
    }

    [Fact]
    public async Task RemoveSelectedAsync_PreventsApplyingRemovedSelection()
    {
        var pipeName = CreateUniquePipeName();
        var ipcClient = new RuntimeIpcClient(pipeName);
        var ensureCalled = false;
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                ensureCalled = true;
                return Task.FromResult(IpcResponse.Ok(string.Empty));
            });

        var imagePath = CreateTempFile("to_remove.png");
        var addResult = await applyController.AddFileAsync(imagePath);
        Assert.True(addResult.Success);
        var removedId = addResult.Entry!.Id;

        var removeResult = await applyController.RemoveSelectedAsync();
        Assert.True(removeResult.Success);
        Assert.False(applyController.CanApplySelected);

        var applySelectedResult = await applyController.ApplySelectedAsync();
        Assert.False(applySelectedResult.Success);
        Assert.Equal(WallpaperError.InvalidPath, applySelectedResult.Error);
        Assert.False(ensureCalled);

        var applyByIdResult = await applyController.ApplyByIdAsync(removedId);
        Assert.False(applyByIdResult.Success);
        Assert.Equal(WallpaperError.FileNotFound, applyByIdResult.Error);
        Assert.False(ensureCalled);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenAlreadyBusy_BlocksDuplicateApplyAndAdd_AndRestoresStateInFinally()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName);
        var ipcClient = new RuntimeIpcClient(pipeName);

        var ensureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ensureGate = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ensureCallCount = 0;

        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                Interlocked.Increment(ref ensureCallCount);
                ensureEntered.TrySetResult();
                return ensureGate.Task;
            });

        var imagePath = CreateTempFile("busy_test.png");
        var secondImagePath = CreateTempFile("busy_second.png");
        await applyController.AddFileAsync(imagePath);

        Assert.False(applyController.IsBusy);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);

        // Start first Apply which pauses at ensureHostRunningAsync
        var firstApplyTask = applyController.ApplySelectedAsync();
        await ensureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While busy, actions must be locked and duplicate Apply / Add / Remove must be rejected with WallpaperError.Busy
        Assert.True(applyController.IsBusy);
        Assert.False(applyController.CanApplySelected);
        Assert.False(applyController.CanRemoveSelected);

        var duplicateApplyResult = await applyController.ApplySelectedAsync();
        Assert.False(duplicateApplyResult.Success);
        Assert.Equal(WallpaperError.Busy, duplicateApplyResult.Error);
        Assert.Equal(1, Volatile.Read(ref ensureCallCount));

        var duplicateAddResult = await applyController.AddFileAsync(secondImagePath);
        Assert.False(duplicateAddResult.Success);
        Assert.Equal(WallpaperError.Busy, duplicateAddResult.Error);
        Assert.Single(applyController.Items);

        var duplicateRemoveResult = await applyController.RemoveSelectedAsync();
        Assert.False(duplicateRemoveResult.Success);
        Assert.Equal(WallpaperError.Busy, duplicateRemoveResult.Error);
        Assert.Single(applyController.Items);

        // Release the gate and verify the first Apply finishes and unlocks state cleanly
        ensureGate.SetResult(IpcResponse.Ok(string.Empty));
        var firstApplyResult = await firstApplyTask;

        Assert.True(firstApplyResult.Success);
        Assert.False(applyController.IsBusy);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);

        // Subsequent Add now succeeds after busy state unlocked
        var addAfterUnlock = await applyController.AddFileAsync(secondImagePath);
        Assert.True(addAfterUnlock.Success);
        Assert.Equal(2, applyController.Items.Count);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenRuntimeUnavailableOrThrows_UnlocksStateCleanly_HidesTechnicalErrors_AndAllowsRetry()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName);
        var ipcClient = new RuntimeIpcClient(pipeName);

        var mode = 0; // 0 = fail response, 1 = throw exception, 2 = succeed
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                return mode switch
                {
                    0 => Task.FromResult(IpcResponse.Fail("System.ComponentModel.Win32Exception: Technical details at stack")),
                    1 => Task.FromException<IpcResponse>(new InvalidOperationException("Simulated host launch exception")),
                    _ => Task.FromResult(IpcResponse.Ok(string.Empty))
                };
            });

        var imagePath = CreateTempFile("retry_after_offline.png");
        await applyController.AddFileAsync(imagePath);

        // 1. Host returns failure
        var failResult = await applyController.ApplySelectedAsync();
        Assert.False(failResult.Success);
        Assert.Equal(WallpaperError.DesktopHostUnavailable, failResult.Error);
        Assert.Equal("Runtime unavailable.", failResult.Message);
        Assert.False(applyController.IsBusy);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);

        // 2. Host launcher throws exception
        mode = 1;
        var throwResult = await applyController.ApplySelectedAsync();
        Assert.False(throwResult.Success);
        Assert.Equal(WallpaperError.DesktopHostUnavailable, throwResult.Error);
        Assert.Equal("Runtime unavailable.", throwResult.Message);
        Assert.False(applyController.IsBusy);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);

        // 3. Host becomes available -> retry succeeds
        mode = 2;
        var retryResult = await applyController.ApplySelectedAsync();
        Assert.True(retryResult.Success);
        Assert.Equal(WallpaperError.None, retryResult.Error);
        Assert.False(applyController.IsBusy);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);
    }

    [Fact]
    public async Task ApplySelectedAsync_WhenFileDeletedDuringHostStartupRace_MarksItemMissing_DisablesApply_KeepsRemoveEnabled_AndPreservesActiveWallpaper()
    {
        var pipeName = CreateUniquePipeName();
        using var host = CreateStartedHost(pipeName);
        var ipcClient = new RuntimeIpcClient(pipeName);

        var activeImage = CreateTempFile("active_before_race.png");
        var setActive = await ipcClient.SetImageAsync(activeImage);
        Assert.True(setActive.Success);

        var raceImage = CreateTempFile("deleted_during_ensure.png");
        var applyController = new WallpaperLibraryController(
            service,
            ipcClient,
            ensureHostRunningAsync: _ =>
            {
                if (File.Exists(raceImage))
                {
                    File.Delete(raceImage);
                }
                return Task.FromResult(IpcResponse.Ok(string.Empty));
            });

        var added = await applyController.AddFileAsync(raceImage);
        Assert.True(added.Success);
        Assert.False(applyController.Items[0].IsMissing);
        Assert.True(applyController.Items[0].CanApply);
        Assert.True(applyController.Items[0].CanRemove);
        Assert.True(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);

        var applyResult = await applyController.ApplySelectedAsync();

        Assert.False(applyResult.Success);
        Assert.Equal(WallpaperError.FileNotFound, applyResult.Error);
        Assert.Equal("File not found.", applyResult.Message);
        Assert.False(applyController.IsBusy);

        // Active wallpaper on Runtime Host must be untouched
        Assert.True(host.CurrentState.HasActiveWallpaper);
        Assert.Equal(Path.GetFullPath(activeImage), host.CurrentState.Path);

        // Library item must remain in library, marked Missing, with Apply disabled and Remove enabled
        Assert.Single(applyController.Items);
        Assert.True(applyController.Items[0].IsMissing);
        Assert.False(applyController.Items[0].CanApply);
        Assert.True(applyController.Items[0].CanRemove);
        Assert.False(applyController.CanApplySelected);
        Assert.True(applyController.CanRemoveSelected);
    }

    [Fact]
    public async Task WallpaperThumbnailService_WhenExtractorFailsOrThrows_ReturnsNullSafely_AndLeavesSourceFileIntact()
    {
        var cacheDir = Path.Combine(tempDirectory, "thumbs_fail_test");
        var videoPath = CreateTempFile("corrupt_or_locked.mp4");

        var throwingService = new WallpaperThumbnailService(
            cacheDir,
            (_, _, _) => throw new InvalidOperationException("Simulated MediaFoundation failure"));

        var nullFromThrow = await throwingService.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.Null(nullFromThrow);
        Assert.True(File.Exists(videoPath));

        var failingService = new WallpaperThumbnailService(
            cacheDir,
            (_, _, _) => Task.FromResult(false));

        var nullFromFalse = await failingService.GetOrCreateVideoThumbnailAsync(videoPath);
        Assert.Null(nullFromFalse);
        Assert.True(File.Exists(videoPath));
    }

    [Fact]
    public void MapUserStatusMessage_MapsErrorsToConciseMessagesWithoutTechnicalDetails()
    {
        Assert.Equal("Runtime unavailable.", WallpaperLibraryController.MapUserStatusMessage(WallpaperError.DesktopHostUnavailable));
        Assert.Equal("File not found.", WallpaperLibraryController.MapUserStatusMessage(WallpaperError.FileNotFound));
        Assert.Equal("Unsupported wallpaper.", WallpaperLibraryController.MapUserStatusMessage(WallpaperError.UnsupportedFormat));
        Assert.Equal("Failed to apply wallpaper.", WallpaperLibraryController.MapUserStatusMessage(WallpaperError.RendererFailed));
        Assert.Equal("Select a valid wallpaper file.", WallpaperLibraryController.MapUserStatusMessage(
            WallpaperError.InvalidPath,
            "System.ArgumentException: Invalid path\n   at System.IO.Path.GetFullPath(String path)"));
    }
}
