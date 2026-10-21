using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class RuntimeHostTests
{
    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.HostPipe.{Guid.NewGuid():N}";

    private sealed class TestDesktopHost : IDesktopHost
    {
        public nint NextHandle { get; set; } = 10;
        public int ResolveCount { get; private set; }
        public nint LastAttachedHost { get; private set; }
        public HashSet<nint> InvalidHandles { get; } = [];

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return Task.FromResult(new DesktopHostResult(NextHandle, DesktopHostError.None));
        }

        public bool IsValid(nint handle) => handle != 0 && !InvalidHandles.Contains(handle);

        public nint Attach(nint host, nint window)
        {
            LastAttachedHost = host;
            return 0;
        }

        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class ControllableRenderer : IWallpaperRenderer
    {
        private readonly TaskCompletionSource<bool>? loadStarted;
        private readonly TaskCompletionSource<WallpaperResult>? loadGate;
        private readonly WallpaperResult loadResult;

        public ControllableRenderer(
            TaskCompletionSource<bool>? loadStarted = null,
            TaskCompletionSource<WallpaperResult>? loadGate = null,
            WallpaperResult? loadResult = null)
        {
            this.loadStarted = loadStarted;
            this.loadGate = loadGate;
            this.loadResult = loadResult ?? new WallpaperResult();
        }

        public nint Handle { get; set; } = 42;
        public nint WindowHandle => Handle;
        public int DisposeCount { get; private set; }
        public string? LoadedPath { get; private set; }

        public Task<WallpaperResult> LoadAsync(string path)
        {
            LoadedPath = path;
            loadStarted?.TrySetResult(true);
            return loadGate?.Task ?? Task.FromResult(loadResult);
        }

        public void Show() { }

        public void Dispose()
        {
            if (DisposeCount != 0) return;
            DisposeCount++;
            Handle = 0;
            loadGate?.TrySetResult(new WallpaperResult(WallpaperError.Stopped, "Wallpaper stopped"));
        }
    }

    [Fact]
    public void SingleInstanceGuard_FirstInstanceAcquiresMutex()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var guard = new SingleInstanceGuard(mutexName);

        Assert.True(guard.IsSingleInstance);
    }

    [Fact]
    public async Task SingleInstanceGuard_SecondInstanceIsPrevented()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(mutexName);
        Assert.True(first.IsSingleInstance);

        var secondIsSingleInstance = await Task.Run(() =>
        {
            using var second = new SingleInstanceGuard(mutexName);
            return second.IsSingleInstance;
        });

        Assert.False(secondIsSingleInstance);
    }

    [Fact]
    public void SingleInstanceGuard_AllowsNewInstanceAfterDispose()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";

        using (var first = new SingleInstanceGuard(mutexName))
        {
            Assert.True(first.IsSingleInstance);
        }

        using (var second = new SingleInstanceGuard(mutexName))
        {
            Assert.True(second.IsSingleInstance);
        }
    }

    [Fact]
    public void RuntimeCompositionRoot_InitializesServicesWithoutStartingWallpaper()
    {
        using var root = new RuntimeCompositionRoot();

        Assert.NotNull(root.DesktopHost);
        Assert.NotNull(root.WallpaperService);
        Assert.Null(root.WallpaperService.ActivePath);
        Assert.False(root.IsDisposed);
    }

    [Fact]
    public void RuntimeCompositionRoot_DisposesSafely()
    {
        var root = new RuntimeCompositionRoot();
        Assert.False(root.IsDisposed);

        root.Dispose();
        Assert.True(root.IsDisposed);

        // Multiple disposes should not throw
        var exception = Record.Exception(() => root.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task RuntimeHostApp_ExitsWithCode1WhenNotSingleInstance()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var activeInstance = new SingleInstanceGuard(mutexName);
        Assert.True(activeInstance.IsSingleInstance);

        var exitCode = await Task.Run(() =>
        {
            using var conflictingGuard = new SingleInstanceGuard(mutexName);
            Assert.False(conflictingGuard.IsSingleInstance);

            using var app = new RuntimeHostApp(guard: conflictingGuard);
            return app.Run();
        });

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void RuntimeHostApp_ShutsDownGracefullyOnCancellation()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var guard = new SingleInstanceGuard(mutexName);
        using var app = new RuntimeHostApp(guard: guard);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel to trigger immediate graceful shutdown

        var exitCode = app.Run(cts.Token);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DuplicateShutdown_IsIdempotent_DisposesRendererOnce_AndDoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_DupShutdown_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "active.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var pipeName = CreateUniquePipeName();

            ControllableRenderer? activeRenderer = null;
            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => activeRenderer = new ControllableRenderer(),
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            var runTask = Task.Run(() => host.Run());
            var client = new RuntimeIpcClient(pipeName);
            for (var i = 0; i < 20; i++)
            {
                if ((await client.PingAsync(250)).Success) break;
                await Task.Delay(50);
            }

            Assert.True((await client.SetImageAsync(imagePath)).Success);
            Assert.NotNull(activeRenderer);

            var firstShutdown = await client.ShutdownAsync(timeoutMs: 2000);
            Assert.True(firstShutdown.Success);

            // Second shutdown / direct RequestShutdown calls must be safe and idempotent
            var exception = Record.Exception(() =>
            {
                host.RequestShutdown();
                host.RequestShutdown();
            });
            Assert.Null(exception);

            var exitCode = await runTask;
            Assert.Equal(0, exitCode);
            Assert.Equal(1, activeRenderer!.DisposeCount);

            // Wait past the 50ms delayed IPC server shutdown callback after Dispose
            host.Dispose();
            await Task.Delay(100);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndShutdown_BackToBack_IsSafe_AndLateStopDuringShutdownPreservesSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_StopShutdown_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "keep.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var pipeName = CreateUniquePipeName();

            ControllableRenderer? activeRenderer = null;
            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => activeRenderer = new ControllableRenderer(),
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            var runTask = Task.Run(() => host.Run());
            var client = new RuntimeIpcClient(pipeName);
            for (var i = 0; i < 20; i++)
            {
                if ((await client.PingAsync(250)).Success) break;
                await Task.Delay(50);
            }

            Assert.True((await client.SetImageAsync(imagePath)).Success);

            // 1. Trigger shutdown first, then immediately send StopWallpaper while pipe is still closing
            host.RequestShutdown();
            _ = await client.StopWallpaperAsync(timeoutMs: 500);

            var exitCode = await runTask;
            Assert.Equal(0, exitCode);
            Assert.Equal(1, activeRenderer!.DisposeCount);

            // Persisted settings must NOT have been wiped by a late StopWallpaper after shutdown started
            var persisted = await settingsService.LoadAsync();
            Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
            Assert.Equal(Path.GetFullPath(imagePath), persisted.LastWallpaperPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAndSetImageRace_WhenRestoreIsMidLoad_UserSetImagePreemptsRestoreAndWins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_RestoreSetRace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var restoredImage = Path.Combine(tempDir, "restored.png");
            var userImage = Path.Combine(tempDir, "user_selected.png");
            await File.WriteAllBytesAsync(restoredImage, [1, 2, 3]);
            await File.WriteAllBytesAsync(userImage, [4, 5, 6]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            await settingsService.SaveAsync(new AppSettings
            {
                RestoreLastWallpaper = true,
                LastWallpaperType = IpcConstants.WallpaperTypeImage,
                LastWallpaperPath = restoredImage
            });

            var restoreLoadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var restoreLoadGate = new TaskCompletionSource<WallpaperResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderers = new List<ControllableRenderer>();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () =>
                {
                    // First renderer is for the startup restore (blocks until cancelled/disposed)
                    var renderer = renderers.Count == 0
                        ? new ControllableRenderer(restoreLoadStarted, restoreLoadGate)
                        : new ControllableRenderer();
                    renderers.Add(renderer);
                    return renderer;
                },
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();

            // Wait until restore is actively mid-load inside WallpaperService.StartAsync
            await restoreLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var client = new RuntimeIpcClient(pipeName);
            var setTask = client.SetImageAsync(userImage, timeoutMs: 3000);

            // Simulate the in-flight image decode finishing while user SetImage is waiting on CancelPendingRestoreAsync
            await Task.Delay(50);
            restoreLoadGate.TrySetResult(new WallpaperResult());

            var setResponse = await setTask;

            Assert.True(setResponse.Success, $"Expected user SetImage to preempt restore, got: {setResponse.Message}");
            Assert.Equal(Path.GetFullPath(userImage), host.CurrentState.Path);
            Assert.Equal(IpcConstants.WallpaperTypeImage, host.CurrentState.Type);
            Assert.Equal(2, renderers.Count);
            Assert.Equal(1, renderers[0].DisposeCount);
            Assert.Equal(0, renderers[1].DisposeCount);

            var persisted = await settingsService.LoadAsync();
            Assert.Equal(Path.GetFullPath(userImage), persisted.LastWallpaperPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAndStopRace_WhenRestoreIsMidLoad_UserStopPreemptsRestoreAndWins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_RestoreStopRace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var restoredImage = Path.Combine(tempDir, "restored.png");
            await File.WriteAllBytesAsync(restoredImage, [1, 2, 3]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            await settingsService.SaveAsync(new AppSettings
            {
                RestoreLastWallpaper = true,
                LastWallpaperType = IpcConstants.WallpaperTypeImage,
                LastWallpaperPath = restoredImage
            });

            var restoreLoadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var restoreLoadGate = new TaskCompletionSource<WallpaperResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ControllableRenderer? restoreRenderer = null;

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => restoreRenderer = new ControllableRenderer(restoreLoadStarted, restoreLoadGate),
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();

            await restoreLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var client = new RuntimeIpcClient(pipeName);
            var stopTask = client.StopWallpaperAsync(timeoutMs: 3000);

            await Task.Delay(50);
            restoreLoadGate.TrySetResult(new WallpaperResult());

            var stopResponse = await stopTask;

            Assert.True(stopResponse.Success);
            Assert.False(host.CurrentState.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeNone, host.CurrentState.Type);
            Assert.NotNull(restoreRenderer);
            Assert.Equal(1, restoreRenderer!.DisposeCount);

            var persisted = await settingsService.LoadAsync();
            Assert.Equal(IpcConstants.WallpaperTypeNone, persisted.LastWallpaperType);
            Assert.Null(persisted.LastWallpaperPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureHostRunningAsync_ConcurrentCalls_SerializeAndRespondCleanly()
    {
        var pipeName = CreateUniquePipeName();
        var pingCount = 0;
        using var server = new RuntimeIpcServer(
            pipeName,
            onCommandHandler: _ => Task.FromResult(IpcResponse.Ok(string.Empty)));
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var launcher = new RuntimeHostLauncher(client);

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                Interlocked.Increment(ref pingCount);
                return await launcher.EnsureHostRunningAsync(timeoutMs: 2000);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(6, pingCount);
        Assert.All(results, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task ExplorerRestartAfterActiveVideo_SetVideoDisposesStaleRenderer_FailedStartDoesNotPersist_AndSubsequentSetVideoSucceeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_ExplorerVideoRestart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var video1 = Path.Combine(tempDir, "video1.mp4");
            var video2 = Path.Combine(tempDir, "video2.mp4");
            await File.WriteAllBytesAsync(video1, [1, 2, 3]);
            await File.WriteAllBytesAsync(video2, [4, 5, 6]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var desktopHost = new TestDesktopHost { NextHandle = 10 };
            var videoRenderers = new List<ControllableRenderer>();
            var nextVideoResult = new WallpaperResult();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                desktopHost,
                () => new ControllableRenderer(),
                () =>
                {
                    var renderer = new ControllableRenderer(loadResult: nextVideoResult);
                    videoRenderers.Add(renderer);
                    return renderer;
                },
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();
            var client = new RuntimeIpcClient(pipeName);

            // 1. Apply initial video wallpaper on WorkerW handle 10
            var firstSet = await client.SetVideoAsync(video1, timeoutMs: 3000);
            Assert.True(firstSet.Success);
            Assert.Single(videoRenderers);
            Assert.Equal(10, desktopHost.LastAttachedHost);
            Assert.Equal(Path.GetFullPath(video1), host.CurrentState.Path);

            // 2. Simulate Explorer restart: WorkerW 10 and child video HWND are destroyed by OS; new WorkerW is 20
            desktopHost.InvalidHandles.Add(10);
            desktopHost.NextHandle = 20;
            videoRenderers[0].Handle = 0;

            // 3. First attempt after Explorer restart fails in video load -> stale renderer must be disposed, failure must not persist video2
            nextVideoResult = new WallpaperResult(WallpaperError.RendererFailed, "Simulated video decode error");
            var failedSet = await client.SetVideoAsync(video2, timeoutMs: 3000);
            Assert.False(failedSet.Success);
            Assert.Equal(2, videoRenderers.Count);
            Assert.Equal(1, videoRenderers[0].DisposeCount);
            Assert.Equal(1, videoRenderers[1].DisposeCount);

            var persistedAfterFail = await settingsService.LoadAsync();
            Assert.Equal(Path.GetFullPath(video1), persistedAfterFail.LastWallpaperPath);

            // 4. Retry video apply -> fresh video renderer attaches to new WorkerW 20 and updates state
            nextVideoResult = new WallpaperResult();
            var recoveredSet = await client.SetVideoAsync(video2, timeoutMs: 3000);
            Assert.True(recoveredSet.Success);
            Assert.Equal(3, videoRenderers.Count);
            Assert.Equal(0, videoRenderers[2].DisposeCount);
            Assert.Equal(20, desktopHost.LastAttachedHost);
            Assert.Equal(Path.GetFullPath(video2), host.CurrentState.Path);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, host.CurrentState.Type);

            var persistedAfterSuccess = await settingsService.LoadAsync();
            Assert.Equal(Path.GetFullPath(video2), persistedAfterSuccess.LastWallpaperPath);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, persistedAfterSuccess.LastWallpaperType);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplorerLoss_Apply_Shutdown_IsSafeAndPreservesSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_ShutdownLoss_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var image1 = Path.Combine(tempDir, "image1.jpg");
            var image2 = Path.Combine(tempDir, "image2.jpg");
            await File.WriteAllBytesAsync(image1, [1, 2, 3]);
            await File.WriteAllBytesAsync(image2, [4, 5, 6]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var desktopHost = new TestDesktopHost { NextHandle = 10 };
            var renderers = new List<ControllableRenderer>();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                desktopHost,
                () =>
                {
                    var renderer = new ControllableRenderer();
                    renderers.Add(renderer);
                    return renderer;
                },
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();
            var client = new RuntimeIpcClient(pipeName);

            // 1. Initial wallpaper applied
            var firstSet = await client.SetImageAsync(image1, timeoutMs: 3000);
            Assert.True(firstSet.Success);
            Assert.Single(renderers);
            Assert.Equal(0, renderers[0].DisposeCount);

            // 2. Simulate Explorer restart: window 1 dead, desktop host dead
            desktopHost.InvalidHandles.Add(10);
            desktopHost.NextHandle = 20;
            renderers[0].Handle = 0;

            // 3. Apply wallpaper 2 on new Explorer host
            var secondSet = await client.SetImageAsync(image2, timeoutMs: 3000);
            Assert.True(secondSet.Success);
            Assert.Equal(2, renderers.Count);
            Assert.Equal(1, renderers[0].DisposeCount);
            Assert.Equal(0, renderers[1].DisposeCount);

            // 4. Shutdown requested
            var shutdown = await client.ShutdownAsync(timeoutMs: 3000);
            Assert.True(shutdown.Success);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((!host.IsShuttingDown || renderers[1].DisposeCount == 0) && sw.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(20);
            }
            Assert.True(host.IsShuttingDown);
            Assert.Equal(1, renderers[0].DisposeCount);
            Assert.Equal(1, renderers[1].DisposeCount);

            // 5. Settings must preserve image2
            var persisted = await settingsService.LoadAsync();
            Assert.Equal(Path.GetFullPath(image2), persisted.LastWallpaperPath);
            Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
            Assert.True(persisted.RestoreLastWallpaper);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StaleRenderer_Shutdown_DoubleDispose_IsSafe()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_StaleShutdown_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var image = Path.Combine(tempDir, "image.jpg");
            await File.WriteAllBytesAsync(image, [1, 2, 3]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var desktopHost = new TestDesktopHost { NextHandle = 10 };
            var renderers = new List<ControllableRenderer>();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                desktopHost,
                () =>
                {
                    var renderer = new ControllableRenderer();
                    renderers.Add(renderer);
                    return renderer;
                },
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();
            var client = new RuntimeIpcClient(pipeName);

            var set = await client.SetImageAsync(image, timeoutMs: 3000);
            Assert.True(set.Success);
            Assert.Single(renderers);

            // Explorer restart kills window and host
            desktopHost.InvalidHandles.Add(10);
            renderers[0].Handle = 0;

            // Shutdown with stale renderer
            host.RequestShutdown();
            Assert.True(host.IsShuttingDown);
            Assert.Equal(1, renderers[0].DisposeCount);

            // Calling Dispose after RequestShutdown must be a safe idempotent no-op
            host.Dispose();
            Assert.Equal(1, renderers[0].DisposeCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RequestShutdown_And_Dispose_RendererCleanupOnce()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_CleanupOnce_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var image = Path.Combine(tempDir, "image.jpg");
            await File.WriteAllBytesAsync(image, [1, 2, 3]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var desktopHost = new TestDesktopHost { NextHandle = 10 };
            var renderers = new List<ControllableRenderer>();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                desktopHost,
                () =>
                {
                    var renderer = new ControllableRenderer();
                    renderers.Add(renderer);
                    return renderer;
                },
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();
            var client = new RuntimeIpcClient(pipeName);

            var set = await client.SetImageAsync(image, timeoutMs: 3000);
            Assert.True(set.Success);
            Assert.Single(renderers);
            Assert.Equal(0, renderers[0].DisposeCount);

            host.RequestShutdown();
            Assert.Equal(1, renderers[0].DisposeCount);

            // Repeated dispose does not clean up twice
            host.Dispose();
            host.Dispose();
            Assert.Equal(1, renderers[0].DisposeCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_Then_Exit_IsSafeAndPreservesSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_StopExit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var image = Path.Combine(tempDir, "image.jpg");
            await File.WriteAllBytesAsync(image, [1, 2, 3]);

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var desktopHost = new TestDesktopHost { NextHandle = 10 };
            var renderers = new List<ControllableRenderer>();

            var pipeName = CreateUniquePipeName();
            using var composition = new RuntimeCompositionRoot(
                desktopHost,
                () =>
                {
                    var renderer = new ControllableRenderer();
                    renderers.Add(renderer);
                    return renderer;
                },
                () => new ControllableRenderer(),
                settingsService);
            using var guard = new SingleInstanceGuard($@"Local\NiceWP.Test.{Guid.NewGuid():N}");
            using var host = new RuntimeHostApp(guard: guard, composition: composition, pipeName: pipeName);

            host.Start();
            var client = new RuntimeIpcClient(pipeName);

            var set = await client.SetImageAsync(image, timeoutMs: 3000);
            Assert.True(set.Success);
            Assert.Single(renderers);
            Assert.Equal(0, renderers[0].DisposeCount);

            // Stop wallpaper first
            var stop = await client.StopWallpaperAsync(timeoutMs: 3000);
            Assert.True(stop.Success);
            Assert.Equal(1, renderers[0].DisposeCount);
            Assert.False(host.CurrentState.HasActiveWallpaper);

            // Then Shutdown
            var shutdown = await client.ShutdownAsync(timeoutMs: 3000);
            Assert.True(shutdown.Success);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!host.IsShuttingDown && sw.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(20);
            }
            Assert.True(host.IsShuttingDown);
            Assert.Equal(1, renderers[0].DisposeCount);

            // Settings preserved with None (as set by StopWallpaper)
            var persisted = await settingsService.LoadAsync();
            Assert.Equal(IpcConstants.WallpaperTypeNone, persisted.LastWallpaperType);
            Assert.Null(persisted.LastWallpaperPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
