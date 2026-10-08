using System.Diagnostics;
using System.IO.Pipes;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class IpcTests
{
    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.Pipe.{Guid.NewGuid():N}";

    private static async Task WaitForReadyAsync(RuntimeIpcClient client)
    {
        for (var i = 0; i < 30; i++)
        {
            var ping = await client.PingAsync(timeoutMs: 250);
            if (ping.Success) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Runtime host did not become ready for IPC.");
    }

    private sealed class TestHost : IDesktopHost
    {
        public bool Valid { get; set; } = true;
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopHostResult(10, DesktopHostError.None));
        public bool IsValid(nint handle) => Valid;
        public nint Attach(nint host, nint window) => 0;
        public void Detach(nint host, nint window, nint previousParent) { }
    }

    private sealed class TestRenderer : IWallpaperRenderer
    {
        public nint WindowHandle => 42;
        public int DisposeCount { get; private set; }
        public Task<WallpaperResult> LoadAsync(string path) => Task.FromResult(new WallpaperResult());
        public void Show() { }
        public void Dispose() => DisposeCount++;
    }

    private static RuntimeCompositionRoot CreateTestComposition() =>
        new(new TestHost(), () => new TestRenderer(), () => new TestRenderer());

    [Fact]
    public async Task Ping_ReturnsSuccessWithPongMessage()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var response = await client.PingAsync(timeoutMs: 3000);

        Assert.True(response.Success);
        Assert.Equal(IpcConstants.PongMessage, response.Message);
        Assert.False(string.IsNullOrEmpty(response.RequestId));
    }

    [Fact]
    public async Task UnknownCommand_ReturnsFailureWithDescriptiveError()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var response = await client.SendCommandAsync("NonExistentCommand", timeoutMs: 3000);

        Assert.False(response.Success);
        Assert.Contains("Unknown command", response.Message);
    }

    [Fact]
    public async Task MalformedRequest_InvalidJson_ReturnsFailureAndServerStaysResponsive()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        var client = new RuntimeIpcClient(pipeName);

        var malformedResponse = await client.SendRawAsync("test-bad", "{bad-json-format", timeoutMs: 3000);
        Assert.False(malformedResponse.Success);
        Assert.Contains("Malformed request", malformedResponse.Message);

        var pingResponse = await client.PingAsync(timeoutMs: 3000);
        Assert.True(pingResponse.Success);
        Assert.Equal(IpcConstants.PongMessage, pingResponse.Message);
    }

    [Fact]
    public async Task MalformedRequest_OversizedPayload_ReturnsFailure()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        var client = new RuntimeIpcClient(pipeName);

        var largeString = new string('A', IpcConstants.MaxMessageLength + 100);
        var response = await client.SendRawAsync("test-oversized", largeString, timeoutMs: 3000);

        Assert.False(response.Success);
        Assert.Contains("exceeds", response.Message, StringComparison.OrdinalIgnoreCase);

        var pingResponse = await client.PingAsync(timeoutMs: 3000);
        Assert.True(pingResponse.Success);
    }

    [Fact]
    public async Task RuntimeUnavailable_ReturnsControlledFailureWithoutCrashing()
    {
        var nonExistentPipe = CreateUniquePipeName();
        var client = new RuntimeIpcClient(nonExistentPipe);

        var response = await client.PingAsync(timeoutMs: 250);

        Assert.False(response.Success);
        Assert.Contains("unavailable", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientDisconnect_ServerRecoversAndAcceptsSubsequentClient()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        using (var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await rawClient.ConnectAsync(2000);
            Assert.True(rawClient.IsConnected);
        }

        await Task.Delay(100);

        var client = new RuntimeIpcClient(pipeName);
        var response = await client.PingAsync(timeoutMs: 3000);

        Assert.True(response.Success);
        Assert.Equal(IpcConstants.PongMessage, response.Message);
    }

    [Fact]
    public async Task ShutdownCommand_TriggersShutdownCallbackAndReturnsSuccess()
    {
        var pipeName = CreateUniquePipeName();
        var shutdownTcs = new TaskCompletionSource<bool>();

        using var server = new RuntimeIpcServer(pipeName, onShutdownRequested: () => shutdownTcs.TrySetResult(true));
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        var response = await client.ShutdownAsync(timeoutMs: 3000);

        Assert.True(response.Success);
        Assert.Equal(IpcConstants.ShutdownMessage, response.Message);

        var completed = await Task.WhenAny(shutdownTcs.Task, Task.Delay(3000));
        Assert.Same(shutdownTcs.Task, completed);
        Assert.True(await shutdownTcs.Task);
    }

    [Fact]
    public async Task RuntimeHostApp_ExitsGracefullyWhenShutdownReceivedViaIpc()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        var pipeName = CreateUniquePipeName();

        using var guard = new SingleInstanceGuard(mutexName);
        using var app = new RuntimeHostApp(guard: guard, pipeName: pipeName);

        var runTask = Task.Run(() => app.Run());
        var client = new RuntimeIpcClient(pipeName);
        await WaitForReadyAsync(client);

        var shutdownResponse = await client.ShutdownAsync(timeoutMs: 3000);
        Assert.True(shutdownResponse.Success);

        var completed = await Task.WhenAny(runTask, Task.Delay(5000));
        Assert.Same(runTask, completed);
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task SetImage_ValidPath_AppliesImageWallpaper()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var response = await client.SetImageAsync(tempImage);
            Assert.True(response.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task SetVideo_ValidPath_AppliesVideoWallpaper()
    {
        var tempVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempVideo, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var response = await client.SetVideoAsync(tempVideo);
            Assert.True(response.Success);
            Assert.Equal(tempVideo, app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempVideo);
        }
    }

    [Fact]
    public async Task InvalidPath_ReturnsFailureAndPreservesCurrentWallpaper()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var initial = await client.SetImageAsync(tempImage);
            Assert.True(initial.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var emptyResult = await client.SetImageAsync("");
            Assert.False(emptyResult.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var nonexistentResult = await client.SetImageAsync(@"C:\nonexistent\image.png");
            Assert.False(nonexistentResult.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task WrongFileType_ReturnsFailureAndPreservesCurrentWallpaper()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        var tempTxt = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(tempImage, []);
        File.WriteAllBytes(tempTxt, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            await client.SetImageAsync(tempImage);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var wrongExt = await client.SetImageAsync(tempTxt);
            Assert.False(wrongExt.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var wrongForVideo = await client.SetVideoAsync(tempImage);
            Assert.False(wrongForVideo.Success);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
            File.Delete(tempTxt);
        }
    }

    [Fact]
    public async Task StopWallpaper_ClearsActiveWallpaper()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            await client.SetImageAsync(tempImage);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var stopResponse = await client.StopWallpaperAsync();
            Assert.True(stopResponse.Success);
            Assert.Null(app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task WallpaperReplace_ReplacesActiveWallpaperSeamlessly()
    {
        var firstImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        var videoFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var secondImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(firstImage, []);
        File.WriteAllBytes(videoFile, []);
        File.WriteAllBytes(secondImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            await client.SetImageAsync(firstImage);
            Assert.Equal(firstImage, app.Composition.WallpaperService.ActivePath);

            await client.SetVideoAsync(videoFile);
            Assert.Equal(videoFile, app.Composition.WallpaperService.ActivePath);

            await client.SetImageAsync(secondImage);
            Assert.Equal(secondImage, app.Composition.WallpaperService.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(firstImage);
            File.Delete(videoFile);
            File.Delete(secondImage);
        }
    }

    [Fact]
    public async Task ClientDisconnect_HostRemainsAliveAndAcceptsNewClient()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client1 = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client1);
            var response1 = await client1.SetImageAsync(tempImage);
            Assert.True(response1.Success);

            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);
            Assert.False(runTask.IsCompleted);

            var client2 = new RuntimeIpcClient(pipeName);
            var response2 = await client2.StopWallpaperAsync();
            Assert.True(response2.Success);
            Assert.Null(app.Composition.WallpaperService.ActivePath);

            await client2.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task Shutdown_StopsWallpaperAndCleansUp()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            await client.SetImageAsync(tempImage);
            Assert.Equal(tempImage, app.Composition.WallpaperService.ActivePath);

            var shutdownResponse = await client.ShutdownAsync();
            Assert.True(shutdownResponse.Success);

            var exitCode = await runTask;
            Assert.Equal(0, exitCode);
            Assert.Null(app.Composition.WallpaperService.ActivePath);
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task RealProcess_HostExecutable_RespondsToPingAndShutsDownCleanly()
    {
        var possiblePaths = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\NiceWP.Runtime.Host\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\src\NiceWP.Runtime.Host\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe"))
        };

        var exePath = possiblePaths.FirstOrDefault(File.Exists);
        if (exePath == null)
        {
            return;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);

        try
        {
            var client = new RuntimeIpcClient();

            IpcResponse? pingResponse = null;
            for (var i = 0; i < 30; i++)
            {
                pingResponse = await client.PingAsync(timeoutMs: 300);
                if (pingResponse.Success) break;
                await Task.Delay(100);
            }

            Assert.NotNull(pingResponse);
            Assert.True(pingResponse.Success, $"Ping failed: {pingResponse.Message}");
            Assert.Equal(IpcConstants.PongMessage, pingResponse.Message);

            var shutdownResponse = await client.ShutdownAsync(timeoutMs: 3000);
            Assert.True(shutdownResponse.Success);
            Assert.Equal(IpcConstants.ShutdownMessage, shutdownResponse.Message);

            var exited = process.WaitForExit(5000);
            Assert.True(exited, "NiceWP.Runtime.Host.exe did not exit within timeout.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            process.Dispose();
        }
    }

    [Fact]
    public async Task GetStatus_InitialState_ReturnsRunningWithNone()
    {
        var pipeName = CreateUniquePipeName();
        var composition = CreateTestComposition();
        using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
        var runTask = Task.Run(() => app.Run());

        var client = new RuntimeIpcClient(pipeName);
        await WaitForReadyAsync(client);

        var status = await client.GetStatusAsync();
        Assert.True(status.Success);
        Assert.True(status.IsRunning);
        Assert.False(status.HasActiveWallpaper);
        Assert.Equal(IpcConstants.WallpaperTypeNone, status.WallpaperType);
        Assert.Null(status.ActivePath);

        await client.ShutdownAsync();
        await runTask;
    }

    [Fact]
    public async Task GetStatus_AfterSetImage_ReturnsImageAndPath()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var setResult = await client.SetImageAsync(tempImage);
            Assert.True(setResult.Success);

            var status = await client.GetStatusAsync();
            Assert.True(status.Success);
            Assert.True(status.IsRunning);
            Assert.True(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, status.WallpaperType);
            Assert.Equal(tempImage, status.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task GetStatus_AfterSetVideo_ReturnsVideoAndPath()
    {
        var tempVideo = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempVideo, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var setResult = await client.SetVideoAsync(tempVideo);
            Assert.True(setResult.Success);

            var status = await client.GetStatusAsync();
            Assert.True(status.Success);
            Assert.True(status.IsRunning);
            Assert.True(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeVideo, status.WallpaperType);
            Assert.Equal(tempVideo, status.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempVideo);
        }
    }

    [Fact]
    public async Task GetStatus_AfterStopWallpaper_ReturnsNone()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            await client.SetImageAsync(tempImage);
            var stopResult = await client.StopWallpaperAsync();
            Assert.True(stopResult.Success);

            var status = await client.GetStatusAsync();
            Assert.True(status.Success);
            Assert.False(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeNone, status.WallpaperType);
            Assert.Null(status.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task GetStatus_AfterFailedWallpaperChange_PreservesPreviousState()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client);

            var setResult = await client.SetImageAsync(tempImage);
            Assert.True(setResult.Success);

            var failResult = await client.SetImageAsync(@"C:\nonexistent\bad_image.png");
            Assert.False(failResult.Success);

            var status1 = await client.GetStatusAsync();
            Assert.True(status1.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, status1.WallpaperType);
            Assert.Equal(tempImage, status1.ActivePath);

            var failVideo = await client.SetVideoAsync(tempImage);
            Assert.False(failVideo.Success);

            var status2 = await client.GetStatusAsync();
            Assert.True(status2.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, status2.WallpaperType);
            Assert.Equal(tempImage, status2.ActivePath);

            await client.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task GetStatus_AppReconnect_RetrievesExistingState()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempImage, []);
        try
        {
            var pipeName = CreateUniquePipeName();
            var composition = CreateTestComposition();
            using var app = new RuntimeHostApp(composition: composition, pipeName: pipeName);
            var runTask = Task.Run(() => app.Run());

            var client1 = new RuntimeIpcClient(pipeName);
            await WaitForReadyAsync(client1);
            var setResult = await client1.SetImageAsync(tempImage);
            Assert.True(setResult.Success);

            Assert.False(runTask.IsCompleted);

            var client2 = new RuntimeIpcClient(pipeName);
            var ping = await client2.PingAsync();
            Assert.True(ping.Success);

            var status = await client2.GetStatusAsync();
            Assert.True(status.Success);
            Assert.True(status.HasActiveWallpaper);
            Assert.Equal(IpcConstants.WallpaperTypeImage, status.WallpaperType);
            Assert.Equal(tempImage, status.ActivePath);

            await client2.ShutdownAsync();
            await runTask;
        }
        finally
        {
            File.Delete(tempImage);
        }
    }
}
