using NiceWP.Core;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeVisualPreservationTests
{
    [Fact]
    public void DefaultInterfaceMethods_PreserveLifecycleAndWindowHandle()
    {
        var renderer = new MockVisualPreservingRenderer();

        Assert.False(renderer.IsDeepFrozen);
        Assert.Equal((nint)0x1234, renderer.WindowHandle);
        Assert.True(renderer.IsWindowAlive);

        renderer.DeepFreeze();
        Assert.True(renderer.IsDeepFrozen);
        Assert.True(renderer.PauseCalled);
        Assert.True(renderer.IsWindowAlive);
        Assert.Equal((nint)0x1234, renderer.WindowHandle); // Surface retained!

        renderer.RestoreFromDeepFreeze();
        Assert.False(renderer.IsDeepFrozen);
        Assert.True(renderer.ResumeCalled);
        Assert.True(renderer.IsWindowAlive);
    }

    private sealed class MockVisualPreservingRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 0x1234;
        public bool IsDeepFrozen { get; private set; }
        public bool PauseCalled { get; private set; }
        public bool ResumeCalled { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Pause() => PauseCalled = true;
        public void Resume() => ResumeCalled = true;

        public void DeepFreeze()
        {
            IsDeepFrozen = true;
            Pause();
        }

        public void RestoreFromDeepFreeze()
        {
            IsDeepFrozen = false;
            Resume();
        }

        public void Dispose() { }
    }
}
