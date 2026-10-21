using NiceWP.Core;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeVideoRestoreTests
{
    [Fact]
    public void RestoreFromDeepFreeze_RecoversPlaybackPositionAndResetsFlag()
    {
        var renderer = new PositionTrackingRenderer();
        renderer.CurrentPosition = TimeSpan.FromSeconds(42);

        renderer.DeepFreeze();
        Assert.True(renderer.IsDeepFrozen);
        Assert.True(renderer.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(42), renderer.RecordedFreezePosition);

        renderer.RestoreFromDeepFreeze();
        Assert.False(renderer.IsDeepFrozen);
        Assert.False(renderer.IsPaused);
        Assert.True(renderer.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(42), renderer.RestoredPosition);
    }

    private sealed class PositionTrackingRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 0x9876;
        public bool IsDeepFrozen { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsPlaying { get; private set; } = true;
        public TimeSpan? CurrentPosition { get; set; }
        public TimeSpan? RecordedFreezePosition { get; private set; }
        public TimeSpan? RestoredPosition { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Pause()
        {
            IsPaused = true;
            IsPlaying = false;
        }

        public void Resume()
        {
            IsPaused = false;
            IsPlaying = true;
        }

        public void DeepFreeze()
        {
            RecordedFreezePosition = CurrentPosition;
            IsDeepFrozen = true;
            Pause();
        }

        public void RestoreFromDeepFreeze()
        {
            RestoredPosition = RecordedFreezePosition;
            IsDeepFrozen = false;
            Resume();
        }

        public void Dispose() { }
    }
}
