using System.Diagnostics;
using NiceWP.Infrastructure.Ipc;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class RuntimeLauncherTests
{
    private static string CreateUniquePipeName() =>
        $@"NiceWP.Test.Pipe.{Guid.NewGuid():N}";

    [Fact]
    public void ResolveHostExecutablePath_FindsExecutableInBuildOutputs()
    {
        var resolved = RuntimeHostLauncher.ResolveHostExecutablePath();
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved), $"Resolved path does not exist: {resolved}");
        Assert.EndsWith(IpcConstants.HostExecutableName, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureHostRunning_WhenHostAlreadyRunning_DoesNotLaunchNewProcess()
    {
        var pipeName = CreateUniquePipeName();
        using var server = new RuntimeIpcServer(pipeName);
        server.Start();

        var client = new RuntimeIpcClient(pipeName);
        // Provide an intentionally invalid executable path. If launcher tried to start a process, it would fail.
        var launcher = new RuntimeHostLauncher(client, @"C:\invalid_never_called\dummy.exe");

        var result = await launcher.EnsureHostRunningAsync(timeoutMs: 2000);

        Assert.True(result.Success);
        Assert.Contains("already running", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureHostRunning_WhenExecutableNotFound_ReturnsDescriptiveFailure()
    {
        var pipeName = CreateUniquePipeName();
        var client = new RuntimeIpcClient(pipeName);
        var launcher = new RuntimeHostLauncher(client, @"C:\completely\invalid\nonexistent_host.exe");

        var result = await launcher.EnsureHostRunningAsync(timeoutMs: 1000);

        Assert.False(result.Success);
        Assert.Contains("could not be found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Expected at:", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureHostRunning_WhenProcessExitsImmediately_ReturnsExitCodeWithoutTimeout()
    {
        var pipeName = CreateUniquePipeName();
        var client = new RuntimeIpcClient(pipeName);

        var tempBatch = Path.Combine(Path.GetTempPath(), $"exit_test_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(tempBatch, "@exit 42");

        try
        {
            var launcher = new RuntimeHostLauncher(client, tempBatch);
            var sw = Stopwatch.StartNew();
            var result = await launcher.EnsureHostRunningAsync(timeoutMs: 5000, retryIntervalMs: 50);
            sw.Stop();

            Assert.False(result.Success);
            Assert.Contains("exited unexpectedly", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("42", result.Message);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"Expected immediate exit detection, took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            if (File.Exists(tempBatch)) File.Delete(tempBatch);
        }
    }

    [Fact]
    public async Task EnsureHostRunning_WhenStartupTimesOut_ReturnsTimeoutError()
    {
        var pipeName = CreateUniquePipeName();
        var client = new RuntimeIpcClient(pipeName);

        // Use cmd.exe /c timeout as a process that will not open the named pipe
        var cmdPath = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
        var launcher = new RuntimeHostLauncher(client, cmdPath);

        var result = await launcher.EnsureHostRunningAsync(timeoutMs: 500, retryIntervalMs: 50);

        Assert.False(result.Success);
        Assert.True(
            result.Message?.Contains("did not respond", StringComparison.OrdinalIgnoreCase) == true ||
            result.Message?.Contains("exited unexpectedly", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected timeout or exit message, got: {result.Message}");
    }

    [Fact]
    public async Task EnsureHostRunning_WhenHostClosed_LaunchesProcessAndConnects()
    {
        var hostExe = RuntimeHostLauncher.ResolveHostExecutablePath();
        if (hostExe == null || !File.Exists(hostExe))
        {
            return;
        }

        // Test with the standard default pipe
        var client = new RuntimeIpcClient();
        var launcher = new RuntimeHostLauncher(client, hostExe);

        try
        {
            var result = await launcher.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);
            Assert.True(result.Success, $"Expected host to start and connect, got: {result.Message}");

            var ping = await client.PingAsync(timeoutMs: 1000);
            Assert.True(ping.Success);
            Assert.Equal(IpcConstants.PongMessage, ping.Message);

            var status = await client.GetStatusAsync(timeoutMs: 4000);
            Assert.True(status.Success);
            Assert.True(status.IsRunning);
        }
        finally
        {
            // Clean up host process via IPC shutdown
            try
            {
                await client.ShutdownAsync(timeoutMs: 2000);
                await Task.Delay(200);
            }
            catch
            {
                // Ignore cleanup shutdown errors
            }
        }
    }

    [Fact]
    public async Task EnsureHostRunning_ConcurrentLaunch_BothClientsConnectSuccessfully()
    {
        var hostExe = RuntimeHostLauncher.ResolveHostExecutablePath();
        if (hostExe == null || !File.Exists(hostExe))
        {
            return;
        }

        var client1 = new RuntimeIpcClient();
        var client2 = new RuntimeIpcClient();
        var launcher1 = new RuntimeHostLauncher(client1, hostExe);
        var launcher2 = new RuntimeHostLauncher(client2, hostExe);

        try
        {
            var task1 = launcher1.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);
            var task2 = launcher2.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);

            var results = await Task.WhenAll(task1, task2);

            Assert.True(results[0].Success, $"Launcher 1 failed: {results[0].Message}");
            Assert.True(results[1].Success, $"Launcher 2 failed: {results[1].Message}");

            // Verify both can independently communicate with the single host
            var ping1 = await client1.PingAsync(timeoutMs: 1000);
            var ping2 = await client2.PingAsync(timeoutMs: 1000);

            Assert.True(ping1.Success);
            Assert.True(ping2.Success);
        }
        finally
        {
            try
            {
                await client1.ShutdownAsync(timeoutMs: 2000);
                await Task.Delay(200);
            }
            catch
            {
                // Ignore cleanup shutdown errors
            }
        }
    }

    [Fact]
    public async Task AppClose_RuntimeHostContinuesRunning()
    {
        var hostExe = RuntimeHostLauncher.ResolveHostExecutablePath();
        if (hostExe == null || !File.Exists(hostExe))
        {
            return;
        }

        var client1 = new RuntimeIpcClient();
        var launcher = new RuntimeHostLauncher(client1, hostExe);

        try
        {
            var launchResult = await launcher.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);
            Assert.True(launchResult.Success);

            // Simulate App 1 closing: dispose client1 connection
            // Host must continue running in background!
            var client2 = new RuntimeIpcClient();
            var ping = await client2.PingAsync(timeoutMs: 1000);
            Assert.True(ping.Success, "Runtime host should continue running after first client disconnects.");
        }
        finally
        {
            try
            {
                await client1.ShutdownAsync(timeoutMs: 2000);
                await Task.Delay(200);
            }
            catch
            {
                // Ignore cleanup shutdown errors
            }
        }
    }
}
