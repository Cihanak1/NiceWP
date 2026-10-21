using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Observatory;
using NiceWP.Infrastructure.Settings;
using NiceWP.Platform.Windows;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class AdaptiveEfficiencyAndGuardrailsTests : IDisposable
{
    private readonly string testDir;
    private readonly string imagePath;
    private readonly string videoPath;
    private readonly FakeRuntimeEventNotifier fakeNotifier = new();
    private readonly FakeFullscreenDetector fakeFullscreen = new();
    private readonly FakePowerProvider fakePower = new();
    private readonly List<FakeTestRenderer> createdRenderers = [];
    private readonly FakeTestHost fakeHost = new();

    public AdaptiveEfficiencyAndGuardrailsTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"NiceWP_AdaptiveTest_{Guid.NewGuid():N}");
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
            pipeName: pipeName ?? $@"NiceWP.Test.Adaptive.{Guid.NewGuid():N}",
            eventNotifier: fakeNotifier,
            fullscreenDetector: fakeFullscreen,
            powerStatusProvider: fakePower,
            fullscreenPollIntervalMs: 50,
            fullscreenFallbackIntervalMs: 50,
            powerPollIntervalMs: 50,
            powerFallbackIntervalMs: 50);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void EfficiencyProfile_Parsing_SafeFallbacks()
    {
        Assert.Equal(EfficiencyProfile.Balanced, EfficiencyPolicyMatrix.ParseProfile(null));
        Assert.Equal(EfficiencyProfile.Balanced, EfficiencyPolicyMatrix.ParseProfile(""));
        Assert.Equal(EfficiencyProfile.Balanced, EfficiencyPolicyMatrix.ParseProfile("Invalid"));
        Assert.Equal(EfficiencyProfile.Balanced, EfficiencyPolicyMatrix.ParseProfile("balanced"));
        Assert.Equal(EfficiencyProfile.Eco, EfficiencyPolicyMatrix.ParseProfile("eco"));
        Assert.Equal(EfficiencyProfile.Eco, EfficiencyPolicyMatrix.ParseProfile("Eco"));
        Assert.Equal(EfficiencyProfile.Unrestricted, EfficiencyPolicyMatrix.ParseProfile("unrestricted"));
        Assert.Equal(EfficiencyProfile.Unrestricted, EfficiencyPolicyMatrix.ParseProfile("Unrestricted"));

        Assert.Equal("Balanced", EfficiencyProfile.Balanced.ToDisplayString());
        Assert.Equal("Eco", EfficiencyProfile.Eco.ToDisplayString());
        Assert.Equal("Unrestricted", EfficiencyProfile.Unrestricted.ToDisplayString());
    }

    [Fact]
    public void EfficiencyPolicyMatrix_Fullscreen_Evaluation()
    {
        // Balanced: pauses on fullscreen
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Balanced, true));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Balanced, false));

        // Eco: pauses on fullscreen
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Eco, true));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Eco, false));

        // Unrestricted: bypasses fullscreen pause
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Unrestricted, true));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForFullscreen(EfficiencyProfile.Unrestricted, false));
    }

    [Fact]
    public void EfficiencyPolicyMatrix_Power_Evaluation()
    {
        var acHealthy = new PowerStatus(IsAcPowerConnected: true, HasBattery: true, BatteryLifePercent: 100, IsBatterySaverActive: false);
        var batteryNormal = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 80, IsBatterySaverActive: false);
        var batterySaver = new PowerStatus(IsAcPowerConnected: false, HasBattery: true, BatteryLifePercent: 30, IsBatterySaverActive: true);
        var acSaver = new PowerStatus(IsAcPowerConnected: true, HasBattery: true, BatteryLifePercent: 90, IsBatterySaverActive: true);

        // Balanced: pauses only when battery saver is engaged or critical battery
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Balanced, in acHealthy));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Balanced, in batteryNormal));
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Balanced, in batterySaver));
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Balanced, in acSaver));

        // Eco: pauses whenever on battery OR battery saver
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Eco, in acHealthy));
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Eco, in batteryNormal));
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Eco, in batterySaver));
        Assert.True(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Eco, in acSaver));

        // Unrestricted: bypasses power pause
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Unrestricted, in acHealthy));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Unrestricted, in batteryNormal));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Unrestricted, in batterySaver));
        Assert.False(EfficiencyPolicyMatrix.ShouldPauseForPower(EfficiencyProfile.Unrestricted, in acSaver));
    }

    [Fact]
    public void EfficiencyPolicyMatrix_SessionLockAndDisplayOff_AlwaysPausesEvenInUnrestricted()
    {
        // Safety invariant: Screen is locked or display is off -> all profiles must pause
        foreach (var profile in new[] { EfficiencyProfile.Eco, EfficiencyProfile.Balanced, EfficiencyProfile.Unrestricted })
        {
            Assert.True(EfficiencyPolicyMatrix.ShouldPauseForSessionLock(profile, isLocked: true));
            Assert.False(EfficiencyPolicyMatrix.ShouldPauseForSessionLock(profile, isLocked: false));

            Assert.True(EfficiencyPolicyMatrix.ShouldPauseForDisplayOff(profile, isDisplayOff: true));
            Assert.False(EfficiencyPolicyMatrix.ShouldPauseForDisplayOff(profile, isDisplayOff: false));
        }
    }

    [Fact]
    public void RuntimeHealthTracker_CircuitBreaker_TripsAtThreeFailures_AndResetsOnManualApply()
    {
        var tracker = new RuntimeHealthTracker();
        Assert.False(tracker.IsCircuitBreakerTripped);
        Assert.Equal(0, tracker.ConsecutiveFailures);

        var (status0, _) = tracker.EvaluateHealth(hasActiveWallpaper: false, desktopHostConnected: false, rendererWindowAlive: false);
        Assert.Equal(RuntimeHealthStatus.Healthy, status0);

        // Failure 1
        var tripped1 = tracker.RecordFailure("Error 1");
        Assert.False(tripped1);
        Assert.False(tracker.IsCircuitBreakerTripped);
        Assert.Equal(1, tracker.ConsecutiveFailures);

        // Failure 2
        var tripped2 = tracker.RecordFailure("Error 2");
        Assert.False(tripped2);
        Assert.False(tracker.IsCircuitBreakerTripped);
        Assert.Equal(2, tracker.ConsecutiveFailures);

        // Failure 3 -> Trips breaker!
        var tripped3 = tracker.RecordFailure("Error 3");
        Assert.True(tripped3);
        Assert.True(tracker.IsCircuitBreakerTripped);
        Assert.Equal(3, tracker.ConsecutiveFailures);

        var (statusTripped, detail) = tracker.EvaluateHealth(hasActiveWallpaper: true, desktopHostConnected: true, rendererWindowAlive: true);
        Assert.Equal(RuntimeHealthStatus.SafeStopped, statusTripped);
        Assert.Contains("Circuit breaker tripped", detail);

        var action = tracker.ComputeCurrentAction(
            isCircuitBreakerTripped: true,
            hasActiveWallpaper: true,
            isPaused: false,
            isManualPaused: false,
            isFullscreenPaused: false,
            isPowerPaused: false,
            isSessionLockedPaused: false,
            isDisplayOffPaused: false,
            wallpaperType: "Video");
        Assert.Contains("Safe Stopped", action);

        // Manual apply resets circuit breaker
        tracker.ResetCircuitBreaker();
        Assert.False(tracker.IsCircuitBreakerTripped);
        Assert.Equal(0, tracker.ConsecutiveFailures);

        var (statusReset, _) = tracker.EvaluateHealth(hasActiveWallpaper: true, desktopHostConnected: true, rendererWindowAlive: true);
        Assert.Equal(RuntimeHealthStatus.Healthy, statusReset);
    }

    [Fact]
    public void RuntimeHealthTracker_RecordSuccess_ClearsConsecutiveFailures()
    {
        var tracker = new RuntimeHealthTracker();
        tracker.RecordFailure("Failure 1");
        tracker.RecordFailure("Failure 2");
        Assert.Equal(2, tracker.ConsecutiveFailures);

        tracker.RecordSuccess();
        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.IsCircuitBreakerTripped);
    }

    [Fact]
    public void RuntimeAuditTrail_RingBuffer_BoundedCapacityAndOrdering()
    {
        var trail = new RuntimeAuditTrail(capacity: 50);
        Assert.Equal(0, trail.Count);

        for (var i = 1; i <= 65; i++)
        {
            trail.Record($"Event_{i}", $"Message {i}");
        }

        // Bounded capacity invariant: never exceeds 50
        Assert.Equal(50, trail.Count);

        var recent10 = trail.GetRecentEvents(10);
        Assert.Equal(10, recent10.Count);
        // Latest events should be 56..65 in chronologically ascending order
        Assert.Equal("Event_56", recent10[0].EventType);
        Assert.Equal("Event_65", recent10[9].EventType);

        var all50 = trail.GetRecentEvents(100);
        Assert.Equal(50, all50.Count);
        Assert.Equal("Event_16", all50[0].EventType);
        Assert.Equal("Event_65", all50[49].EventType);

        trail.Clear();
        Assert.Equal(0, trail.Count);
    }

    [Fact]
    public void RuntimePerformanceSampler_AdaptiveSamplingTransition()
    {
        var sampler = new RuntimePerformanceSampler(
            stateProvider: () => new RuntimeHostStateSnapshot(
                WallpaperType: "None",
                WallpaperState: "None",
                PauseReasons: "None",
                ActiveWallpaperPath: null,
                DesktopHostConnected: false,
                RendererWindowAlive: false,
                SourceWidth: null,
                SourceHeight: null,
                DecodeWidth: null,
                DecodeHeight: null,
                ApproximateDecodedMemoryBytes: null,
                VideoWidth: null,
                VideoHeight: null,
                PlaybackStatus: null,
                PlaybackPosition: null,
                PlaybackDuration: null,
                FullscreenPauseActive: false,
                PowerPauseActive: false,
                ManualPauseActive: false,
                SessionLockedPauseActive: false,
                DisplayOffPauseActive: false,
                EfficiencyProfile: "Balanced",
                RuntimeHealth: "Healthy",
                CurrentAction: "Idle",
                RecentEvents: []));

        // When created before any UI query, sampler is in background rate (5s)
        Assert.False(sampler.IsForegroundActive);
        Assert.Equal(RuntimePerformanceSampler.DefaultBackgroundSampleIntervalMs, sampler.CurrentEffectiveSampleIntervalMs);

        // Take snapshot to populate state provider values
        sampler.TakeSnapshot();

        // Calling GetStatus() indicates active UI monitoring and elevates to foreground rate (1s)
        var status = sampler.GetStatus();
        Assert.NotNull(status);
        Assert.Equal("Balanced", status.Snapshot.EfficiencyProfile);
        Assert.Equal("Healthy", status.Snapshot.RuntimeHealth);
        Assert.Equal("Idle", status.Snapshot.CurrentAction);
        Assert.True(sampler.IsForegroundActive);
        Assert.Equal(RuntimePerformanceSampler.ForegroundSampleIntervalMs, sampler.CurrentEffectiveSampleIntervalMs);

        sampler.Dispose();
    }

    [Fact]
    public async Task RuntimeHostApp_SetEfficiencyProfileCommand_UpdatesProfileAndPersists()
    {
        using var app = CreateTestHostApp();
        app.Start();

        // Default is Balanced
        Assert.Equal(EfficiencyProfile.Balanced, app.CurrentEfficiencyProfile);

        // Switch to Eco via IPC command
        var responseEco = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "test-1",
            Command = IpcConstants.SetEfficiencyProfileCommand,
            Path = "Eco"
        });
        Assert.True(responseEco.Success);
        Assert.Equal(EfficiencyProfile.Eco, app.CurrentEfficiencyProfile);

        // Check settings persisted
        var savedSettings = await app.SettingsService.LoadAsync();
        Assert.Equal("Eco", savedSettings.EfficiencyProfile);

        // Switch to Unrestricted
        var responseUnrestricted = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "test-2",
            Command = IpcConstants.SetEfficiencyProfileCommand,
            Path = "Unrestricted"
        });
        Assert.True(responseUnrestricted.Success);
        Assert.Equal(EfficiencyProfile.Unrestricted, app.CurrentEfficiencyProfile);

        savedSettings = await app.SettingsService.LoadAsync();
        Assert.Equal("Unrestricted", savedSettings.EfficiencyProfile);

        // Switch to invalid -> fallback to Balanced
        var responseInvalid = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "test-3",
            Command = IpcConstants.SetEfficiencyProfileCommand,
            Path = "UnknownGarbage"
        });
        Assert.True(responseInvalid.Success);
        Assert.Equal(EfficiencyProfile.Balanced, app.CurrentEfficiencyProfile);
    }

    [Fact]
    public async Task RuntimeHostApp_AuditLogging_RecordsLifecycleEvents()
    {
        using var app = CreateTestHostApp();
        app.Start();

        // Start Image
        var startRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "start-1",
            Command = IpcConstants.SetImageCommand,
            Path = imagePath
        });
        Assert.True(startRes.Success);

        // Pause
        var pauseRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "pause-1",
            Command = IpcConstants.PauseWallpaperCommand
        });
        Assert.True(pauseRes.Success);

        // Resume
        var resumeRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "resume-1",
            Command = IpcConstants.ResumeWallpaperCommand
        });
        Assert.True(resumeRes.Success);

        // Stop
        var stopRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "stop-1",
            Command = IpcConstants.StopWallpaperCommand
        });
        Assert.True(stopRes.Success);

        var events = app.AuditTrail.GetRecentEvents(20);
        var eventTypes = events.Select(e => e.EventType).ToList();

        Assert.Contains("WallpaperStarted", eventTypes);
        Assert.Contains("WallpaperPaused", eventTypes);
        Assert.Contains("WallpaperResumed", eventTypes);
        Assert.Contains("WallpaperStopped", eventTypes);
    }

    [Fact]
    public async Task RuntimeHostApp_CircuitBreaker_BlocksResumeWhenTripped()
    {
        using var app = CreateTestHostApp();
        app.Start();

        // Artificially trip circuit breaker
        app.HealthTracker.RecordFailure("Fail 1");
        app.HealthTracker.RecordFailure("Fail 2");
        app.HealthTracker.RecordFailure("Fail 3");
        Assert.True(app.HealthTracker.IsCircuitBreakerTripped);

        // Resume should be blocked with circuit breaker error message
        var resumeRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "res-1",
            Command = IpcConstants.ResumeWallpaperCommand
        });
        Assert.False(resumeRes.Success);
        Assert.Contains("circuit breaker is tripped", resumeRes.Message, StringComparison.OrdinalIgnoreCase);

        // Manual apply should reset breaker
        var applyRes = await app.HandleCommandAsync(new IpcRequest
        {
            Id = "apply-1",
            Command = IpcConstants.SetImageCommand,
            Path = imagePath
        });
        Assert.True(applyRes.Success);
        Assert.False(app.HealthTracker.IsCircuitBreakerTripped);
    }

    private sealed class FakeRuntimeEventNotifier : IRuntimeEventNotifier
    {
        public event EventHandler? ForegroundWindowChanged;
        public event Action<string?>? ForegroundProcessChanged;
        public event Action<bool>? SessionLockChanged;
        public event Action<bool>? DisplayPowerChanged;
        public event EventHandler? PowerStatusChanged;

        public bool IsForegroundHookActive { get; set; } = true;
        public bool IsSessionNotificationActive { get; set; } = true;
        public bool IsDisplayNotificationActive { get; set; } = true;
        public bool IsPowerNotificationActive { get; set; } = true;
        public string? CurrentForegroundProcessName { get; set; }

        public void RaiseForegroundWindowChanged() => ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseForegroundProcessChanged(string? processName)
        {
            CurrentForegroundProcessName = processName;
            ForegroundProcessChanged?.Invoke(processName);
        }
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
