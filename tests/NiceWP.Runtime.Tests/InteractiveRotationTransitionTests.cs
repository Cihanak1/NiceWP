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

    #region 10. Desktop Gesture Window & Preconditions Integration

    [Fact]
    public async Task GestureWindow_CreationAndDisposal_CleansUpNativeResources()
    {
        var (coordinator, captureManager, rotation) = await CreateTestPipelineAsync();
        using (coordinator)
        using (rotation)
        {
            var window = new TopRightGestureWindow(
                coordinator,
                captureManager,
                () => rotation,
                () => null,
                committedItem => { });

            try
            {
                Assert.NotEqual(IntPtr.Zero, window.Handle);
                Assert.True(window.ScreenWidth > 0);
                Assert.True(window.ScreenHeight > 0);
                Assert.False(window.IsCapturing);

                // Visibility update should succeed without error
                window.UpdateVisibility();
            }
            finally
            {
                window.Dispose();
            }

            // Disposing again should be idempotent
            window.Dispose();
        }
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
