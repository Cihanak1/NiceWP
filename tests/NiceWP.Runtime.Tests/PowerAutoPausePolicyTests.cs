using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Platform.Windows;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class PowerAutoPausePolicyTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public PowerAutoPausePolicyTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_PowerPolicyTests_{Guid.NewGuid():N}");
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
        $@"NiceWP.Test.PowerPolicyPipe.{Guid.NewGuid():N}";

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class FakeFullscreenDetector : IFullscreenWindowDetector
    {
        public volatile bool IsFullscreen;
        public bool IsFullscreenWindowActive() => IsFullscreen;
    }

    private sealed class FakePowerStatusProvider : IPowerStatusProvider
    {
        public PowerStatus Status { get; set; } = PowerStatus.DesktopDefault;
        public PowerStatus GetPowerStatus() => Status;
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
        public nint WindowHandle => 42;
        public int PauseCallCount { get; private set; }
        public int ResumeCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Pause() => PauseCallCount++;
        public void Resume() => ResumeCallCount++;
        public void Dispose() => DisposeCallCount++;
    }

    private RuntimeCompositionRoot CreateComposition(
        IFullscreenWindowDetector fullscreenDetector,
        IPowerStatusProvider powerProvider,
        List<TrackingRenderer>? imageRenderers = null,
        List<TrackingRenderer>? videoRenderers = null) =>
        new(
            new FakeDesktopHost(),
            () =>
            {
                var r = new TrackingRenderer();
                imageRenderers?.Add(r);
                return r;
            },
            () =>
            {
                var r = new TrackingRenderer();
                videoRenderers?.Add(r);
                return r;
            },
            settingsService,
            fullscreenDetector,
            powerProvider);

    [Fact]
    public void PowerDetection_NoBattery_ReturnsPauseFalse()
    {
        var status = WindowsPowerStatusProvider.FromNative(
            acLineStatus: 1,
            batteryFlag: 0x80, // No system battery
            batteryLifePercent: 255,
            systemStatusFlag: 0);

        Assert.False(status.HasBattery);
        Assert.False(status.ShouldPauseWallpaper());

        var provider = new WindowsPowerStatusProvider(() => status);
        Assert.False(provider.GetPowerStatus().ShouldPauseWallpaper());
    }

    [Fact]
    public void PowerDetection_AcConnected_EvenWithLowBattery_ReturnsPauseFalse()
    {
        var status10 = WindowsPowerStatusProvider.FromNative(
            acLineStatus: 1, // AC Online
            batteryFlag: 2,  // Low battery
            batteryLifePercent: 10,
            systemStatusFlag: 0);

        Assert.True(status10.HasBattery);
        Assert.True(status10.IsAcPowerConnected);
        Assert.Equal(10, status10.BatteryLifePercent);
        Assert.False(status10.ShouldPauseWallpaper());
    }

    [Fact]
    public void PowerDetection_OnBatteryAt50Percent_ReturnsPauseFalse()
    {
        var status = WindowsPowerStatusProvider.FromNative(
            acLineStatus: 0, // Offline (On Battery)
            batteryFlag: 1,
            batteryLifePercent: 50,
            systemStatusFlag: 0);

        Assert.True(status.HasBattery);
        Assert.False(status.IsAcPowerConnected);
        Assert.Equal(50, status.BatteryLifePercent);
        Assert.False(status.ShouldPauseWallpaper());
    }

    [Theory]
    [InlineData(20)]
    [InlineData(10)]
    public void PowerDetection_OnBatteryAtOrBelowThreshold_ReturnsPauseTrue(byte batteryPercent)
    {
        var status = WindowsPowerStatusProvider.FromNative(
            acLineStatus: 0, // Offline (On Battery)
            batteryFlag: 2,
            batteryLifePercent: batteryPercent,
            systemStatusFlag: 0);

        Assert.True(status.HasBattery);
        Assert.False(status.IsAcPowerConnected);
        Assert.Equal(batteryPercent, status.BatteryLifePercent);
        Assert.True(status.ShouldPauseWallpaper());
    }

    [Fact]
    public void PowerDetection_BatterySaverActive_ReturnsPauseTrue()
    {
        var status = WindowsPowerStatusProvider.FromNative(
            acLineStatus: 0,
            batteryFlag: 1,
            batteryLifePercent: 75,
            systemStatusFlag: 1); // Battery Saver On

        Assert.True(status.HasBattery);
        Assert.True(status.IsBatterySaverActive);
        Assert.True(status.ShouldPauseWallpaper());
    }

    [Fact]
    public async Task Policy_RunningPlusLowBattery_AutoPauses_AndLowBatteryCleared_AutoResumesWithoutWritingSettings()
    {
        var videoPath = CreateTempWallpaperFile("power_pause.mp4");
        var fullscreenDetector = new FakeFullscreenDetector();
        var powerProvider = new FakePowerStatusProvider();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(fullscreenDetector, powerProvider, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);

        var fixedWriteTime = new DateTime(2025, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsFilePath, fixedWriteTime);

        // Running + low battery (15%) -> auto pause
        powerProvider.Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 15, IsBatterySaverActive: false);
        await app.EvaluatePowerPolicyAsync();

        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsPowerPaused);
        Assert.True(app.IsAutoPaused);
        Assert.False(app.IsManualPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));

        // Low battery ends (AC plugged in) -> auto resume
        powerProvider.Status = new PowerStatus(IsAcPowerConnected: true, HasBattery: true, BatteryLifePercent: 15, IsBatterySaverActive: false);
        await app.EvaluatePowerPolicyAsync();

        Assert.True(app.CurrentState.IsRunning);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.IsPowerPaused);
        Assert.False(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].ResumeCallCount);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
    }

    [Fact]
    public async Task Policy_FullscreenAndLowBattery_DoesNotResumeUntilBothClear()
    {
        var videoPath = CreateTempWallpaperFile("combined_policy.mp4");
        var fullscreenDetector = new FakeFullscreenDetector();
        var powerProvider = new FakePowerStatusProvider();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(fullscreenDetector, powerProvider, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);

        // 1. Fullscreen activates -> pause
        fullscreenDetector.IsFullscreen = true;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsFullscreenPaused);
        Assert.False(app.IsPowerPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);

        // 2. Low battery also activates while already paused by fullscreen
        powerProvider.Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 20, IsBatterySaverActive: false);
        await app.EvaluatePowerPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsFullscreenPaused);
        Assert.True(app.IsPowerPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);

        // 3. Fullscreen ends while low battery continues -> MUST NOT resume
        fullscreenDetector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.False(app.IsFullscreenPaused);
        Assert.True(app.IsPowerPaused);
        Assert.Equal(0, videoRenderers[0].ResumeCallCount);

        // 4. Fullscreen re-enters, then low battery ends while fullscreen continues -> MUST NOT resume
        fullscreenDetector.IsFullscreen = true;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.IsFullscreenPaused);

        powerProvider.Status = new PowerStatus(IsAcPowerConnected: true, HasBattery: true, BatteryLifePercent: 50, IsBatterySaverActive: false);
        await app.EvaluatePowerPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsFullscreenPaused);
        Assert.False(app.IsPowerPaused);
        Assert.Equal(0, videoRenderers[0].ResumeCallCount);

        // 5. Both fullscreen and low battery are now cleared -> auto resumes
        fullscreenDetector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsRunning);
        Assert.False(app.IsFullscreenPaused);
        Assert.False(app.IsPowerPaused);
        Assert.False(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].ResumeCallCount);
    }

    [Fact]
    public async Task Policy_ManualPause_WhenPowerConditionEnds_DoesNotAutoResume()
    {
        var videoPath = CreateTempWallpaperFile("manual_power.mp4");
        var fullscreenDetector = new FakeFullscreenDetector();
        var powerProvider = new FakePowerStatusProvider
        {
            Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 10, IsBatterySaverActive: true)
        };
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(fullscreenDetector, powerProvider, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);

        // Manual pause
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsManualPaused);
        Assert.False(app.IsAutoPaused);

        // Power policy evaluates while low battery is active, then power returns to normal
        await app.EvaluatePowerPolicyAsync();
        powerProvider.Status = new PowerStatus(IsAcPowerConnected: true, HasBattery: true, BatteryLifePercent: 85, IsBatterySaverActive: false);
        await app.EvaluatePowerPolicyAsync();

        // Must stay Paused
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsManualPaused);
        Assert.Equal(0, videoRenderers[0].ResumeCallCount);
    }

    [Fact]
    public async Task Policy_ManualResume_WhilePowerConditionStillActive_PolicyPausesAgainOnNextCheck()
    {
        var videoPath = CreateTempWallpaperFile("manual_resume_re_pause.mp4");
        var fullscreenDetector = new FakeFullscreenDetector();
        var powerProvider = new FakePowerStatusProvider
        {
            Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 12, IsBatterySaverActive: false)
        };
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(fullscreenDetector, powerProvider, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        await app.EvaluatePowerPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsPowerPaused);

        // User explicitly sends ResumeWallpaper -> becomes Running and clears manual/auto pause flags
        var resumeResponse = await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand));
        Assert.True(resumeResponse.Success);
        Assert.True(app.CurrentState.IsRunning);
        Assert.False(app.IsManualPaused);
        Assert.False(app.IsPowerPaused);

        // Next power policy check sees low battery is still active -> auto-pauses again
        await app.EvaluatePowerPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsPowerPaused);
        Assert.True(app.IsAutoPaused);
        Assert.Equal(2, videoRenderers[0].PauseCallCount);
    }

    [Fact]
    public async Task Policy_StopWallpaper_ClearsAllPauseReasons_AndShutdownStopsPowerMonitorCleanly()
    {
        var videoPath = CreateTempWallpaperFile("stop_and_shutdown.mp4");
        var fullscreenDetector = new FakeFullscreenDetector { IsFullscreen = true };
        var powerProvider = new FakePowerStatusProvider
        {
            Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 15, IsBatterySaverActive: true)
        };
        using var composition = CreateComposition(fullscreenDetector, powerProvider);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: CreateUniquePipeName(),
            fullscreenPollIntervalMs: 25,
            powerPollIntervalMs: 25);

        app.Start();
        await app.RestoreTask;

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        await WaitForConditionAsync(() => app.CurrentState.IsPaused && app.IsPowerPaused && app.IsFullscreenPaused, timeoutMs: 1500);

        // StopWallpaper clears all pause reasons
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand))).Success);
        Assert.False(app.IsManualPaused);
        Assert.False(app.IsFullscreenPaused);
        Assert.False(app.IsPowerPaused);
        Assert.False(app.IsAutoPaused);
        Assert.False(app.ShouldBePaused);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);

        // Shutdown stops both fullscreen and power monitor tasks cleanly
        app.RequestShutdown();
        var completed = await Task.WhenAll(
            Task.WhenAny(app.MonitorTask, Task.Delay(1000)),
            Task.WhenAny(app.PowerMonitorTask, Task.Delay(1000)));
        Assert.Same(app.MonitorTask, completed[0]);
        Assert.Same(app.PowerMonitorTask, completed[1]);
        Assert.True(app.MonitorTask.IsCompletedSuccessfully);
        Assert.True(app.PowerMonitorTask.IsCompletedSuccessfully);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(15);
        }

        Assert.True(predicate(), "Timed out waiting for expected power policy state transition.");
    }
}
