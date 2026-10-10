using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeHostPersistenceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public RuntimeHostPersistenceTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_PersistenceTests_{Guid.NewGuid():N}");
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

    private sealed class TestHost : IDesktopHost
    {
        public bool Valid { get; set; } = true;
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));
        public bool IsValid(nint handle) => Valid;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class ConfigurableTestRenderer : IWallpaperRenderer
    {
        private readonly bool shouldSucceed;
        private readonly bool throwOnDispose;

        public ConfigurableTestRenderer(bool shouldSucceed = true, bool throwOnDispose = false)
        {
            this.shouldSucceed = shouldSucceed;
            this.throwOnDispose = throwOnDispose;
        }

        public nint WindowHandle => 42;
        public int DisposeCount { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) =>
            Task.FromResult(shouldSucceed
                ? new WallpaperResult()
                : new WallpaperResult(WallpaperError.RendererFailed, "Simulated renderer failure."));

        public void Show() { }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("Simulated renderer dispose failure.");
            }
        }
    }

    private RuntimeCompositionRoot CreateComposition(
        SettingsService? customSettings = null,
        bool imageLoadSuccess = true,
        bool videoLoadSuccess = true,
        bool throwOnDispose = false) =>
        new(
            new TestHost(),
            () => new ConfigurableTestRenderer(imageLoadSuccess, throwOnDispose),
            () => new ConfigurableTestRenderer(videoLoadSuccess, throwOnDispose),
            customSettings ?? settingsService);

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task SetImage_Success_PersistsImageTypeAndAbsolutePath()
    {
        var imagePath = CreateTempWallpaperFile("forest.png");
        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var response = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(response.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.True(persisted.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(imagePath), persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task SetVideo_Success_PersistsVideoTypeAndAbsolutePath()
    {
        var videoPath = CreateTempWallpaperFile("ocean.mp4");
        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var response = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(response.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.True(persisted.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, persisted.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(videoPath), persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task StopWallpaper_Success_PersistsNoneAndNullPath()
    {
        var imagePath = CreateTempWallpaperFile("sunset.jpg");
        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var setResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(setResponse.Success);

        var stopResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
        Assert.True(stopResponse.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeNone, persisted.LastWallpaperType);
        Assert.Null(persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task StopWallpaper_Failure_PreservesPreviousPersistedSettings()
    {
        var imagePath = CreateTempWallpaperFile("sticky.png");
        using var composition = CreateComposition(imageLoadSuccess: true, throwOnDispose: true);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var setResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(setResponse.Success);

        var stopResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
        Assert.False(stopResponse.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(imagePath), persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task SetImage_Failure_PreservesPreviousPersistedSettings()
    {
        var initialImage = CreateTempWallpaperFile("initial.png");
        var secondImage = CreateTempWallpaperFile("corrupt.png");

        // First succeed and persist initialImage
        using (var composition1 = CreateComposition(imageLoadSuccess: true))
        using (var app1 = new RuntimeHostApp(composition: composition1, pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}"))
        {
            var initialResult = await app1.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, initialImage));
            Assert.True(initialResult.Success);
        }

        // Now test validation failure (missing file) and renderer failure
        using var composition2 = CreateComposition(imageLoadSuccess: false);
        using var app2 = new RuntimeHostApp(composition: composition2, pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var missingResponse = await app2.HandleCommandAsync(
            new IpcRequest(IpcConstants.SetImageCommand, Path.Combine(tempDirectory, "does_not_exist.png")));
        Assert.False(missingResponse.Success);

        var decodeFailResponse = await app2.HandleCommandAsync(
            new IpcRequest(IpcConstants.SetImageCommand, secondImage));
        Assert.False(decodeFailResponse.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(initialImage), persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task SetVideo_Failure_PreservesPreviousPersistedSettings()
    {
        var initialVideo = CreateTempWallpaperFile("initial.mp4");
        var failingVideo = CreateTempWallpaperFile("broken.mp4");

        using (var composition1 = CreateComposition(videoLoadSuccess: true))
        using (var app1 = new RuntimeHostApp(composition: composition1, pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}"))
        {
            var initialResult = await app1.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, initialVideo));
            Assert.True(initialResult.Success);
        }

        using var composition2 = CreateComposition(videoLoadSuccess: false);
        using var app2 = new RuntimeHostApp(composition: composition2, pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var missingResponse = await app2.HandleCommandAsync(
            new IpcRequest(IpcConstants.SetVideoCommand, Path.Combine(tempDirectory, "missing_video.mp4")));
        Assert.False(missingResponse.Success);

        var decodeFailResponse = await app2.HandleCommandAsync(
            new IpcRequest(IpcConstants.SetVideoCommand, failingVideo));
        Assert.False(decodeFailResponse.Success);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeVideo, persisted.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(initialVideo), persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task SaveFailure_DoesNotFailWallpaperOperationOrRollbackRuntimeState()
    {
        var imagePath = CreateTempWallpaperFile("resilient.png");

        // Force SettingsService.SaveAsync to fail by placing a file where the settings directory should be
        var blockingFile = Path.Combine(tempDirectory, "blocked_dir");
        await File.WriteAllTextAsync(blockingFile, "block");
        var uncreatableSettingsPath = Path.Combine(blockingFile, "settings.json");
        var failingSettingsService = new SettingsService(uncreatableSettingsPath);

        using var composition = CreateComposition(customSettings: failingSettingsService);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var response = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));

        // Wallpaper operation and runtime state must succeed despite persistence failure
        Assert.True(response.Success);
        Assert.Equal(Path.GetFullPath(imagePath), app.Composition.WallpaperService.ActivePath);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(imagePath), app.CurrentState.Path);
    }

    [Fact]
    public async Task Persistence_PreservesExistingRestoreLastWallpaperFlag()
    {
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = false,
            LastWallpaperType = IpcConstants.WallpaperTypeNone,
            LastWallpaperPath = null
        });

        var imagePath = CreateTempWallpaperFile("keep_flag.png");
        var videoPath = CreateTempWallpaperFile("keep_flag.mp4");

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        var imgResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(imgResult.Success);
        var afterImage = await settingsService.LoadAsync();
        Assert.False(afterImage.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, afterImage.LastWallpaperType);

        var vidResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(vidResult.Success);
        var afterVideo = await settingsService.LoadAsync();
        Assert.False(afterVideo.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, afterVideo.LastWallpaperType);

        var stopResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
        Assert.True(stopResult.Success);
        var afterStop = await settingsService.LoadAsync();
        Assert.False(afterStop.RestoreLastWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, afterStop.LastWallpaperType);
        Assert.Null(afterStop.LastWallpaperPath);
    }

    [Fact]
    public async Task RuntimeStateAndPersistedState_RemainConsistentAcrossTransitions()
    {
        var imagePath = CreateTempWallpaperFile("state1.png");
        var videoPath = CreateTempWallpaperFile("state2.mp4");

        using var composition = CreateComposition();
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: $@"NiceWP.Test.Persist.{Guid.NewGuid():N}");

        // 1. SetImage
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath))).Success);
        var persisted1 = await settingsService.LoadAsync();
        Assert.Equal(app.CurrentState.Type, persisted1.LastWallpaperType);
        Assert.Equal(app.CurrentState.Path, persisted1.LastWallpaperPath);

        // 2. SetVideo
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        var persisted2 = await settingsService.LoadAsync();
        Assert.Equal(app.CurrentState.Type, persisted2.LastWallpaperType);
        Assert.Equal(app.CurrentState.Path, persisted2.LastWallpaperPath);

        // 3. StopWallpaper
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand))).Success);
        var persisted3 = await settingsService.LoadAsync();
        Assert.Equal(app.CurrentState.Type, persisted3.LastWallpaperType);
        Assert.Equal(app.CurrentState.Path, persisted3.LastWallpaperPath);
    }
}
