using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Observatory;
using NiceWP.Infrastructure.Settings;
using NiceWP.Platform.Windows;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class EventDrivenRuntimeEfficiencyTests : IDisposable
{
    private readonly string testDir;
    private readonly string imagePath;
    private readonly string videoPath;
    private readonly FakeRuntimeEventNotifier fakeNotifier = new();
    private readonly FakeFullscreenDetector fakeFullscreen = new();
    private readonly FakePowerProvider fakePower = new();
    private readonly List<FakeTestRenderer> createdRenderers = [];
    private readonly FakeTestHost fakeHost = new();

    public EventDrivenRuntimeEfficiencyTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"NiceWP_EffTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        imagePath = Path.Combine(testDir, "test.jpg");
        videoPath = Path.Combine(testDir, "test.mp4");
        File.WriteAllBytes(imagePath, [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllBytes(videoPath, [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70]);
    }

    private RuntimeHostApp CreateTestHostApp(string? pipeName = null)
    {
        var settingsPath = Path.Combine(testDir, "settings.json");
        var settingsService = new SettingsService(settingsPath);

        var composition = new RuntimeCompositionRoot(
            desktopHost: fakeHost,
            imageRendererFactory: () =>
            {
                var r = new FakeTestRenderer("Image");
                createdRenderers.Add(r);
                return r;
            },
            videoRendererFactory: () =>
            {
                var r = new FakeTestRenderer("Video");
                createdRenderers.Add(r);
                return r;
            },
            settingsService: settingsService,
            fullscreenDetector: fakeFullscreen,
            powerStatusProvider: fakePower,
            eventNotifier: fakeNotifier);

        return new RuntimeHostApp(
            composition: composition,
            pipeName: pipeName ?? $@"NiceWP.Test.Eff.{Guid.NewGuid():N}",
            eventNotifier: fakeNotifier,
            fullscreenPollIntervalMs: 50,
            fullscreenFallbackIntervalMs: 200,
            powerPollIntervalMs: 50,
            powerFallbackIntervalMs: 200);
    }

    [Fact]
    public async Task PauseReasons_SessionLocked_PausesAndResumesIdempotently()
    {
        using var app = CreateTestHostApp();
        app.Start();

        var setResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(setResp.Success);
        Assert.True(app.CurrentState.IsRunning);

        // Lock session
        fakeNotifier.RaiseSessionLockChanged(true);
        await Task.Delay(50);

        Assert.True(app.IsSessionLockedPaused);
        Assert.True(app.CurrentState.IsPaused);
        var snapLocked = app.CaptureHostStateSnapshot();
        Assert.Contains("SessionLocked", snapLocked.PauseReasons);
        Assert.True(snapLocked.SessionLockedPauseActive);

        // Unlock session
        fakeNotifier.RaiseSessionLockChanged(false);
        await Task.Delay(50);

        Assert.False(app.IsSessionLockedPaused);
        Assert.True(app.CurrentState.IsRunning);
        var snapUnlocked = app.CaptureHostStateSnapshot();
        Assert.Equal("None", snapUnlocked.PauseReasons);
        Assert.False(snapUnlocked.SessionLockedPauseActive);
    }

    [Fact]
    public async Task PauseReasons_DisplayOff_PausesAndResumesIdempotently()
    {
        using var app = CreateTestHostApp();
        app.Start();

        var setResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(setResp.Success);
        Assert.True(app.CurrentState.IsRunning);

        // Display turns off
        fakeNotifier.RaiseDisplayPowerChanged(false);
        await Task.Delay(50);

        Assert.True(app.IsDisplayOffPaused);
        Assert.True(app.CurrentState.IsPaused);
        var snapOff = app.CaptureHostStateSnapshot();
        Assert.Contains("DisplayOff", snapOff.PauseReasons);
        Assert.True(snapOff.DisplayOffPauseActive);

        // Display turns on
        fakeNotifier.RaiseDisplayPowerChanged(true);
        await Task.Delay(50);

        Assert.False(app.IsDisplayOffPaused);
        Assert.True(app.CurrentState.IsRunning);
        var snapOn = app.CaptureHostStateSnapshot();
        Assert.Equal("None", snapOn.PauseReasons);
        Assert.False(snapOn.DisplayOffPauseActive);
    }

    [Fact]
    public async Task PauseReasons_CombinedReasons_OneReasonEndingDoesNotResume()
    {
        using var app = CreateTestHostApp();
        app.Start();

        var setResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(setResp.Success);

        // 1. Fullscreen becomes active
        fakeFullscreen.IsActive = true;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.IsFullscreenPaused);
        Assert.True(app.CurrentState.IsPaused);

        // 2. Display turns off simultaneously
        fakeNotifier.RaiseDisplayPowerChanged(false);
        await Task.Delay(50);
        Assert.True(app.IsDisplayOffPaused);
        Assert.True(app.CurrentState.IsPaused);

        var snapCombined = app.CaptureHostStateSnapshot();
        Assert.Contains("Fullscreen", snapCombined.PauseReasons);
        Assert.Contains("DisplayOff", snapCombined.PauseReasons);
        Assert.True(snapCombined.FullscreenPauseActive);
        Assert.True(snapCombined.DisplayOffPauseActive);

        // 3. Display turns back on (DisplayOff cleared), but Fullscreen is STILL active
        fakeNotifier.RaiseDisplayPowerChanged(true);
        await Task.Delay(50);

        Assert.False(app.IsDisplayOffPaused);
        Assert.True(app.IsFullscreenPaused);
        // CRUCIAL: Wallpaper must REMAIN paused!
        Assert.True(app.CurrentState.IsPaused);
        var snapStillPaused = app.CaptureHostStateSnapshot();
        Assert.Contains("Fullscreen", snapStillPaused.PauseReasons);
        Assert.DoesNotContain("DisplayOff", snapStillPaused.PauseReasons);

        // 4. Fullscreen exits
        fakeFullscreen.IsActive = false;
        await app.EvaluateFullscreenPolicyAsync();

        Assert.False(app.IsFullscreenPaused);
        Assert.False(app.ShouldBePaused);
        // Now wallpaper resumes
        Assert.True(app.CurrentState.IsRunning);
        Assert.Equal("None", app.CaptureHostStateSnapshot().PauseReasons);
    }

    [Fact]
    public async Task PauseReasons_ManualPause_OverridesAutomaticPolicies()
    {
        using var app = CreateTestHostApp();
        app.Start();

        var setResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(setResp.Success);

        // User clicks manual pause
        var pauseResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand));
        Assert.True(pauseResp.Success);
        Assert.True(app.IsManualPaused);
        Assert.True(app.CurrentState.IsPaused);

        // System triggers session lock and unlock
        fakeNotifier.RaiseSessionLockChanged(true);
        await Task.Delay(50);
        fakeNotifier.RaiseSessionLockChanged(false);
        await Task.Delay(50);

        // System triggers display off and on
        fakeNotifier.RaiseDisplayPowerChanged(false);
        await Task.Delay(50);
        fakeNotifier.RaiseDisplayPowerChanged(true);
        await Task.Delay(50);

        // Fullscreen enters and exits
        fakeFullscreen.IsActive = true;
        await app.EvaluateFullscreenPolicyAsync();
        fakeFullscreen.IsActive = false;
        await app.EvaluateFullscreenPolicyAsync();

        // Wallpaper must STILL be paused by Manual pause
        Assert.True(app.IsManualPaused);
        Assert.True(app.CurrentState.IsPaused);
        var snap = app.CaptureHostStateSnapshot();
        Assert.Contains("Manual", snap.PauseReasons);

        // User explicitly resumes
        var resumeResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.ResumeWallpaperCommand));
        Assert.True(resumeResp.Success);
        Assert.False(app.IsManualPaused);
        Assert.True(app.CurrentState.IsRunning);
    }

    [Fact]
    public async Task EventDriven_ForegroundEvent_TriggersEvaluationImmediately()
    {
        using var app = CreateTestHostApp();
        app.Start();

        await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(app.CurrentState.IsRunning);

        fakeFullscreen.IsActive = true;
        fakeNotifier.RaiseForegroundWindowChanged();
        await Task.Delay(100);

        Assert.True(app.IsFullscreenPaused);
        Assert.True(app.CurrentState.IsPaused);

        fakeFullscreen.IsActive = false;
        fakeNotifier.RaiseForegroundWindowChanged();
        await Task.Delay(100);

        Assert.False(app.IsFullscreenPaused);
        Assert.True(app.CurrentState.IsRunning);
    }

    [Fact]
    public async Task EventDriven_PowerStatusChanged_TriggersEvaluationImmediately()
    {
        using var app = CreateTestHostApp();
        app.Start();

        await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
        Assert.True(app.CurrentState.IsRunning);

        // Simulate on-battery low power
        fakePower.Status = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 10, IsBatterySaverActive: false);
        fakeNotifier.RaisePowerStatusChanged();
        await Task.Delay(100);

        Assert.True(app.IsPowerPaused);
        Assert.True(app.CurrentState.IsPaused);

        // Power restored
        fakePower.Status = PowerStatus.DesktopDefault;
        fakeNotifier.RaisePowerStatusChanged();
        await Task.Delay(100);

        Assert.False(app.IsPowerPaused);
        Assert.True(app.CurrentState.IsRunning);
    }

    [Fact]
    public void EventNotifier_IdempotentDisposal_DoesNotThrow()
    {
        using var notifier = new NullRuntimeEventNotifier();
        notifier.Dispose();
        notifier.Dispose(); // second call is safe

        using var fake = new FakeRuntimeEventNotifier();
        fake.Dispose();
        fake.Dispose();
    }

    [Fact]
    public void MultiRateSampler_CachesKernelMetricsAcrossFastIntervals()
    {
        using var sampler = new RuntimePerformanceSampler(
            kernelMetricsInterval: 3,
            stateProvider: () => RuntimeHostStateSnapshot.Empty);

        Assert.Equal(3, sampler.KernelMetricsInterval);

        // Snapshot 0 (primes cached metrics)
        var snap0 = sampler.TakeSnapshot();
        Assert.NotNull(snap0);
        var initialThreads = snap0.ThreadCount;
        var initialHandles = snap0.HandleCount;
        Assert.True(initialThreads > 0);
        Assert.True(initialHandles > 0);

        // Snapshot 1 (should reuse cached metrics)
        var snap1 = sampler.TakeSnapshot();
        Assert.Equal(initialThreads, snap1.ThreadCount);
        Assert.Equal(initialHandles, snap1.HandleCount);

        // Snapshot 2 (should reuse cached metrics)
        var snap2 = sampler.TakeSnapshot();
        Assert.Equal(initialThreads, snap2.ThreadCount);
        Assert.Equal(initialHandles, snap2.HandleCount);

        // Snapshot 3 (sample count 3 is multiple of 3 -> refresh)
        var snap3 = sampler.TakeSnapshot();
        Assert.True(snap3.ThreadCount > 0);
        Assert.True(snap3.HandleCount > 0);

        // Ring buffer calculation remains completely sound
        var status = sampler.GetStatus();
        Assert.Equal(4, status.Stats.SampleCount);
        Assert.True(status.Stats.PeakThreadCount > 0);
        Assert.True(status.Stats.PeakHandleCount > 0);
    }

    [Fact]
    public async Task Stability_TenCycleImageVideoReplacement_ReleasesAllPreviousRenderers()
    {
        using var app = CreateTestHostApp();
        app.Start();

        const int cycles = 10;
        for (var i = 0; i < cycles; i++)
        {
            var imgResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
            Assert.True(imgResp.Success);

            var vidResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath));
            Assert.True(vidResp.Success);
        }

        // In 10 cycles of (Image then Video), total 20 renderers were created
        Assert.Equal(20, createdRenderers.Count);

        // 19 renderers must be disposed, exactly 1 active renderer
        var disposedCount = createdRenderers.Count(r => r.IsDisposed);
        var aliveCount = createdRenderers.Count(r => !r.IsDisposed);
        Assert.Equal(19, disposedCount);
        Assert.Equal(1, aliveCount);

        // Now stop wallpaper
        var stopResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
        Assert.True(stopResp.Success);

        // All 20 renderers are now cleanly disposed (0 leak)
        Assert.Equal(20, createdRenderers.Count(r => r.IsDisposed));
    }

    [Fact]
    public async Task Stability_RepeatedStopCalls_AreSafeAndIdempotent()
    {
        using var app = CreateTestHostApp();
        app.Start();

        await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath));
        Assert.True(app.CurrentState.HasActiveWallpaper);

        for (var i = 0; i < 5; i++)
        {
            var stopResp = await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand));
            Assert.True(stopResp.Success);
            Assert.False(app.CurrentState.HasActiveWallpaper);
        }
    }

    [Fact]
    public void Serialization_NewPolicyStates_SerializedCorrectlyInReport()
    {
        var snap = new RuntimePerformanceSnapshot
        {
            WallpaperType = IpcConstants.WallpaperTypeVideo,
            WallpaperState = IpcConstants.PlaybackStatePaused,
            PauseReasons = "SessionLocked, DisplayOff",
            SessionLockedPauseActive = true,
            DisplayOffPauseActive = true,
            FullscreenPauseActive = false,
            PowerPauseActive = false,
            ManualPauseActive = false
        };

        var status = new RuntimePerformanceStatus
        {
            Snapshot = snap,
            Stats = new RuntimePerformanceStats()
        };

        var report = PerformanceReportGenerator.GenerateTextReport(status, "0.0.12");

        Assert.Contains("Session Lock Policy : Active (Paused due to locked session)", report);
        Assert.Contains("Display Power Policy: Active (Paused due to display sleep/off)", report);
        Assert.Contains("Active Pause Reasons: SessionLocked, DisplayOff", report);
        Assert.Contains("Fullscreen Policy   : Inactive", report);
    }

    [Fact]
    public void RuntimePerformanceSnapshot_IsPausedAndIsRunning_EvaluateCorrectly()
    {
        var noneSnap = new RuntimePerformanceSnapshot
        {
            WallpaperType = IpcConstants.WallpaperTypeNone,
            WallpaperState = IpcConstants.PlaybackStateNone
        };
        Assert.False(noneSnap.HasActiveWallpaper);
        Assert.False(noneSnap.IsPaused);
        Assert.False(noneSnap.IsRunning);

        var runningSnap = new RuntimePerformanceSnapshot
        {
            WallpaperType = IpcConstants.WallpaperTypeImage,
            WallpaperState = IpcConstants.PlaybackStateRunning
        };
        Assert.True(runningSnap.HasActiveWallpaper);
        Assert.False(runningSnap.IsPaused);
        Assert.True(runningSnap.IsRunning);

        var pausedSnap = new RuntimePerformanceSnapshot
        {
            WallpaperType = IpcConstants.WallpaperTypeVideo,
            WallpaperState = IpcConstants.PlaybackStatePaused
        };
        Assert.True(pausedSnap.HasActiveWallpaper);
        Assert.True(pausedSnap.IsPaused);
        Assert.False(pausedSnap.IsRunning);
    }

    public void Dispose()
    {
        fakeNotifier.Dispose();
        try
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
            // Clean test teardown
        }
    }

    private sealed class FakeRuntimeEventNotifier : IRuntimeEventNotifier
    {
        public event EventHandler? ForegroundWindowChanged;
        public event Action<bool>? SessionLockChanged;
        public event Action<bool>? DisplayPowerChanged;
        public event EventHandler? PowerStatusChanged;

        public bool IsForegroundHookActive { get; set; } = true;
        public bool IsSessionNotificationActive { get; set; } = true;
        public bool IsDisplayNotificationActive { get; set; } = true;
        public bool IsPowerNotificationActive { get; set; } = true;

        public void RaiseForegroundWindowChanged() => ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseSessionLockChanged(bool isLocked) => SessionLockChanged?.Invoke(isLocked);
        public void RaiseDisplayPowerChanged(bool isDisplayOn) => DisplayPowerChanged?.Invoke(isDisplayOn);
        public void RaisePowerStatusChanged() => PowerStatusChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    private sealed class FakeFullscreenDetector : IFullscreenWindowDetector
    {
        public bool IsActive { get; set; }
        public bool IsFullscreenWindowActive() => IsActive;
    }

    private sealed class FakePowerProvider : IPowerStatusProvider
    {
        public PowerStatus Status { get; set; } = PowerStatus.DesktopDefault;
        public PowerStatus GetPowerStatus() => Status;
    }

    private sealed class FakeTestRenderer : IWallpaperRenderer
    {
        public string Kind { get; }
        public nint WindowHandle { get; set; } = 100;
        public bool IsDisposed { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsShown { get; private set; }

        public FakeTestRenderer(string kind)
        {
            Kind = kind;
        }

        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() => IsShown = true;
        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;
        public void Dispose()
        {
            IsDisposed = true;
            WindowHandle = 0;
        }
    }

    private sealed class FakeTestHost : IDesktopHost
    {
        public nint HostHandle { get; set; } = 10;
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(HostHandle, DesktopHostError.None));
        public bool IsValid(nint handle) => handle != 0;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }
}
