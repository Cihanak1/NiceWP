using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeWallpaperPauseResumeTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public RuntimeWallpaperPauseResumeTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_PauseResumeTests_{Guid.NewGuid():N}");
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
        $@"NiceWP.Test.PauseResumePipe.{Guid.NewGuid():N}";

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class FakeDesktopHost : IDesktopHost
    {
        public int DetachCallCount { get; private set; }

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));

        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) => DetachCallCount++;
    }

    private sealed class TrackingRenderer : IWallpaperRenderer
    {
        private readonly bool shouldSucceed;
        private readonly int loadDelayMs;

        public TrackingRenderer(bool shouldSucceed = true, int loadDelayMs = 0)
        {
            this.shouldSucceed = shouldSucceed;
            this.loadDelayMs = loadDelayMs;
        }

        public nint WindowHandle => 42;
        public int LoadCallCount { get; private set; }
        public int ShowCallCount { get; private set; }
        public int PauseCallCount { get; private set; }
        public int ResumeCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public bool IsDisposed => DisposeCallCount > 0;
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

        public void Show() => ShowCallCount++;
        public void Pause() => PauseCallCount++;
        public void Resume() => ResumeCallCount++;
        public void Dispose() => DisposeCallCount++;
    }

    private RuntimeCompositionRoot CreateComposition(
        out FakeDesktopHost desktopHost,
        List<TrackingRenderer>? imageRenderers = null,
        List<TrackingRenderer>? videoRenderers = null,
        int loadDelayMs = 0)
    {
        var host = new FakeDesktopHost();
        desktopHost = host;
        return new RuntimeCompositionRoot(
            host,
            () =>
            {
                var r = new TrackingRenderer(loadDelayMs: loadDelayMs);
                imageRenderers?.Add(r);
                return r;
            },
            () =>
            {
                var r = new TrackingRenderer(loadDelayMs: loadDelayMs);
                videoRenderers?.Add(r);
                return r;
            },
            settingsService);
    }

    [Fact]
    public async Task ActiveVideo_PauseAndResume_UpdatesStateAndCallsRendererWithoutDisposing()
    {
        var videoPath = CreateTempWallpaperFile("clip.mp4");
        var videoRenderers = new List<TrackingRenderer>();
        var pipeName = CreateUniquePipeName();
        using var composition = CreateComposition(out var desktopHost, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
        app.Start();
        await app.RestoreTask;

        var client = new RuntimeIpcClient(pipeName);

        var setResult = await client.SetVideoAsync(videoPath);
        Assert.True(setResult.Success);
        Assert.Single(videoRenderers);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);

        // 1. Active video -> PauseWallpaper -> state Paused, renderer Pause called
        var pauseResult = await client.PauseWallpaperAsync();
        Assert.True(pauseResult.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
        Assert.False(videoRenderers[0].IsDisposed);
        Assert.Equal(0, desktopHost.DetachCallCount);

        // 2. Paused video -> ResumeWallpaper -> state Running, renderer Resume called
        var resumeResult = await client.ResumeWallpaperAsync();
        Assert.True(resumeResult.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.Equal(1, videoRenderers[0].ResumeCallCount);
        Assert.False(videoRenderers[0].IsDisposed);
        Assert.Equal(0, desktopHost.DetachCallCount);
    }

    [Fact]
    public async Task ActiveImage_PauseAndResume_UpdatesStateAndKeepsImageRendererUndisposed()
    {
        var imagePath = CreateTempWallpaperFile("photo.png");
        var imageRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(out var desktopHost, imageRenderers: imageRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        var setResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(setResult.Success);
        Assert.Single(imageRenderers);

        // 3. Active image -> PauseWallpaper -> state Paused, renderer not disposed / detached
        var pauseResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        Assert.True(pauseResult.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(imagePath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(1, imageRenderers[0].PauseCallCount);
        Assert.False(imageRenderers[0].IsDisposed);
        Assert.Equal(0, desktopHost.DetachCallCount);

        // 4. Paused image -> ResumeWallpaper -> state Running
        var resumeResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand));
        Assert.True(resumeResult.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(imagePath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.Equal(1, imageRenderers[0].ResumeCallCount);
        Assert.False(imageRenderers[0].IsDisposed);
        Assert.Equal(0, desktopHost.DetachCallCount);
    }

    [Fact]
    public async Task PauseAndResume_WhenNoActiveWallpaper_ReturnsControlledFailureAndKeepsNoneState()
    {
        using var composition = CreateComposition(out _);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        // 5. No active wallpaper -> PauseWallpaper -> controlled failure, state None
        var pauseResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        Assert.False(pauseResponse.Success);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);

        // 6. No active wallpaper -> ResumeWallpaper -> controlled failure, state None
        var resumeResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand));
        Assert.False(resumeResponse.Success);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
    }

    [Fact]
    public async Task PauseWhenAlreadyPaused_AndResumeWhenAlreadyRunning_AreIdempotent()
    {
        var videoPath = CreateTempWallpaperFile("idempotent.mp4");
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(out _, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);

        // 8. Already Running -> ResumeWallpaper -> idempotent / safe
        var resumeWhileRunning = await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand));
        Assert.True(resumeWhileRunning.Success);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.Equal(0, videoRenderers[0].ResumeCallCount);

        // Pause once -> Paused
        var firstPause = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        Assert.True(firstPause.Success);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);

        // 7. Already Paused -> PauseWallpaper -> idempotent / safe
        var secondPause = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        Assert.True(secondPause.Success);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
    }

    [Fact]
    public async Task SetImage_AndSetVideo_WhilePaused_TransitionsNewWallpaperToRunning()
    {
        var firstVideo = CreateTempWallpaperFile("first.mp4");
        var nextImage = CreateTempWallpaperFile("next.png");
        var nextVideo = CreateTempWallpaperFile("next.mp4");
        var imageRenderers = new List<TrackingRenderer>();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(out _, imageRenderers: imageRenderers, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, firstVideo))).Success);
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);

        // 9. Paused -> SetImage -> new image is Running
        var setImageResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, nextImage));
        Assert.True(setImageResult.Success);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(nextImage), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.False(app.Composition.WallpaperService.IsPaused);
        Assert.True(videoRenderers[0].IsDisposed);

        // Pause the image, then 10. Paused -> SetVideo -> new video is Running
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);

        var setVideoResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, nextVideo));
        Assert.True(setVideoResult.Success);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(nextVideo), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.False(app.Composition.WallpaperService.IsPaused);
        Assert.True(imageRenderers[0].IsDisposed);
    }

    [Fact]
    public async Task StopWallpaper_WhilePaused_ResetsStateToNoneAndClearsSettings()
    {
        var videoPath = CreateTempWallpaperFile("stop_while_paused.mp4");
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(out var desktopHost, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);

        // 11. Paused -> StopWallpaper -> state None, settings None/null
        var stopResult = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
        Assert.True(stopResult.Success);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, app.CurrentState.Type);
        Assert.Null(app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);
        Assert.True(videoRenderers[0].IsDisposed);
        Assert.Equal(1, desktopHost.DetachCallCount);

        var persisted = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeNone, persisted.LastWallpaperType);
        Assert.Null(persisted.LastWallpaperPath);
    }

    [Fact]
    public async Task PauseAndResume_DoNotModifySettingsFile()
    {
        var videoPath = CreateTempWallpaperFile("no_persist_on_pause.mp4");
        using var composition = CreateComposition(out _);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);

        var fixedWriteTime = new DateTime(2025, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsFilePath, fixedWriteTime);
        var contentBefore = await File.ReadAllTextAsync(settingsFilePath);

        // 12. PauseWallpaper and ResumeWallpaper do not modify settings.json
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
        Assert.Equal(contentBefore, await File.ReadAllTextAsync(settingsFilePath));

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand))).Success);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
        Assert.Equal(contentBefore, await File.ReadAllTextAsync(settingsFilePath));

        var loaded = await settingsService.LoadAsync();
        Assert.Equal(IpcConstants.WallpaperTypeVideo, loaded.LastWallpaperType);
        Assert.Equal(Path.GetFullPath(videoPath), loaded.LastWallpaperPath);
    }

    [Fact]
    public async Task StartupRestore_StartsWallpaperInRunningState_AndGetStatusReportsPlaybackState()
    {
        var videoPath = CreateTempWallpaperFile("restored_running.mp4");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = videoPath
        });

        var pipeName = CreateUniquePipeName();
        using var composition = CreateComposition(out _);
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);

        // 13. Startup restore starts wallpaper with Running state
        app.Start();
        await app.RestoreTask;

        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.CurrentState.IsPaused);

        // 14. GetStatus returns active wallpaper type/path and pause/running state accurately
        var client = new RuntimeIpcClient(pipeName);
        var runningStatus = await client.GetStatusAsync();
        Assert.True(runningStatus.Success);
        Assert.True(runningStatus.IsRunning);
        Assert.True(runningStatus.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, runningStatus.WallpaperType);
        Assert.Equal(Path.GetFullPath(videoPath), runningStatus.ActivePath);
        Assert.Equal(IpcConstants.PlaybackStateRunning, runningStatus.PlaybackState);
        Assert.False(runningStatus.IsPaused);

        Assert.True((await client.PauseWallpaperAsync()).Success);

        var pausedStatus = await client.GetStatusAsync();
        Assert.True(pausedStatus.Success);
        Assert.True(pausedStatus.IsRunning);
        Assert.True(pausedStatus.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, pausedStatus.WallpaperType);
        Assert.Equal(Path.GetFullPath(videoPath), pausedStatus.ActivePath);
        Assert.Equal(IpcConstants.PlaybackStatePaused, pausedStatus.PlaybackState);
        Assert.True(pausedStatus.IsPaused);

        Assert.True((await client.StopWallpaperAsync()).Success);

        var stoppedStatus = await client.GetStatusAsync();
        Assert.True(stoppedStatus.Success);
        Assert.True(stoppedStatus.IsRunning);
        Assert.False(stoppedStatus.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, stoppedStatus.WallpaperType);
        Assert.Null(stoppedStatus.ActivePath);
        Assert.Equal(IpcConstants.PlaybackStateNone, stoppedStatus.PlaybackState);
        Assert.False(stoppedStatus.IsPaused);
    }

    [Fact]
    public async Task PauseWallpaper_DuringInFlightStartupRestore_WaitsForRestoreAndPausesRestoredWallpaper()
    {
        var videoPath = CreateTempWallpaperFile("slow_restore.mp4");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = videoPath
        });

        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(out _, videoRenderers: videoRenderers, loadDelayMs: 150);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: CreateUniquePipeName(),
            restoreWaitTimeoutMs: 2000);

        app.Start();

        // Immediately send PauseWallpaper while startup restore is in flight
        var pauseResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        await app.RestoreTask;

        Assert.True(pauseResponse.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.CurrentState.IsPaused);
        Assert.Single(videoRenderers);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
    }
}
