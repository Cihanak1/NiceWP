using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeDeepFreezePolicyTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public RuntimeDeepFreezePolicyTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_DeepFreezeTests_{Guid.NewGuid():N}");
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
            // Ignore temp cleanup errors
        }
    }

    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.DeepFreezePipe.{Guid.NewGuid():N}";

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class FakeDesktopHost : IDesktopHost
    {
        public int AttachCallCount { get; private set; }
        public int DetachCallCount { get; private set; }

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
        {
            AttachCallCount++;
            return Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));
        }

        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) => DetachCallCount++;
    }

    private sealed class DeepFreezeTrackingRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 0x2222;
        public int PauseCallCount { get; private set; }
        public int ResumeCallCount { get; private set; }
        public int DeepFreezeCallCount { get; private set; }
        public int RestoreCallCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsDeepFrozen { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }

        public void Pause() => PauseCallCount++;
        public void Resume() => ResumeCallCount++;

        public void DeepFreeze()
        {
            DeepFreezeCallCount++;
            IsDeepFrozen = true;
            Pause();
        }

        public void RestoreFromDeepFreeze()
        {
            RestoreCallCount++;
            IsDeepFrozen = false;
            Resume();
        }

        public void Dispose() => IsDisposed = true;
    }

    private RuntimeCompositionRoot CreateComposition(
        out FakeDesktopHost desktopHost,
        List<DeepFreezeTrackingRenderer>? videoRenderers = null)
    {
        desktopHost = new FakeDesktopHost();
        var host = desktopHost;
        return new RuntimeCompositionRoot(
            desktopHost: host,
            imageRendererFactory: () => new DeepFreezeTrackingRenderer(),
            videoRendererFactory: () =>
            {
                var r = new DeepFreezeTrackingRenderer();
                videoRenderers?.Add(r);
                return r;
            },
            settingsService: settingsService);
    }

    [Fact]
    public async Task ActiveVideo_DeepFreezeAndResume_TransitionsStateCorrectly()
    {
        var videoPath = CreateTempWallpaperFile("video.mp4");
        var videoRenderers = new List<DeepFreezeTrackingRenderer>();
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

        // 1. Send DeepFreezeWallpaperCommand
        var freezeResult = await client.DeepFreezeWallpaperAsync();
        Assert.True(freezeResult.Success);
        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.CurrentState.IsDeepFrozen);
        Assert.Equal(IpcConstants.PlaybackStateDeepFrozen, app.CurrentState.PlaybackState);
        Assert.Equal(1, videoRenderers[0].DeepFreezeCallCount);
        Assert.False(videoRenderers[0].IsDisposed);

        // 2. Query Status via IPC
        var status = await client.GetStatusAsync();
        Assert.True(status.Success);
        Assert.True(status.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStateDeepFrozen, status.PlaybackState);

        // 3. Send ResumeWallpaperCommand
        var resumeResult = await client.ResumeWallpaperAsync();
        Assert.True(resumeResult.Success);
        Assert.True(app.CurrentState.IsRunning);
        Assert.False(app.CurrentState.IsDeepFrozen);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.Equal(1, videoRenderers[0].RestoreCallCount);
        Assert.False(videoRenderers[0].IsDisposed);
    }
}
