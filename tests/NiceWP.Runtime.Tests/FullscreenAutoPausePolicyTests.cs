using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Platform.Windows;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class FullscreenAutoPausePolicyTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string settingsFilePath;
    private readonly SettingsService settingsService;

    public FullscreenAutoPausePolicyTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_FullscreenPolicyTests_{Guid.NewGuid():N}");
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
        $@"NiceWP.Test.FullscreenPolicyPipe.{Guid.NewGuid():N}";

    private string CreateTempWallpaperFile(string fileName)
    {
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static ForegroundWindowSnapshot CreateValidFullscreenSnapshot() =>
        new(
            Handle: 1001,
            IsValidWindow: true,
            IsVisible: true,
            IsMinimized: false,
            IsCloaked: false,
            ClassName: "Chrome_WidgetWin_1",
            ProcessId: 4242,
            CurrentProcessId: 9999,
            ProcessName: "chrome",
            HasWindowBounds: true,
            WindowBounds: new ScreenBounds(0, 0, 1920, 1080),
            HasMonitorBounds: true,
            MonitorBounds: new ScreenBounds(0, 0, 1920, 1080));

    private sealed class FakeFullscreenDetector : IFullscreenWindowDetector
    {
        public volatile bool IsFullscreen;
        public bool IsFullscreenWindowActive() => IsFullscreen;
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
        IFullscreenWindowDetector detector,
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
            detector);

    [Fact]
    public void Detector_ForegroundFullscreenWindow_ReturnsTrue()
    {
        var exactSnapshot = CreateValidFullscreenSnapshot();
        var detector = new FullscreenWindowDetector(() => exactSnapshot);
        Assert.True(detector.IsFullscreenWindowActive());

        // Auto-hidden taskbar or minor 2px edge tolerance should still count as fullscreen
        var toleranceSnapshot = exactSnapshot with
        {
            WindowBounds = new ScreenBounds(0, 0, 1920, 1078)
        };
        Assert.True(FullscreenWindowDetector.Evaluate(toleranceSnapshot));
    }

    [Fact]
    public void Detector_NormalOrMaximizedWithTaskbarWindow_ReturnsFalse()
    {
        var normalWindow = CreateValidFullscreenSnapshot() with
        {
            WindowBounds = new ScreenBounds(120, 80, 1440, 900)
        };
        var detector = new FullscreenWindowDetector(() => normalWindow);
        Assert.False(detector.IsFullscreenWindowActive());

        // Maximized window with visible 48px taskbar does not cover full monitor bounds
        var maximizedWithVisibleTaskbar = CreateValidFullscreenSnapshot() with
        {
            WindowBounds = new ScreenBounds(0, 0, 1920, 1032)
        };
        Assert.False(FullscreenWindowDetector.Evaluate(maximizedWithVisibleTaskbar));
    }

    [Fact]
    public void Detector_MinimizedWindow_ReturnsFalse()
    {
        var minimized = CreateValidFullscreenSnapshot() with { IsMinimized = true };
        var detector = new FullscreenWindowDetector(() => minimized);
        Assert.False(detector.IsFullscreenWindowActive());
    }

    [Fact]
    public void Detector_InvisibleOrCloakedWindow_ReturnsFalse()
    {
        var invisible = CreateValidFullscreenSnapshot() with { IsVisible = false };
        Assert.False(new FullscreenWindowDetector(() => invisible).IsFullscreenWindowActive());

        var cloaked = CreateValidFullscreenSnapshot() with { IsCloaked = true };
        Assert.False(new FullscreenWindowDetector(() => cloaked).IsFullscreenWindowActive());
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("SHELLDLL_DefView")]
    public void Detector_ShellWindows_ReturnFalse(string className)
    {
        var shellWindow = CreateValidFullscreenSnapshot() with { ClassName = className };
        var detector = new FullscreenWindowDetector(() => shellWindow);
        Assert.False(detector.IsFullscreenWindowActive());
    }

    [Theory]
    [InlineData("NiceWP.App")]
    [InlineData("NiceWP.App.exe")]
    [InlineData("NiceWP.Runtime.Host")]
    [InlineData("NiceWP.Runtime.Host.exe")]
    public void Detector_NiceWpProcessesAndCurrentHostProcess_ReturnFalse(string processName)
    {
        var niceWpProcess = CreateValidFullscreenSnapshot() with { ProcessName = processName };
        Assert.False(FullscreenWindowDetector.Evaluate(niceWpProcess));

        var sameProcessSnapshot = CreateValidFullscreenSnapshot() with
        {
            ProcessId = 7777,
            CurrentProcessId = 7777,
            ProcessName = "anything"
        };
        Assert.False(FullscreenWindowDetector.Evaluate(sameProcessSnapshot));
    }

    [Fact]
    public async Task Policy_RunningPlusFullscreen_AutoPauses_AndFullscreenGone_AutoResumesWithoutWritingSettings()
    {
        var videoPath = CreateTempWallpaperFile("auto_pause.mp4");
        var detector = new FakeFullscreenDetector();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(detector, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.IsAutoPaused);

        var fixedWriteTime = new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settingsFilePath, fixedWriteTime);

        // Running + fullscreen -> auto pause
        detector.IsFullscreen = true;
        await app.EvaluateFullscreenPolicyAsync();

        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));

        // Auto paused + fullscreen gone -> auto resume
        detector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();

        Assert.True(app.CurrentState.IsRunning);
        Assert.Equal(IpcConstants.PlaybackStateRunning, app.CurrentState.PlaybackState);
        Assert.False(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].ResumeCallCount);
        Assert.Equal(fixedWriteTime, File.GetLastWriteTimeUtc(settingsFilePath));
    }

    [Fact]
    public async Task Policy_ManualPaused_WhenFullscreenStartsAndEnds_DoesNotAutoResume()
    {
        var videoPath = CreateTempWallpaperFile("manual_pause.mp4");
        var detector = new FakeFullscreenDetector();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(detector, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);

        // User manually pauses
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);
        Assert.False(app.IsAutoPaused);

        // Fullscreen detected while manually paused -> stays manually paused
        detector.IsFullscreen = true;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.False(app.IsAutoPaused);

        // Fullscreen ends -> still Paused (not auto-resumed)
        detector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.False(app.IsAutoPaused);
        Assert.Equal(0, videoRenderers[0].ResumeCallCount);
    }

    [Fact]
    public async Task Policy_ManualPauseWhileAutoPaused_ConvertsToManualPauseAndPreventsAutoResume()
    {
        var videoPath = CreateTempWallpaperFile("auto_to_manual.mp4");
        var detector = new FakeFullscreenDetector { IsFullscreen = true };
        using var composition = CreateComposition(detector);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
        Assert.True(app.IsAutoPaused);

        // Explicit manual PauseWallpaper command overrides auto-pause flag
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.PauseWallpaperCommand))).Success);
        Assert.True(app.CurrentState.IsPaused);
        Assert.False(app.IsAutoPaused);

        // Fullscreen ends -> remains Paused
        detector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.CurrentState.IsPaused);
    }

    [Fact]
    public async Task Policy_StopWallpaper_ClearsPolicyState()
    {
        var videoPath = CreateTempWallpaperFile("stop_clears_policy.mp4");
        var detector = new FakeFullscreenDetector { IsFullscreen = true };
        using var composition = CreateComposition(detector);
        using var app = new RuntimeHostApp(composition: composition, pipeName: CreateUniquePipeName());

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        await app.EvaluateFullscreenPolicyAsync();
        Assert.True(app.IsAutoPaused);
        Assert.True(app.CurrentState.IsPaused);

        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.StopWallpaperCommand))).Success);
        Assert.False(app.IsAutoPaused);
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);

        // Fullscreen ends -> no resume attempt, remains None
        detector.IsFullscreen = false;
        await app.EvaluateFullscreenPolicyAsync();
        Assert.False(app.CurrentState.HasActiveWallpaper);
        Assert.Equal(IpcConstants.PlaybackStateNone, app.CurrentState.PlaybackState);
    }

    [Fact]
    public async Task Policy_SetImageAndSetVideo_WhileFullscreen_StartRunningAndSoonAutoPause()
    {
        var imagePath = CreateTempWallpaperFile("fullscreen_img.png");
        var videoPath = CreateTempWallpaperFile("fullscreen_vid.mp4");
        var detector = new FakeFullscreenDetector { IsFullscreen = true };
        var imageRenderers = new List<TrackingRenderer>();
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(detector, imageRenderers, videoRenderers);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: CreateUniquePipeName(),
            fullscreenPollIntervalMs: 25);

        app.Start();
        await app.RestoreTask;

        // SetImage while fullscreen -> policy loop auto-pauses shortly
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetImageCommand, imagePath))).Success);
        await WaitForConditionAsync(() => app.CurrentState.IsPaused && app.IsAutoPaused, timeoutMs: 1500);
        Assert.Equal(IpcConstants.WallpaperTypeImage, app.CurrentState.Type);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsAutoPaused);
        Assert.Equal(1, imageRenderers[0].PauseCallCount);

        // SetVideo while fullscreen -> starts Running, then policy loop auto-pauses shortly
        Assert.True((await app.HandleCommandAsync(new IpcRequest(IpcConstants.SetVideoCommand, videoPath))).Success);
        await WaitForConditionAsync(() =>
            string.Equals(app.CurrentState.Type, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase) &&
            app.CurrentState.IsPaused &&
            app.IsAutoPaused, timeoutMs: 1500);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);
    }

    [Fact]
    public async Task Policy_StartupRestoredWallpaper_WhileFullscreen_AutoPauses_AndShutdownStopsMonitorCleanly()
    {
        var videoPath = CreateTempWallpaperFile("restored_fullscreen.mp4");
        await settingsService.SaveAsync(new AppSettings
        {
            RestoreLastWallpaper = true,
            LastWallpaperType = IpcConstants.WallpaperTypeVideo,
            LastWallpaperPath = videoPath
        });

        var detector = new FakeFullscreenDetector { IsFullscreen = true };
        var videoRenderers = new List<TrackingRenderer>();
        using var composition = CreateComposition(detector, videoRenderers: videoRenderers);
        using var app = new RuntimeHostApp(
            composition: composition,
            pipeName: CreateUniquePipeName(),
            fullscreenPollIntervalMs: 25);

        app.Start();
        await app.RestoreTask;

        // Startup restored wallpaper while fullscreen -> policy auto-pauses
        await WaitForConditionAsync(() => app.CurrentState.IsPaused && app.IsAutoPaused, timeoutMs: 1500);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, app.CurrentState.Type);
        Assert.Equal(Path.GetFullPath(videoPath), app.CurrentState.Path);
        Assert.Equal(IpcConstants.PlaybackStatePaused, app.CurrentState.PlaybackState);
        Assert.True(app.IsAutoPaused);
        Assert.Equal(1, videoRenderers[0].PauseCallCount);

        // Shutdown -> monitor task stops cleanly without leaking
        app.RequestShutdown();
        var completedTask = await Task.WhenAny(app.MonitorTask, Task.Delay(1000));
        Assert.Same(app.MonitorTask, completedTask);
        Assert.True(app.MonitorTask.IsCompletedSuccessfully);
        Assert.False(app.IsAutoPaused);
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

        Assert.True(predicate(), "Timed out waiting for expected policy state transition.");
    }
}
