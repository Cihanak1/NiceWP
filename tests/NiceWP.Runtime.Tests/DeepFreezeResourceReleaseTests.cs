using NiceWP.Core;
using NiceWP.Runtime;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeResourceReleaseTests
{
    [Fact]
    public async Task DeepFreeze_ReleasesResourcesAndFlagsState()
    {
        var renderer = new MockFreezableRenderer();
        var host = new MockDesktopHost();
        var service = new ImageWallpaperService(host, () => renderer);

        // Create a temporary valid image file to start
        var tempImage = Path.Combine(Path.GetTempPath(), $"nice_test_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempImage, CreateValidPngBytes());

        try
        {
            var startRes = await service.StartAsync(tempImage);
            Assert.True(startRes.Success);
            Assert.False(service.IsPaused);
            Assert.False(service.IsDeepFrozen);

            var freezeRes = service.DeepFreeze();
            Assert.True(freezeRes.Success);
            Assert.True(service.IsPaused);
            Assert.True(service.IsDeepFrozen);
            Assert.True(renderer.DeepFreezeCalled);

            var resumeRes = service.Resume();
            Assert.True(resumeRes.Success);
            Assert.False(service.IsPaused);
            Assert.False(service.IsDeepFrozen);
            Assert.True(renderer.RestoreCalled);
        }
        finally
        {
            service.Stop();
            if (File.Exists(tempImage)) File.Delete(tempImage);
        }
    }

    private static byte[] CreateValidPngBytes() =>
        new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82
        };

    private sealed class MockDesktopHost : IDesktopHost
    {
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));

        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class MockFreezableRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 0x5555;
        public bool IsDeepFrozen { get; private set; }
        public bool DeepFreezeCalled { get; private set; }
        public bool RestoreCalled { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Pause() { }
        public void Resume() { }

        public void DeepFreeze()
        {
            IsDeepFrozen = true;
            DeepFreezeCalled = true;
        }

        public void RestoreFromDeepFreeze()
        {
            IsDeepFrozen = false;
            RestoreCalled = true;
        }

        public void Dispose() { }
    }
}
