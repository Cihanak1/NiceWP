using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.SingleInstance;
using NiceWP.Infrastructure.Tray;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class TrayLifecycleTests
{
    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.TrayPipe.{Guid.NewGuid():N}";

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
    public async Task Exit_StopsActiveWallpaper_SendsShutdownToRuntime_AndExitsApp()
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
        Assert.True(stopWallpaperCalled, "Active wallpaper must be stopped prior to exit.");

        var shutdownCompleted = await Task.WhenAny(shutdownTcs.Task, Task.Delay(3000));
        Assert.Same(shutdownTcs.Task, shutdownCompleted);
        Assert.True(await shutdownTcs.Task, "Shutdown must be sent to runtime host.");
        Assert.True(exitAppCalled, "App exit action must be triggered.");

        // Subsequent close requests must not be canceled now that app is exiting
        controller.OnWindowCloseRequested(out var cancelClose);
        Assert.False(cancelClose);
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
}
