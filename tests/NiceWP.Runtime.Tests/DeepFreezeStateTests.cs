using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Observatory;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DeepFreezeStateTests
{
    [Fact]
    public void RuntimeWallpaperState_DeepFrozen_PropertiesMatch()
    {
        var state = new RuntimeWallpaperState(IpcConstants.WallpaperTypeVideo, @"C:\video.mp4", IpcConstants.PlaybackStateDeepFrozen);

        Assert.True(state.HasActiveWallpaper);
        Assert.True(state.IsPaused);
        Assert.True(state.IsDeepFrozen);
        Assert.False(state.IsRunning);
        Assert.Equal(IpcConstants.PlaybackStateDeepFrozen, state.PlaybackState);
    }

    [Fact]
    public void RuntimeWallpaperState_Transitions_AsDeepFrozenAndAsRunning()
    {
        var running = new RuntimeWallpaperState(IpcConstants.WallpaperTypeVideo, @"C:\video.mp4", IpcConstants.PlaybackStateRunning);
        var frozen = running.AsDeepFrozen();

        Assert.True(frozen.IsDeepFrozen);
        Assert.True(frozen.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStateDeepFrozen, frozen.PlaybackState);

        var resumed = frozen.AsRunning();
        Assert.False(resumed.IsDeepFrozen);
        Assert.True(resumed.IsRunning);
        Assert.Equal(IpcConstants.PlaybackStateRunning, resumed.PlaybackState);
    }

    [Fact]
    public void PerformanceSnapshotFormatter_FormatsDeepFrozenState()
    {
        var enResult = PerformanceSnapshotFormatter.FormatTypeAndState(
            IpcConstants.WallpaperTypeVideo,
            IpcConstants.PlaybackStateDeepFrozen,
            new FakeLoc(EffectiveLanguage.English));

        Assert.Contains("Deep Frozen", enResult);

        var trResult = PerformanceSnapshotFormatter.FormatTypeAndState(
            IpcConstants.WallpaperTypeVideo,
            IpcConstants.PlaybackStateDeepFrozen,
            new FakeLoc(EffectiveLanguage.Turkish));

        Assert.Contains("Derin Donduruldu", trResult);
    }

    private sealed class FakeLoc : ILocalizationService
    {
        public EffectiveLanguage CurrentLanguage { get; }
        public AppLanguage ConfiguredLanguage => AppLanguage.System;
        public event EventHandler<EffectiveLanguage>? LanguageChanged { add { } remove { } }
        public FakeLoc(EffectiveLanguage lang) => CurrentLanguage = lang;
        public string GetString(string key) => key;
        public void SetLanguage(AppLanguage language) { }
    }
}
