using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeHostDeepFreezeRestoreTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public RuntimeHostDeepFreezeRestoreTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_RestoreDeepFreeze_{Guid.NewGuid():N}");
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

    private sealed class FakeDesktopHost : IDesktopHost
    {
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));

        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class TrackingRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 0x5555;
        public bool DeepFreezeCalled { get; private set; }
        public bool IsDeepFrozen { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Pause() { }
        public void Resume() { }

        public void DeepFreeze()
        {
            DeepFreezeCalled = true;
            IsDeepFrozen = true;
        }

        public void RestoreFromDeepFreeze()
        {
            IsDeepFrozen = false;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task StartupRestore_WhenPersistedAsDeepFrozen_RestoresDirectlyIntoDeepFreezeState()
    {
        var videoPath = Path.Combine(tempDirectory, "test.mp4");
        File.WriteAllBytes(videoPath, [1, 2, 3, 4]);

        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = videoPath,
            LastWallpaperState = IpcConstants.PlaybackStateDeepFrozen
        });

        var videoRenderers = new List<TrackingRenderer>();
        var desktopHost = new FakeDesktopHost();
        var composition = new RuntimeCompositionRoot(
            desktopHost: desktopHost,
            imageRendererFactory: () => new TrackingRenderer(),
            videoRendererFactory: () =>
            {
                var r = new TrackingRenderer();
                videoRenderers.Add(r);
                return r;
            },
            settingsService: settingsService);

        var pipeName = $@"NiceWP.Test.DFRestorePipe.{Guid.NewGuid():N}";
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
        app.Start();
        await app.RestoreTask;

        Assert.True(app.CurrentState.HasActiveWallpaper);
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.CurrentState.IsDeepFrozen);
        Assert.Equal(IpcConstants.PlaybackStateDeepFrozen, app.CurrentState.PlaybackState);
        Assert.Single(videoRenderers);
        Assert.True(videoRenderers[0].DeepFreezeCalled);
    }
}
