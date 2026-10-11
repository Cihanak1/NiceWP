using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeHostRestoreTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public RuntimeHostRestoreTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_RestoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        settingsFilePath = Path.Combine(tempDirectory, "settings.json");
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
            // Ignore temp cleanup errors in tests
        }
    }

    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.RestorePipe.{Guid.NewGuid():N}";

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class ConfigurableDesktopHost : IDesktopHost
    {
        private readonly DesktopHostError resolveError;

        public ConfigurableDesktopHost(DesktopHostError resolveError = DesktopHostError.None)
        {
            this.resolveError = resolveError;
        }

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(resolveError == DesktopHostError.None
                ? new DesktopHostResult(10, DesktopHostError.None)
                : new DesktopHostResult(0, resolveError));

        public bool IsValid(nint handle) => resolveError == DesktopHostError.None && handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class ConfigurableRenderer : IWallpaperRenderer
    {
        private readonly bool shouldSucceed;
        private readonly int loadDelayMs;

        public ConfigurableRenderer(bool shouldSucceed = true, int loadDelayMs = 0)
        {
            this.shouldSucceed = shouldSucceed;
            this.loadDelayMs = loadDelayMs;
        }

        public nint WindowHandle => 42;
        public int LoadCallCount { get; private set; }
        public string? LastLoadedPath { get; private set; }

        public async Task<WallpaperResult> LoadAsync(string path)
        {
            LoadCallCount++;
            LastLoadedPath = path;
            if (loadDelayMs > 0)
            {
                await Task.Delay(loadDelayMs).ConfigureAwait(false);
            }

            return shouldSucceed
                ? new WallpaperResult()
                : new WallpaperResult(WallpaperError.RendererFailed, "Simulated renderer failure.");
        }

        public void Show() { }
        public void Dispose() { }
    }

    private RuntimeCompositionRoot CreateComposition(
        SettingsService? customSettings = null,
        DesktopHostError hostError = DesktopHostError.None,
        bool imageSuccess = true,
        bool videoSuccess = true,
        int loadDelayMs = 0,
        Action<ConfigurableRenderer>? onImageRendererCreated = null,
        Action<ConfigurableRenderer>? onVideoRendererCreated = null) =>
        new(
            new ConfigurableDesktopHost(hostError),
            () =>
            {
                var r = new ConfigurableRenderer(imageSuccess, loadDelayMs);
                onImageRendererCreated?.Invoke(r);
                return r;
            },
            () =>
            {
                var r = new ConfigurableRenderer(videoSuccess, loadDelayMs);
                onVideoRendererCreated?.Invoke(r);
                return r;
            },
            customSettings ?? settingsService);

    [Fact]
    public async Task Start_WhenRestoreLastWallpaperIsFalse_DoesNotRestoreWallpaper()
    {
        var imagePath = CreateTempWallpaperFile("disabled_restore.png");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = false,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = imagePath
        });

        var rendererCreated = false;
        using var composition = CreateComposition(onImageRendererCreated: _ => rendererCreated = true);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.False(rendererCreated);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);
        Assert.Null(app.Composition.WallpaperService.ActivePath);
    }

    [Fact]
    public async Task Start_WhenLastWallpaperTypeIsNone_DoesNotRestoreWallpaper()
    {
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeNone,
            LastWallpaperPath = null
        });

        var rendererCreated = false;
        using var composition = CreateComposition(
            onImageRendererCreated: _ => rendererCreated = true,
            onVideoRendererCreated: _ => rendererCreated = true);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.False(rendererCreated);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);
    }

    [Fact]
    public async Task Start_WithValidPersistedImage_RestoresImageAndDoesNotRewriteSettings()
    {
        var imagePath = CreateTempWallpaperFile("restored_image.png");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = imagePath
        });

        var fixedWriteTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsFilePath, fixedWriteTime);

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(imagePath), app.CurrentState.Path);
        Assert.Equal(Path.GetFullPath(imagePath), app.Composition.WallpaperService.ActivePath);

        // Verify settings.json was NOT rewritten during restore
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
    }

    [Fact]
    public async Task Start_WithValidPersistedVideo_RestoresVideoAndDoesNotRewriteSettings()
    {
        var videoPath = CreateTempWallpaperFile("restored_video.mp4");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = videoPath
        });

        var fixedWriteTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsFilePath, fixedWriteTime);

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(Path.GetFullPath(videoPath), app.Composition.WallpaperService.ActivePath);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
    }

    [Fact]
    public async Task Start_WhenPersistedFileDoesNotExist_LeavesStateNoneAndPreservesSettings()
    {
        var missingPath = Path.Combine(tempDirectory, "deleted_wallpaper.png");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = missingPath
        });

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);

        // Persisted settings must remain untouched on disk
        Assert.True(File.Exists(settingsFilePath));
        var persisted = await settingsService.LoadAsync();
        Assert.True(persisted.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
        Assert.Equal(missingPath, persisted.LastWallpaperPath);
    }

    [Theory]
    [InlineData("Image", "mismatch.mp4")]
    [InlineData("Video", "mismatch.png")]
    [InlineData("Image", "unsupported.txt")]
    public async Task Start_WhenFileTypeDoesNotMatchPersistedWallpaperType_LeavesStateNoneAndPreservesSettings(
        string persistedType,
        string fileName)
    {
        var filePath = CreateTempWallpaperFile(fileName);
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = persistedType,
            LastWallpaperPath = filePath
        });

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        app.Start();
        await app.RestoreTask;

        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(persistedType, persisted.LastWallpaperType);
        Assert.Equal(filePath, persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task Start_WhenRendererOrDesktopHostFails_HostContinuesRunningAndPreservesSettings()
    {
        var imagePath = CreateTempWallpaperFile("corrupt_or_worker_down.png");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = imagePath
        });

        var pipeName = CreateUniquePipeName();
        using var composition = CreateComposition(
            hostError: DesktopHostError.WorkerUnavailable,
            imageSuccess: false);
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);

        app.Start();
        await app.RestoreTask;

        // State must be None and settings must be preserved
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
        Assert.Equal(imagePath, persisted.LastWallpaperPath);

        // IPC server must still be alive and responsive
        var client = new RuntimeIpcClient(pipeName);
        var ping = await client.PingAsync(timeoutMs: 2000);
        Assert.True(ping.Success);

        var status = await client.GetStatusAsync(timeoutMs: 2000);
        Assert.True(status.Success);
        Assert.True(status.IsRunning);
        Assert.False(status.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, status.WallpaperType);
    }

    [Fact]
    public async Task GetStatus_WaitsForPendingRestore_AndReturnsRestoredTypeAndPath()
    {
        var imagePath = CreateTempWallpaperFile("delayed_restore.png");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeImage,
            LastWallpaperPath = imagePath
        });

        var pipeName = CreateUniquePipeName();
        using var composition = CreateComposition(loadDelayMs: 250);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: pipeName,
            restoreWaitTimeoutMs: 2000);

        app.Start();

        var client = new RuntimeIpcClient(pipeName);

        // Ping responds immediately while restore is still loading
        var ping = await client.PingAsync(timeoutMs: 1000);
        Assert.True(ping.Success);

        // GetStatus waits boundedly for the pending restore to complete and returns the restored wallpaper
        var status = await client.GetStatusAsync(timeoutMs: 3000);
        Assert.True(status.Success);
        Assert.True(status.IsRunning);
        Assert.True(status.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, status.WallpaperType);
        Assert.Equal(Path.GetFullPath(imagePath), status.ActivePath);
    }

    [Fact]
    public async Task HostRestart_RestoresPersistedImageAndVideoWallpapers()
    {
        var imagePath = CreateTempWallpaperFile("session_image.jpg");
        var videoPath = CreateTempWallpaperFile("session_video.mp4");

        // 1. Session 1: Set Image wallpaper, then stop/dispose host
        using (var composition1 = CreateComposition())
        using (var host1 = new RuntimeHostApp(composition: composition1, pipeName: CreateUniquePipeName()))
        {
            host1.Start();
            await host1.RestoreTask;

            var setImg = await host1.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
            Assert.True(setImg.Success);
            host1.RequestShutdown();
        }

        // 2. Session 2: New host starts -> automatically restores Image wallpaper, then switches to Video wallpaper
        using (var composition2 = CreateComposition())
        using (var host2 = new RuntimeHostApp(composition: composition2, pipeName: CreateUniquePipeName()))
        {
            host2.Start();

            var statusAfterImageRestore = await host2.HandleCommandAsync(new IpcRequest(IpcConstants.GetStatusCommand));
            Assert.True(statusAfterImageRestore.Success);
            Assert.True(statusAfterImageRestore.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, statusAfterImageRestore.WallpaperType);
            Assert.Equal(Path.GetFullPath(imagePath), statusAfterImageRestore.ActivePath);

            var setVid = await host2.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
            Assert.True(setVid.Success);
            host2.RequestShutdown();
        }

        // 3. Session 3: New host starts -> automatically restores Video wallpaper
        using (var composition3 = CreateComposition())
        using (var host3 = new RuntimeHostApp(composition: composition3, pipeName: CreateUniquePipeName()))
        {
            host3.Start();

            var statusAfterVideoRestore = await host3.HandleCommandAsync(new IpcRequest(IpcConstants.GetStatusCommand));
            Assert.True(statusAfterVideoRestore.Success);
            Assert.True(statusAfterVideoRestore.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, statusAfterVideoRestore.WallpaperType);
            Assert.Equal(Path.GetFullPath(videoPath), statusAfterVideoRestore.ActivePath);
            host3.RequestShutdown();
        }
    }
}
