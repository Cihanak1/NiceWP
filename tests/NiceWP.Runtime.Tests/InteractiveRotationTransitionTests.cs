using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.Transition;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class InteractiveRotationTransitionTests : IDisposable
{
    private readonly string _tempDir;

    public InteractiveRotationTransitionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NiceWP_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }

    #region 1. Corner Hit-Test

    [Fact]
    public void HotCorner_TopRight_IsInside()
    {
        var inside = HotCornerHitTester.IsPointInTopRightCorner(1910, 10, 1920, 1080, 32.0);
        Assert.True(inside);
    }

    [Fact]
    public void HotCorner_TopLeft_IsOutside()
    {
        var inside = HotCornerHitTester.IsPointInTopRightCorner(10, 10, 1920, 1080, 32.0);
        Assert.False(inside);
    }

    [Fact]
    public void HotCorner_BottomRight_IsOutside()
    {
        var inside = HotCornerHitTester.IsPointInTopRightCorner(1910, 1070, 1920, 1080, 32.0);
        Assert.False(inside);
    }

    [Fact]
    public void HotCorner_Center_IsOutside()
    {
        var inside = HotCornerHitTester.IsPointInTopRightCorner(960, 540, 1920, 1080, 32.0);
        Assert.False(inside);
    }

    [Fact]
    public void HotCorner_32pxBoundary_EdgeCases()
    {
        // Screen width 1920, corner size 32 -> X boundary is 1888, Y boundary is 32
        Assert.True(HotCornerHitTester.IsPointInTopRightCorner(1888, 0, 1920, 1080, 32.0));
        Assert.True(HotCornerHitTester.IsPointInTopRightCorner(1888, 32, 1920, 1080, 32.0));
        Assert.True(HotCornerHitTester.IsPointInTopRightCorner(1920, 32, 1920, 1080, 32.0));

        // Just outside
        Assert.False(HotCornerHitTester.IsPointInTopRightCorner(1887.9, 10, 1920, 1080, 32.0));
        Assert.False(HotCornerHitTester.IsPointInTopRightCorner(1900, 32.1, 1920, 1080, 32.0));
    }

    [Fact]
    public void HotCorner_ProcessPointerMoved_RaisesEvents()
    {
        var detector = new HotCornerDetector(1920, 1080, 32.0);
        var entered = false;
        var exited = false;
        detector.CornerEntered += () => entered = true;
        detector.CornerExited += () => exited = true;

        // Move inside
        var changed1 = detector.ProcessPointerMoved(1910, 10);
        Assert.True(changed1);
        Assert.True(entered);
        Assert.True(detector.IsHovered);

        // Move outside
        var changed2 = detector.ProcessPointerMoved(500, 500);
        Assert.True(changed2);
        Assert.True(exited);
        Assert.False(detector.IsHovered);
    }

    #endregion

    #region 2. Drag Progress Calculation

    [Fact]
    public void DragTracker_DiagonalMovement_ComputesPositiveProgress()
    {
        var tracker = new CornerDragTracker(1920, 0, 1920, 1080, thresholdDistance: 360.0);
        var metrics = tracker.TrackPosition(1820, 100, rawVelocity: 150.0);

        Assert.True(metrics.DiagonalDistance > 0);
        Assert.True(metrics.Progress > 0 && metrics.Progress < 1.0);
        Assert.Equal(150.0, metrics.Velocity);
    }

    [Fact]
    public void DragTracker_ReverseMovement_ClampsToZero()
    {
        var tracker = new CornerDragTracker(1920, 0, 1920, 1080, thresholdDistance: 360.0);
        // Dragging above/right of start position
        var metrics = tracker.TrackPosition(1950, -20);

        Assert.Equal(0.0, metrics.Progress);
        Assert.Equal(0.0, metrics.DiagonalDistance);
    }

    [Fact]
    public void DragTracker_ExceedingThreshold_ClampsToOne()
    {
        var tracker = new CornerDragTracker(1920, 0, 1920, 1080, thresholdDistance: 300.0);
        // Dragging far into bottom-left
        var metrics = tracker.TrackPosition(1400, 500);

        Assert.Equal(1.0, metrics.Progress);
    }

    [Fact]
    public void DragTracker_SensitivityThreshold_ScalesCorrectly()
    {
        Assert.Equal(500.0, TransitionSensitivity.Low.GetDragDistanceThreshold());
        Assert.Equal(360.0, TransitionSensitivity.Normal.GetDragDistanceThreshold());
        Assert.Equal(250.0, TransitionSensitivity.High.GetDragDistanceThreshold());
    }

    [Fact]
    public void DragTracker_FoldGeometry_CalculatesTopAndRightIntersections()
    {
        var tracker = new CornerDragTracker(1920, 0, 1920, 1080, thresholdDistance: 360.0);
        var metrics = tracker.TrackPosition(1800, 120);

        Assert.True(metrics.TopFoldX > 0 && metrics.TopFoldX <= 1920);
        Assert.True(metrics.RightFoldY >= 0 && metrics.RightFoldY <= 1080);
        Assert.True(metrics.FoldAngleDegrees > 0 && metrics.FoldAngleDegrees < 90);
    }

    #endregion

    #region 3. Gesture Lifecycle

    [Fact]
    public async Task GestureLifecycle_ReleaseUnderThreshold_Cancels()
    {
        var (coordinator, captureManager, _) = await CreateTestPipelineAsync();

        var acquired = await captureManager.TryAcquireCaptureAsync(1, 1910, 10);
        Assert.True(acquired);
        Assert.Equal(TransitionState.Dragging, coordinator.State);

        // Small drag (progress < 0.30)
        captureManager.ProcessPointerMoved(1, 1900, 10);
        Assert.False(coordinator.CurrentContext!.CanCommit);

        var success = await captureManager.ProcessPointerReleasedAsync(1);
        Assert.False(success);
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }

    [Fact]
    public async Task GestureLifecycle_ReleaseAtOrAboveThreshold_Commits()
    {
        var (coordinator, captureManager, rotation) = await CreateTestPipelineAsync();

        var acquired = await captureManager.TryAcquireCaptureAsync(1, 1910, 10);
        Assert.True(acquired);

        // Large drag (progress >= 0.30)
        captureManager.ProcessPointerMoved(1, 1600, 300);
        Assert.True(coordinator.CurrentContext!.CanCommit);

        var success = await captureManager.ProcessPointerReleasedAsync(1);
        Assert.True(success);
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }

    [Fact]
    public async Task GestureLifecycle_EscapeKey_CancelsImmediately()
    {
        var (coordinator, captureManager, _) = await CreateTestPipelineAsync();

        await captureManager.TryAcquireCaptureAsync(1, 1910, 10);
        Assert.True(captureManager.HasCapture);

        var handled = await captureManager.HandleKeyDownAsync(0x1B); // VK_ESCAPE
        Assert.True(handled);
        Assert.False(captureManager.HasCapture);
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }

    [Fact]
    public async Task GestureLifecycle_PointerCaptureLost_CancelsSafely()
    {
        var (coordinator, captureManager, _) = await CreateTestPipelineAsync();

        await captureManager.TryAcquireCaptureAsync(1, 1910, 10);
        Assert.True(captureManager.HasCapture);

        await captureManager.HandleCaptureLostAsync(1);
        Assert.False(captureManager.HasCapture);
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }

    #endregion

    #region 4. Preconditions

    [Fact]
    public async Task Preconditions_RotationOff_GestureDisabled()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));

        await rotation.UpdateSettingsAsync("Off", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        Assert.False(coordinator.CanInitiateTransition());
    }

    [Fact]
    public async Task Preconditions_EmptyLibrary_GestureDisabled()
    {
        using var rotation = new WallpaperRotationService(
            getItems: () => Array.Empty<WallpaperLibraryItem>(),
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));

        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        var began = await coordinator.BeginCornerGestureAsync(1910, 10, 1920, 1080);
        Assert.False(began);
        Assert.Equal(TransitionState.Idle, coordinator.State);
    }

    [Fact]
    public async Task Preconditions_SingleItemLibrary_DefinedBehavior()
    {
        var items = CreateSampleItems(1);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));

        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        var began = await coordinator.BeginCornerGestureAsync(1910, 10, 1920, 1080);
        Assert.True(began);
        Assert.NotNull(coordinator.PeekedCandidate);
        Assert.Equal(items[0].Id, coordinator.PeekedCandidate.Id);
    }

    [Fact]
    public void Preconditions_ManualPause_InhibitsGesture()
    {
        var state = new EffectiveDesktopState { EffectiveProfile = AdaptiveDesktopProfile.Normal };
        var permitted = AdaptiveTransitionGate.IsTransitionPermitted(state, isManualPaused: true);

        Assert.False(permitted);
        Assert.Equal("ManualPaused", AdaptiveTransitionGate.GetInhibitionReason(state, isManualPaused: true));
    }

    [Fact]
    public void Preconditions_GamingState_InhibitsGesture()
    {
        var state = new EffectiveDesktopState { EffectiveProfile = AdaptiveDesktopProfile.Gaming };
        var permitted = AdaptiveTransitionGate.IsTransitionPermitted(state);

        Assert.False(permitted);
        Assert.Equal("GamingActive", AdaptiveTransitionGate.GetInhibitionReason(state));
    }

    [Fact]
    public void Preconditions_PresentationState_InhibitsGesture()
    {
        var state = new EffectiveDesktopState { EffectiveProfile = AdaptiveDesktopProfile.Presentation };
        var permitted = AdaptiveTransitionGate.IsTransitionPermitted(state);

        Assert.False(permitted);
        Assert.Equal("PresentationActive", AdaptiveTransitionGate.GetInhibitionReason(state));
    }

    [Fact]
    public void Preconditions_DeepFreeze_InhibitsGesture()
    {
        var state = new EffectiveDesktopState
        {
            EffectiveProfile = AdaptiveDesktopProfile.BatterySaver,
            ShouldDeepFreeze = true
        };
        var permitted = AdaptiveTransitionGate.IsTransitionPermitted(state);

        Assert.False(permitted);
        Assert.Equal("DeepFreezeActive", AdaptiveTransitionGate.GetInhibitionReason(state));
    }

    [Fact]
    public async Task Preconditions_InteractiveSettingsDisabled_InhibitsGesture()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = false });

        Assert.False(coordinator.CanInitiateTransition());
    }

    [Fact]
    public void Preconditions_DisplayOffOrLocked_InhibitsGesture()
    {
        var state = new EffectiveDesktopState();
        var permitted = AdaptiveTransitionGate.IsTransitionPermitted(state, isDisplayOffOrLocked: true);

        Assert.False(permitted);
        Assert.Equal("DisplayOffOrLocked", AdaptiveTransitionGate.GetInhibitionReason(state, isDisplayOffOrLocked: true));
    }

    #endregion

    #region 5. Next Candidate Preview

    [Fact]
    public async Task NextCandidatePreview_PeekNextCandidate_DoesNotAdvanceRotationState()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        var initialId = rotation.CurrentItemId;
        var peek1 = rotation.PeekNextCandidate();
        var peek2 = rotation.PeekNextCandidate();

        Assert.NotNull(peek1);
        Assert.NotNull(peek2);
        Assert.Equal(peek1.Id, peek2.Id);
        Assert.Equal(initialId, rotation.CurrentItemId);
    }

    [Fact]
    public async Task NextCandidatePreview_VideoCandidate_UsesPosterFrame()
    {
        var videoPath = Path.Combine(_tempDir, "video.mp4");
        File.WriteAllText(videoPath, "dummy video data");

        var thumbnailDir = Path.Combine(_tempDir, "thumbnails");
        var thumbnailService = new WallpaperThumbnailService(
            cacheDirectory: thumbnailDir,
            videoFrameExtractor: (src, dest, ct) =>
            {
                File.WriteAllText(dest, "dummy png frame");
                return Task.FromResult(true);
            });

        using var preloadService = new PageTurnPreloadService(thumbnailService);
        var item = new WallpaperLibraryItem
        {
            Id = "v1",
            DisplayName = "Sample Video",
            FilePath = videoPath,
            Type = IpcConstants.WallpaperTypeVideo
        };

        var preloaded = await preloadService.PreloadCandidateAsync(item);
        Assert.NotNull(preloaded);
        Assert.True(preloaded.IsVideo);
        Assert.True(preloaded.IsPosterFrame);
        Assert.NotEqual(videoPath, preloaded.PreviewImagePath);
    }

    [Fact]
    public async Task NextCandidatePreview_ImageCandidate_UsesImagePath()
    {
        var imagePath = Path.Combine(_tempDir, "nature.jpg");
        File.WriteAllText(imagePath, "dummy image data");

        using var preloadService = new PageTurnPreloadService();
        var item = new WallpaperLibraryItem
        {
            Id = "img1",
            DisplayName = "Nature",
            FilePath = imagePath,
            Type = IpcConstants.WallpaperTypeImage
        };

        var preloaded = await preloadService.PreloadCandidateAsync(item);
        Assert.NotNull(preloaded);
        Assert.False(preloaded.IsVideo);
        Assert.False(preloaded.IsPosterFrame);
        Assert.Equal(imagePath, preloaded.PreviewImagePath);
    }

    #endregion

    #region 6. Commit Execution

    [Fact]
    public async Task CommitExecution_AdvancesToNextCandidateAndReschedulesTimer()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        await coordinator.BeginCornerGestureAsync(1910, 10, 1920, 1080);
        coordinator.UpdateGesture(1500, 400); // Progress ~0.80

        var targetCandidate = coordinator.PeekedCandidate;
        var committed = await coordinator.CommitAsync(animate: false);

        Assert.True(committed);
        Assert.Equal(targetCandidate!.Id, rotation.CurrentItemId);
        Assert.True(rotation.NextDueTimeUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CommitExecution_RecordsInteractiveCornerTriggerInHistory()
    {
        var items = CreateSampleItems(2);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        await coordinator.BeginCornerGestureAsync(1910, 10, 1920, 1080);
        coordinator.UpdateGesture(1400, 500);

        await coordinator.CommitAsync(animate: false);

        var history = rotation.History;
        Assert.NotEmpty(history);
        Assert.Equal("InteractiveCorner", history[0].Trigger);
        Assert.True(history[0].Success);
    }

    #endregion

    #region 7. Wave & Shadow Parameters

    [Fact]
    public void WaveAndShadow_LowQuality_DisablesWaveAndMinimizesShadow()
    {
        var context = new CornerGestureContext(
            StartX: 1920,
            StartY: 0,
            CurrentX: 1700,
            CurrentY: 200,
            ScreenWidth: 1920,
            ScreenHeight: 1080,
            Progress: 0.50,
            Velocity: 800.0,
            Sensitivity: TransitionSensitivity.Normal,
            Quality: TransitionQuality.Low,
            PaperWaveEnabled: true);

        Assert.Equal(0.0, context.ComputeWaveOffset());
        Assert.True(context.ComputeShadowOpacity() <= 0.35);
    }

    [Fact]
    public void WaveAndShadow_HighQuality_ScalesWaveWithVelocityAndShadowWithProgress()
    {
        var slowContext = new CornerGestureContext(
            StartX: 1920,
            StartY: 0,
            CurrentX: 1700,
            CurrentY: 200,
            ScreenWidth: 1920,
            ScreenHeight: 1080,
            Progress: 0.50,
            Velocity: 200.0,
            Sensitivity: TransitionSensitivity.Normal,
            Quality: TransitionQuality.High,
            PaperWaveEnabled: true);

        var fastContext = slowContext with { Velocity = 1500.0 };

        var slowWave = slowContext.ComputeWaveOffset();
        var fastWave = fastContext.ComputeWaveOffset();

        Assert.True(slowWave > 0);
        Assert.True(fastWave > slowWave);
        Assert.True(fastContext.ComputeShadowOpacity() > 0.40);
    }

    #endregion

    #region 8. Settings & Localization

    [Fact]
    public void Settings_DefaultConfiguration_IsValidAndSensible()
    {
        var settings = AppSettings.CreateDefault();

        Assert.False(settings.InteractiveTransitionEnabled);
        Assert.Equal(InteractiveTransitionEffect.PageTurn, settings.EffectiveTransitionEffect);
        Assert.True(settings.PaperWaveEnabled);
        Assert.Equal(TransitionSensitivity.Normal, settings.EffectiveTransitionSensitivity);
        Assert.Equal(TransitionQuality.Automatic, settings.EffectiveTransitionQuality);
    }

    [Fact]
    public void Settings_JsonRoundtripSerialization_PreservesAllProperties()
    {
        var original = new AppSettings
        {
            InteractiveTransitionEnabled = false,
            TransitionEffect = "PageTurn",
            PaperWaveEnabled = false,
            TransitionSensitivity = "High",
            TransitionQuality = "Low"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.InteractiveTransitionEnabled);
        Assert.Equal("PageTurn", deserialized.TransitionEffect);
        Assert.False(deserialized.PaperWaveEnabled);
        Assert.Equal(TransitionSensitivity.High, deserialized.EffectiveTransitionSensitivity);
        Assert.Equal(TransitionQuality.Low, deserialized.EffectiveTransitionQuality);
    }

    [Fact]
    public void Localization_AllTransitionKeys_ResolveCleanlyWithoutMojibake()
    {
        var service = new LocalizationService();

        var transitionKeys = typeof(LocalizationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.StartsWith("SetInteractive") || f.Name.StartsWith("Transition") || f.Name.StartsWith("SetPaperWave"))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(transitionKeys);

        // Verify Turkish
        service.SetLanguage(EffectiveLanguage.Turkish);
        var badPatterns = new[] { "Ã", "Ä", "Å", "Â", "â", "ï¿½", "\uFFFD" };

        foreach (var key in transitionKeys)
        {
            var text = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(text), $"Key '{key}' missing Turkish text.");
            Assert.NotEqual(key, text);

            foreach (var bad in badPatterns)
            {
                Assert.DoesNotContain(bad, text);
            }
        }

        // Verify English
        service.SetLanguage(EffectiveLanguage.English);
        foreach (var key in transitionKeys)
        {
            var text = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(text), $"Key '{key}' missing English text.");
            Assert.NotEqual(key, text);
        }
    }

    #endregion

    #region 10. Desktop Gesture Input Monitor & Visual Overlay Integration

    [Fact]
    public async Task InputMonitor_HookLifecycle_InstallsAndUninstallsCleanly()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            Assert.False(monitor.IsInstalled);

            var installed = monitor.InstallHook();
            Assert.True(installed);
            Assert.True(monitor.IsInstalled);

            monitor.UninstallHook();
            Assert.False(monitor.IsInstalled);

            // Dispose is idempotent
            monitor.Dispose();
            Assert.False(monitor.IsInstalled);
        }
    }

    [Fact]
    public async Task InputMonitor_NoDuplicateHooks()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            var first = monitor.InstallHook();
            var second = monitor.InstallHook();

            Assert.True(first);
            Assert.True(second);
            Assert.True(monitor.IsInstalled);
        }
    }

    [Fact]
    public void InputMonitor_MonitorBounds_ResolvesPositiveScreenArea()
    {
        var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height > 0);
        Assert.True(bounds.Right > bounds.Left);
        Assert.True(bounds.Bottom > bounds.Top);
    }

    [Fact]
    public async Task InputMonitor_HotCornerCoordinates_BoundaryDetection()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = new MonitorBounds(0, 0, 1920, 1080, 1920, 1080);

            // Inside corner
            Assert.True(monitor.IsInTopRightCorner(1920, 0, bounds));
            Assert.True(monitor.IsInTopRightCorner(1888, 32, bounds));
            Assert.True(monitor.IsInTopRightCorner(1910, 15, bounds));

            // Outside corner
            Assert.False(monitor.IsInTopRightCorner(1887, 15, bounds));
            Assert.False(monitor.IsInTopRightCorner(1900, 33, bounds));
            Assert.False(monitor.IsInTopRightCorner(100, 100, bounds));
        }
    }

    [Fact]
    public async Task InputMonitor_HoverEnterAndExit_FiresEventsAndCoordinator()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var insideX = bounds.Right - 10;
            var insideY = bounds.Top + 10;

            var hoverReported = false;
            monitor.HoverChanged += hovered => hoverReported = hovered;

            // Move inside hot corner
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, insideX, insideY);
            Assert.True(monitor.IsHovering);
            Assert.True(hoverReported);
            Assert.Equal(TransitionState.HoverPreview, coordinator.State);

            // Move outside hot corner
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, bounds.Left + 100, bounds.Top + 100);
            Assert.False(monitor.IsHovering);
            Assert.False(hoverReported);
            Assert.Equal(TransitionState.Idle, coordinator.State);
        }
    }

    [Fact]
    public async Task InputMonitor_LButtonDown_BeginsGestureWhenPermitted()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var insideX = bounds.Right - 10;
            var insideY = bounds.Top + 10;

            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, insideX, insideY);

            Assert.True(monitor.IsDragging);
            Assert.Contains(monitor.EventLog, l => l.Contains("LButtonDownDetected"));
            Assert.Contains(monitor.EventLog, l => l.Contains("GestureAccepted"));
        }
    }

    [Fact]
    public async Task InputMonitor_GlobalMoveTracksOutsideCorner()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var insideX = bounds.Right - 10;
            var insideY = bounds.Top + 10;

            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, insideX, insideY);
            Assert.True(monitor.IsDragging);

            // Move far away to screen center
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, bounds.Width / 2, bounds.Height / 2);

            Assert.True(monitor.IsDragging);
            Assert.Contains(monitor.EventLog, l => l.Contains("ProgressChanged"));
        }
    }

    [Fact]
    public async Task InputMonitor_ReleaseShortDrag_TriggersCancel()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var startX = bounds.Right - 10;
            var startY = bounds.Top + 10;

            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, startX, startY);

            // Barely moved (short drag, progress < 0.30)
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, startX - 20, startY + 20);
            monitor.ProcessMouseEvent(0x0202 /* WM_LBUTTONUP */, startX - 20, startY + 20);

            Assert.False(monitor.IsDragging);
            Assert.Contains(monitor.EventLog, l => l.Contains("CancelTriggered"));
        }
    }

    [Fact]
    public async Task InputMonitor_ReleaseLongDrag_TriggersCommit()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var startX = bounds.Right - 10;
            var startY = bounds.Top + 10;

            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, startX, startY);

            // Large drag across the screen (progress >= 0.30)
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, bounds.Left + 100, bounds.Bottom - 100);
            monitor.ProcessMouseEvent(0x0202 /* WM_LBUTTONUP */, bounds.Left + 100, bounds.Bottom - 100);

            Assert.False(monitor.IsDragging);
            Assert.Contains(monitor.EventLog, l => l.Contains("CommitTriggered"));
        }
    }

    [Fact]
    public async Task InputMonitor_RightClickCancelsActiveGesture()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.HotCornerSize = 32;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            var startX = bounds.Right - 10;
            var startY = bounds.Top + 10;

            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, startX, startY);
            Assert.True(monitor.IsDragging);

            // Right click during drag
            monitor.ProcessMouseEvent(0x0204 /* WM_RBUTTONDOWN */, startX - 50, startY + 50);

            Assert.False(monitor.IsDragging);
            Assert.Contains(monitor.EventLog, l => l.Contains("RightClickCancelled"));
        }
    }

    [Fact]
    public async Task InputMonitor_DisabledFeature_SafeNoOp()
    {
        var (coordinator, _, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
            monitor.IsEnabled = false;

            var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
            monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, bounds.Right - 10, bounds.Top + 10);

            Assert.False(monitor.IsDragging);
            Assert.False(monitor.IsHovering);
        }
    }

    [Fact]
    public async Task InputMonitor_PreconditionsNotMet_RotationOff_SafeNoOp()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("Off", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        using var monitor = new DesktopGestureInputMonitor(coordinator, () => rotation);
        var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();

        monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, bounds.Right - 10, bounds.Top + 10);

        Assert.False(monitor.IsDragging);
        Assert.Contains(monitor.EventLog, l => l.Contains("GestureRejected"));
    }

    [Fact]
    public void PageTurnVisualOverlay_LifecycleAndFrameRendering()
    {
        var overlay = new PageTurnVisualOverlay();
        try
        {
            Assert.NotEqual(IntPtr.Zero, overlay.Handle);
            Assert.True(overlay.ScreenWidth > 0);
            Assert.True(overlay.ScreenHeight > 0);
            Assert.Equal(0, overlay.RenderedFrameCount);
            Assert.False(overlay.IsVisible);

            // Hover surface
            overlay.ShowHover();
            Assert.True(overlay.RenderedFrameCount > 0);
            Assert.True(overlay.IsVisible);

            // Hide
            overlay.Hide();
            Assert.False(overlay.IsVisible);

            // Drag frame
            var context = new CornerGestureContext(
                StartX: overlay.ScreenWidth - 10,
                StartY: 10,
                CurrentX: overlay.ScreenWidth - 200,
                CurrentY: 200,
                ScreenWidth: overlay.ScreenWidth,
                ScreenHeight: overlay.ScreenHeight,
                Progress: 0.45,
                Velocity: 50.0,
                Sensitivity: TransitionSensitivity.Normal,
                Quality: TransitionQuality.High,
                PaperWaveEnabled: true,
                TopFoldX: overlay.ScreenWidth - 250,
                RightFoldY: 250,
                FoldAngleDegrees: 45.0);

            var initialFrames = overlay.RenderedFrameCount;
            overlay.ShowDragFrame(context);
            Assert.True(overlay.RenderedFrameCount > initialFrames);
            Assert.True(overlay.IsVisible);
        }
        finally
        {
            overlay.Dispose();
        }

        // Idempotent dispose
        overlay.Dispose();
    }

    [Fact]
    public async Task Preconditions_CoordinatorDynamicSettingsReflection()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        var currentSettings = new AppSettings { InteractiveTransitionEnabled = false };
        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => currentSettings);

        // Initially disabled
        Assert.False(coordinator.CanInitiateTransition());

        // Toggle enabled dynamically
        currentSettings.InteractiveTransitionEnabled = true;
        Assert.True(coordinator.CanInitiateTransition());

        // Turn rotation off dynamically
        await rotation.UpdateSettingsAsync("Off", "Sequential", 30);
        Assert.False(coordinator.CanInitiateTransition());
    }

    [Fact]
    public async Task WallpaperLibraryController_WiresLiveSettingsDelegate()
    {
        var currentSettings = new AppSettings { InteractiveTransitionEnabled = false };
        var libraryFile = Path.Combine(_tempDir, "library.json");
        var service = new WallpaperLibraryService(libraryFile);
        var controller = new WallpaperLibraryController(
            service: service,
            getSettings: () => currentSettings);

        try
        {
            Assert.NotNull(controller.TransitionCoordinator);
            Assert.False(controller.TransitionCoordinator.CanInitiateTransition());

            // Dynamically enable
            currentSettings.InteractiveTransitionEnabled = true;
            await controller.Rotation.UpdateSettingsAsync("All", "Sequential", 30);

            // Add sample items to make rotation eligible
            var filePath1 = Path.Combine(_tempDir, "w1.jpg");
            File.WriteAllText(filePath1, "dummy");
            await controller.AddFileAsync(filePath1);

            var filePath2 = Path.Combine(_tempDir, "w2.jpg");
            File.WriteAllText(filePath2, "dummy");
            await controller.AddFileAsync(filePath2);

            Assert.True(controller.TransitionCoordinator.CanInitiateTransition());
        }
        finally
        {
            controller.Dispose();
        }
    }

    #endregion

    #region Latency and Event Coalescing Tests

    [Fact]
    public async Task EventCoalescing_HighFrequencyMouseMoves_DropsIntermediateBacklogAndUpdatesLatest()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        using var monitor = new DesktopGestureInputMonitor(
            coordinator,
            () => rotation);

        monitor.InstallHook();

        var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
        var startX = bounds.Right - 10;
        var startY = bounds.Top + 10;

        // 1. Mouse down in top-right hot corner
        monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, startX, startY);
        Assert.True(monitor.IsDragging);

        // 2. Simulate 100 rapid mouse move events
        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= 100; i++)
        {
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, startX - (i * 4), startY + (i * 3));
        }
        sw.Stop();

        // The final mouse coordinates should be exactly (startX - 400, startY + 300)
        Assert.NotNull(coordinator.CurrentContext);
        Assert.Equal((double)(startX - 400), coordinator.CurrentContext.CurrentX);
        Assert.Equal((double)(startY + 300), coordinator.CurrentContext.CurrentY);

        // Verify telemetry recorded the events and input latency is very low (< 25ms)
        Assert.True(monitor.Telemetry.GestureInputLatencyMs < 25.0, $"Expected input latency < 25ms, got {monitor.Telemetry.GestureInputLatencyMs:F2}ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 250.0, $"100 events should process quickly, took {sw.Elapsed.TotalMilliseconds:F2}ms");

        monitor.ProcessMouseEvent(0x0202 /* WM_LBUTTONUP */, startX - 400, startY + 300);
        Assert.False(monitor.IsDragging);
        Assert.Equal(100, monitor.Telemetry.TotalPointerEvents);
    }

    [Fact]
    public async Task EventCoalescing_HumanLikeSlowDrag_SmoothProgressTracking()
    {
        var items = CreateSampleItems(3);
        using var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        using var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        using var monitor = new DesktopGestureInputMonitor(
            coordinator,
            () => rotation);

        monitor.InstallHook();

        var bounds = DesktopGestureInputMonitor.GetPrimaryMonitorBounds();
        var startX = bounds.Right - 10;
        var startY = bounds.Top + 10;

        monitor.ProcessMouseEvent(0x0201 /* WM_LBUTTONDOWN */, startX, startY);
        Assert.True(monitor.IsDragging);

        double lastProgress = 0.0;
        for (var i = 1; i <= 15; i++)
        {
            monitor.ProcessMouseEvent(0x0200 /* WM_MOUSEMOVE */, startX - (i * 25), startY + (i * 20));
            var currentProgress = coordinator.CurrentContext?.Progress ?? 0.0;
            Assert.True(currentProgress >= lastProgress, $"Progress should be monotonic: {currentProgress} vs {lastProgress}");
            lastProgress = currentProgress;
        }

        Assert.True(lastProgress > 0.30, $"Final progress should exceed commit threshold, got {lastProgress:P1}");
        monitor.ProcessMouseEvent(0x0202 /* WM_LBUTTONUP */, startX - 375, startY + 300);
        Assert.False(monitor.IsDragging);
    }

    [Fact]
    public void AnimationTiming_CommitAndCancel_WithinThresholds()
    {
        Assert.InRange(TransitionAnimationController.DefaultCancelDuration.TotalMilliseconds, 120.0, 180.0);
        Assert.InRange(TransitionAnimationController.DefaultCommitDuration.TotalMilliseconds, 160.0, 240.0);
    }

    [Fact]
    public void VisualOverlay_DirtyRegionClearing_ProducesZeroBacklog()
    {
        using var overlay = new PageTurnVisualOverlay();
        var context = new CornerGestureContext(
            StartX: 1910,
            StartY: 10,
            CurrentX: 1400,
            CurrentY: 500,
            ScreenWidth: 1920,
            ScreenHeight: 1080,
            Progress: 0.45,
            Velocity: 200.0,
            Sensitivity: TransitionSensitivity.Normal,
            Quality: TransitionQuality.High,
            PaperWaveEnabled: true,
            TopFoldX: 1500,
            RightFoldY: 400,
            FoldAngleDegrees: 45);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            var curContext = context with { CurrentX = 1400 - (i * 10), CurrentY = 500 + (i * 10), Progress = 0.45 + (i * 0.01) };
            overlay.ShowDragFrame(curContext);
        }
        sw.Stop();

        Assert.Equal(20, overlay.RenderedFrameCount);
        var avgPerFrame = sw.Elapsed.TotalMilliseconds / 20.0;
        Assert.True(avgPerFrame < 16.0, $"Average frame render duration should be < 16ms (60 FPS), got {avgPerFrame:F2}ms");
        overlay.Hide();
    }

    [Fact]
    public void AsyncImagePreload_NeverBlocksRenderThread()
    {
        using var overlay = new PageTurnVisualOverlay();
        var context = new CornerGestureContext(
            StartX: 1910,
            StartY: 10,
            CurrentX: 1800,
            CurrentY: 100,
            ScreenWidth: 1920,
            ScreenHeight: 1080,
            Progress: 0.10,
            Velocity: 50.0,
            Sensitivity: TransitionSensitivity.Normal,
            Quality: TransitionQuality.Balanced,
            PaperWaveEnabled: false,
            TopFoldX: 1850,
            RightFoldY: 80,
            FoldAngleDegrees: 45);

        var nonExistentLargePath = @"C:\FakeDirectory\NonExistentMassiveWallpaper_4K.jpg";

        var sw = Stopwatch.StartNew();
        overlay.ShowDragFrame(context, nonExistentLargePath);
        sw.Stop();

        // Must return in < 15ms without blocking
        Assert.True(sw.Elapsed.TotalMilliseconds < 15.0, $"Frame render must not block on image load, took {sw.Elapsed.TotalMilliseconds:F2}ms");
        overlay.Hide();
    }

    #endregion

    #region Test Helpers

    private async Task<(WallpaperTransitionCoordinator Coordinator, CornerPointerCaptureManager CaptureManager, WallpaperRotationService Rotation)> CreateTestPipelineAsync()
    {
        var items = CreateSampleItems(3);
        var rotation = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (it, ct) => Task.FromResult(new WallpaperLibraryApplyResult(true)),
            getHostStatus: ct => Task.FromResult(IpcResponse.Ok("Running", "Running")));
        await rotation.UpdateSettingsAsync("All", "Sequential", 30);

        var coordinator = new WallpaperTransitionCoordinator(
            getRotationService: () => rotation,
            getSettings: () => new AppSettings { InteractiveTransitionEnabled = true });

        var detector = new HotCornerDetector(1920, 1080, 32.0);
        var captureManager = new CornerPointerCaptureManager(coordinator, detector);

        return (coordinator, captureManager, rotation);
    }

    private List<WallpaperLibraryItem> CreateSampleItems(int count)
    {
        var list = new List<WallpaperLibraryItem>();
        for (var i = 1; i <= count; i++)
        {
            var filePath = Path.Combine(_tempDir, $"wallpaper_{i}.jpg");
            File.WriteAllText(filePath, "test image data");

            list.Add(new WallpaperLibraryItem
            {
                Id = $"item_{i}",
                DisplayName = $"Wallpaper {i}",
                FilePath = filePath,
                Type = IpcConstants.WallpaperTypeImage,
                AddedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        return list;
    }

    #endregion
}
