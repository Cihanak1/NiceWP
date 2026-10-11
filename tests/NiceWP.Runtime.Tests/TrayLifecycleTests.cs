using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.SingleInstance;
using NiceWP.Infrastructure.Tray;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class TrayLifecycleTests
{
    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.TrayPipe.{Guid.NewGuid():N}";

    private sealed class TestDesktopHost : IDesktopHost
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
        public int DisposeCount { get; private set; }
        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void WindowClose_CancelsCloseAndHidesWindow_DoesNotExitApp()
    {
        var pipeName = CreateUniquePipeName();
        var client = new RuntimeIpcClient(pipeName);

        var hideCalled = false;
        var exitCalled = false;

        var controller = new TrayLifecycleController(
            client,
            hideWindowAction: () => hideCalled = true,
            exitAppAction: () => exitCalled = true);

        Assert.True(controller.IsWindowVisible);
        Assert.False(controller.IsExited);

        // Simulate user clicking 'X'
        controller.OnWindowCloseRequested(out var cancelClose);

        Assert.True(cancelClose, "Close must be canceled so window is not destroyed.");
        Assert.False(controller.IsWindowVisible, "Window visibility must be marked false.");
        Assert.True(hideCalled, "Hide window action must be invoked.");
        Assert.False(controller.IsExited, "Application must not exit when closed via X.");
        Assert.False(exitCalled, "Exit callback must not be invoked.");
    }

    [Fact]
    public async Task RepeatedOpenAndClose_TogglesVisibilityCleanlyWithoutExiting()
    {
        var pipeName = CreateUniquePipeName();
        var client = new RuntimeIpcClient(pipeName);

        var hideCount = 0;
        var restoreCount = 0;
        var syncCount = 0;

        var controller = new TrayLifecycleController(
            client,
            restoreWindowAction: () => restoreCount++,
            hideWindowAction: () => hideCount++,
            syncStatusAction: () =>
            {
                syncCount++;
                return Task.CompletedTask;
            });

        // 1. Close -> Hide
        controller.OnWindowCloseRequested(out var cancel1);
        Assert.True(cancel1);
        Assert.False(controller.IsWindowVisible);
        Assert.Equal(1, hideCount);

        // 2. Open -> Restore & Sync
        await controller.RequestOpenWindowAsync();
        Assert.True(controller.IsWindowVisible);
        Assert.Equal(1, restoreCount);
        Assert.Equal(1, syncCount);

        // 3. Close -> Hide
        controller.OnWindowCloseRequested(out var cancel2);
        Assert.True(cancel2);
        Assert.False(controller.IsWindowVisible);
        Assert.Equal(2, hideCount);

        // 4. Open -> Restore & Sync
        await controller.RequestOpenWindowAsync();
        Assert.True(controller.IsWindowVisible);
        Assert.Equal(2, restoreCount);
        Assert.Equal(2, syncCount);

        Assert.False(controller.IsExited);
    }

    [Fact]
    public async Task StopWallpaper_CallsRuntime_ClearsActiveWallpaper_KeepsAppAlive()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName,
            onCommandHandler: req =>
            {
                if (req.Command == IpcConstants.StopWallpaperCommand)
                {
                    return Task.FromResult(new IpcResponse { Success = true, Message = "Wallpaper stopped." });
                }
                return Task.FromResult(new IpcResponse { Success = false });
            });
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var exitCalled = false;

        var controller = new TrayLifecycleController(
            client,
            exitAppAction: () => exitCalled = true);

        controller.SetActiveWallpaper(@"C:\dummy\sample.mp4", IpcConstants.WallpaperTypeVideo);
        Assert.Equal(@"C:\dummy\sample.mp4", controller.ActiveWallpaperPath);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, controller.ActiveWallpaperType);

        var result = await controller.RequestStopWallpaperAsync(timeoutMs: 3000);

        Assert.True(result.Success);
        Assert.Null(controller.ActiveWallpaperPath);
        Assert.Equal(IpcConstants.WallpaperTypeNone, controller.ActiveWallpaperType);
        Assert.False(controller.IsExited);
        Assert.False(exitCalled);

        // Verify runtime server still responds to ping
        var ping = await client.PingAsync(timeoutMs: 2000);
        Assert.True(ping.Success);
    }

    [Fact]
    public async Task Exit_DoesNotSendStopWallpaper_SendsShutdownToRuntime_AndExitsApp()
    {
        var pipeName = CreateUniquePipeName();
        var stopWallpaperCalled = false;
        var shutdownTcs = new TaskCompletionSource<bool>();

        using var server = new RuntimeIpcServer(pipeName,
            onShutdownRequested: () => shutdownTcs.TrySetResult(true),
            onCommandHandler: req =>
            {
                if (req.Command == IpcConstants.StopWallpaperCommand)
                {
                    stopWallpaperCalled = true;
                    return Task.FromResult(new IpcResponse { Success = true, Message = "Stopped." });
                }
                return Task.FromResult(new IpcResponse { Success = false });
            });
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var exitAppCalled = false;

        var controller = new TrayLifecycleController(
            client,
            exitAppAction: () => exitAppCalled = true);

        controller.SetActiveWallpaper(@"C:\dummy\sample.png", IpcConstants.WallpaperTypeImage);

        await controller.RequestExitAsync(stopTimeoutMs: 2000, shutdownTimeoutMs: 2000);

        Assert.True(controller.IsExited);
        Assert.False(stopWallpaperCalled, "Exit NiceWP must NOT send StopWallpaper IPC command so persisted settings remain intact.");

        var shutdownCompleted = await Task.WhenAny(shutdownTcs.Task, Task.Delay(3000));
        Assert.Same(shutdownTcs.Task, shutdownCompleted);
        Assert.True(await shutdownTcs.Task, "Shutdown must be sent to runtime host.");
        Assert.True(exitAppCalled, "App exit action must be triggered.");

        // Subsequent close requests must not be canceled now that app is exiting
        controller.OnWindowCloseRequested(out var cancelClose);
        Assert.False(cancelClose);
    }

    [Fact]
    public async Task StopWallpaper_PersistsNoneStateInSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_TrayStop_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "wall.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

            var settingsPath = Path.Combine(tempDir, "settings.json");
            var settingsService = new SettingsService(settingsPath);
            var pipeName = CreateUniquePipeName();

            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => new TrackingRenderer(),
                () => new TrackingRenderer(),
                settingsService);
            using var hostApp = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            hostApp.Start();
            await hostApp.RestoreTask;

            var client = new RuntimeIpcClient(pipeName);
            var setResult = await client.SetImageAsync(imagePath);
            Assert.True(setResult.Success);

            var controller = new TrayLifecycleController(client);
            controller.SetActiveWallpaper(imagePath, IpcConstants.WallpaperTypeImage);

            var stopResult = await controller.RequestStopWallpaperAsync(timeoutMs: 3000);
            Assert.True(stopResult.Success);

            var persisted = await settingsService.LoadAsync();
            Assert.Equal(IpcConstants.WallpaperTypeNone, persisted.LastWallpaperType);
            Assert.Null(persisted.LastWallpaperPath);
            Assert.Null(hostApp.Composition.WallpaperService.ActivePath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Exit_WithActiveImageWallpaper_PreservesPersistedImage_CleansUpRuntime_AndRestoresOnNextStart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_TrayExitImg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "persisted_exit.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

            var settingsPath = Path.Combine(tempDir, "settings.json");
            var settingsService = new SettingsService(settingsPath);
            var pipeName1 = CreateUniquePipeName();

            TrackingRenderer? activeRenderer = null;
            using (var composition1 = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => activeRenderer = new TrackingRenderer(),
                () => new TrackingRenderer(),
                settingsService))
            using (var host1 = new RuntimeHostApp(composition: composition1, pipeName: pipeName1))
            {
                var runTask = Task.Run(() => host1.Run());
                var client1 = new RuntimeIpcClient(pipeName1);

                for (var i = 0; i < 20; i++)
                {
                    if ((await client1.PingAsync(250)).Success) break;
                    await Task.Delay(50);
                }

                var setResponse = await client1.SetImageAsync(imagePath);
                Assert.True(setResponse.Success);
                Assert.NotNull(activeRenderer);
                Assert.Equal(0, activeRenderer!.DisposeCount);

                var exitCalled = false;
                var controller = new TrayLifecycleController(
                    client1,
                    exitAppAction: () => exitCalled = true);
                controller.SetActiveWallpaper(imagePath, IpcConstants.WallpaperTypeImage);

                // Tray -> Exit NiceWP
                await controller.RequestExitAsync(stopTimeoutMs: 1500, shutdownTimeoutMs: 2000);
                Assert.True(exitCalled);

                var exitCode = await runTask;
                Assert.Equal(0, exitCode);

                // Runtime cleanup must have been performed during shutdown
                Assert.Null(host1.Composition.WallpaperService.ActivePath);
                Assert.Equal(1, activeRenderer.DisposeCount);
            }

            // Persisted settings must still hold the image path
            var persisted = await settingsService.LoadAsync();
            Assert.True(persisted.RestoreLastWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, persisted.LastWallpaperType);
            Assert.Equal(Path.GetFullPath(imagePath), persisted.LastWallpaperPath);

            // Restarting Runtime Host must automatically restore the image
            var pipeName2 = CreateUniquePipeName();
            using var composition2 = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => new TrackingRenderer(),
                () => new TrackingRenderer(),
                settingsService);
            using var host2 = new RuntimeHostApp(composition: composition2, pipeName: pipeName2);
            host2.Start();

            var client2 = new RuntimeIpcClient(pipeName2);
            var status = await client2.GetStatusAsync(timeoutMs: 3000);
            Assert.True(status.Success);
            Assert.True(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, status.WallpaperType);
            Assert.Equal(Path.GetFullPath(imagePath), status.ActivePath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Exit_WithActiveVideoWallpaper_PreservesPersistedVideo_CleansUpRuntime_AndRestoresOnNextStart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_TrayExitVid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var videoPath = Path.Combine(tempDir, "persisted_exit.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);

            var settingsPath = Path.Combine(tempDir, "settings.json");
            var settingsService = new SettingsService(settingsPath);
            var pipeName1 = CreateUniquePipeName();

            TrackingRenderer? activeVideoRenderer = null;
            using (var composition1 = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => new TrackingRenderer(),
                () => activeVideoRenderer = new TrackingRenderer(),
                settingsService))
            using (var host1 = new RuntimeHostApp(composition: composition1, pipeName: pipeName1))
            {
                var runTask = Task.Run(() => host1.Run());
                var client1 = new RuntimeIpcClient(pipeName1);

                for (var i = 0; i < 20; i++)
                {
                    if ((await client1.PingAsync(250)).Success) break;
                    await Task.Delay(50);
                }

                var setResponse = await client1.SetVideoAsync(videoPath);
                Assert.True(setResponse.Success);
                Assert.NotNull(activeVideoRenderer);
                Assert.Equal(0, activeVideoRenderer!.DisposeCount);

                var exitCalled = false;
                var controller = new TrayLifecycleController(
                    client1,
                    exitAppAction: () => exitCalled = true);
                controller.SetActiveWallpaper(videoPath, IpcConstants.WallpaperTypeVideo);

                // Tray -> Exit NiceWP
                await controller.RequestExitAsync(stopTimeoutMs: 1500, shutdownTimeoutMs: 2000);
                Assert.True(exitCalled);

                var exitCode = await runTask;
                Assert.Equal(0, exitCode);

                // Runtime cleanup must have been performed during shutdown
                Assert.Null(host1.Composition.WallpaperService.ActivePath);
                Assert.Equal(1, activeVideoRenderer.DisposeCount);
            }

            // Persisted settings must still hold the video path
            var persisted = await settingsService.LoadAsync();
            Assert.True(persisted.RestoreLastWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, persisted.LastWallpaperType);
            Assert.Equal(Path.GetFullPath(videoPath), persisted.LastWallpaperPath);

            // Restarting Runtime Host must automatically restore the video
            var pipeName2 = CreateUniquePipeName();
            using var composition2 = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => new TrackingRenderer(),
                () => new TrackingRenderer(),
                settingsService);
            using var host2 = new RuntimeHostApp(composition: composition2, pipeName: pipeName2);
            host2.Start();

            var client2 = new RuntimeIpcClient(pipeName2);
            var status = await client2.GetStatusAsync(timeoutMs: 3000);
            Assert.True(status.Success);
            Assert.True(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, status.WallpaperType);
            Assert.Equal(Path.GetFullPath(videoPath), status.ActivePath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownIpc_DoesNotModifySettingsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_ShutdownSettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "keep_on_shutdown.jpg");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

            var settingsPath = Path.Combine(tempDir, "settings.json");
            var settingsService = new SettingsService(settingsPath);
            var pipeName = CreateUniquePipeName();

            using var composition = new RuntimeCompositionRoot(
                new TestDesktopHost(),
                () => new TrackingRenderer(),
                () => new TrackingRenderer(),
                settingsService);
            using var host = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => host.Run());

            var client = new RuntimeIpcClient(pipeName);
            for (var i = 0; i < 20; i++)
            {
                if ((await client.PingAsync(250)).Success) break;
                await Task.Delay(50);
            }

            Assert.True((await client.SetImageAsync(imagePath)).Success);

            var fixedTimestamp = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(settingsPath, fixedTimestamp);

            var shutdownResp = await client.ShutdownAsync(timeoutMs: 3000);
            Assert.True(shutdownResp.Success);
            await runTask;

            Assert.Equal(fixedTimestamp, File.GetLastWriteTimeUtc(settingsPath));
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
    public async Task Exit_WhenRuntimeFails_StillExitsAppWithoutHanging()
    {
        // Unresponsive / non-existent pipe client
        var unreachablePipe = CreateUniquePipeName();
        var client = new RuntimeIpcClient(unreachablePipe);
        var exitAppCalled = false;

        var controller = new TrayLifecycleController(
            client,
            exitAppAction: () => exitAppCalled = true);

        controller.SetActiveWallpaper(@"C:\dummy\image.png", IpcConstants.WallpaperTypeImage);

        // Should not throw or hang indefinitely
        await controller.RequestExitAsync(stopTimeoutMs: 200, shutdownTimeoutMs: 200);

        Assert.True(controller.IsExited);
        Assert.True(exitAppCalled, "App exit must proceed even if IPC times out.");
    }

    [Fact]
    public void AppSingleInstanceGuard_FirstInstanceSucceeds_SecondFails_DisposalAllowsNew()
    {
        var mutexName = $@"Local\NiceWP.Test.AppSingleInstance.{Guid.NewGuid():N}";

        using (var guard1 = new AppSingleInstanceGuard(mutexName))
        {
            Assert.True(guard1.IsSingleInstance, "First instance must acquire single instance mutex.");

            using (var guard2 = new AppSingleInstanceGuard(mutexName))
            {
                Assert.False(guard2.IsSingleInstance, "Second instance must NOT acquire single instance mutex.");
            }
        }

        // After guard1 is disposed, a new guard with the same mutex name should succeed
        using (var guard3 = new AppSingleInstanceGuard(mutexName))
        {
            Assert.True(guard3.IsSingleInstance, "After disposing first instance, a new instance must acquire mutex.");
        }
    }

    [Fact]
    public async Task RealHostProcess_TrayLifecycle_StopAndExit()
    {
        var hostExe = RuntimeHostLauncher.ResolveHostExecutablePath();
        if (hostExe == null || !File.Exists(hostExe))
        {
            return;
        }

        var defaultSettingsPath = SettingsService.GetDefaultSettingsPath();
        var hadExistingSettings = File.Exists(defaultSettingsPath);
        var existingSettingsBytes = hadExistingSettings ? await File.ReadAllBytesAsync(defaultSettingsPath) : null;

        try
        {
            var client = new RuntimeIpcClient();
            var launcher = new RuntimeHostLauncher(client, hostExe);

            var launchResult = await launcher.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);
            Assert.True(launchResult.Success, $"Host failed to start: {launchResult.Message}");

            var exitAppCalled = false;
            var controller = new TrayLifecycleController(
                client,
                exitAppAction: () => exitAppCalled = true);

            // 1. Simulate tray Stop Wallpaper
            var stopResult = await controller.RequestStopWallpaperAsync(timeoutMs: 2000);
            Assert.True(stopResult.Success);
            Assert.Null(controller.ActiveWallpaperPath);
            Assert.Equal(IpcConstants.WallpaperTypeNone, controller.ActiveWallpaperType);
            Assert.False(controller.IsExited);

            // Host must STILL be running!
            var ping = await client.PingAsync(timeoutMs: 1000);
            Assert.True(ping.Success, "Runtime host must remain running after Stop Wallpaper.");

            // 2. Simulate tray Exit
            await controller.RequestExitAsync(stopTimeoutMs: 1000, shutdownTimeoutMs: 2000);
            Assert.True(controller.IsExited);
            Assert.True(exitAppCalled);

            // Allow Host to exit
            await Task.Delay(300);

            // Host must now be shut down!
            var pingAfterExit = await client.PingAsync(timeoutMs: 500);
            Assert.False(pingAfterExit.Success, "Runtime host must be shut down after tray Exit.");
        }
        finally
        {
            try
            {
                if (hadExistingSettings && existingSettingsBytes != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(defaultSettingsPath)!);
                    await File.WriteAllBytesAsync(defaultSettingsPath, existingSettingsBytes);
                }
                else if (!hadExistingSettings && File.Exists(defaultSettingsPath))
                {
                    File.Delete(defaultSettingsPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
