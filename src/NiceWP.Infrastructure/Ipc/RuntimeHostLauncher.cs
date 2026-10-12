using System.Diagnostics;

namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Manages runtime host discovery, background process launching, and readiness polling.
/// Ensures single-instance cooperation and graceful fallback handling without GUI or shell windows.
/// </summary>
public sealed class RuntimeHostLauncher
{
    private readonly RuntimeIpcClient ipcClient;
    private readonly string? customHostExecutablePath;
    private readonly SemaphoreSlim ensureGate = new(1, 1);

    public RuntimeIpcClient IpcClient => ipcClient;
    public string? CustomHostExecutablePath => customHostExecutablePath;

    public RuntimeHostLauncher(RuntimeIpcClient? ipcClient = null, string? customHostExecutablePath = null)
    {
        this.ipcClient = ipcClient ?? new RuntimeIpcClient();
        this.customHostExecutablePath = customHostExecutablePath;
    }

    /// <summary>
    /// Resolves the absolute path to the NiceWP.Runtime.Host executable.
    /// Checks explicit custom path, application distribution directory, and known build output locations.
    /// </summary>
    public static string? ResolveHostExecutablePath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return File.Exists(customPath) ? Path.GetFullPath(customPath) : null;
        }

        var baseDir = AppContext.BaseDirectory;

        // 1. Primary check: Application distribution directory (unpackaged distribution layout)
        var primaryPath = Path.Combine(baseDir, IpcConstants.HostExecutableName);
        if (File.Exists(primaryPath))
        {
            return Path.GetFullPath(primaryPath);
        }

        // 2. Secondary check: Relative build output paths for development and unit testing
        var relativeCandidates = new[]
        {
            @"..\..\..\..\..\NiceWP.Runtime.Host\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\NiceWP.Runtime.Host\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\NiceWP.Runtime.Host\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\NiceWP.Runtime.Host\bin\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\..\src\NiceWP.Runtime.Host\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\..\..\src\NiceWP.Runtime.Host\bin\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\src\NiceWP.Runtime.Host\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\src\NiceWP.Runtime.Host\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe",
            @"..\..\..\..\src\NiceWP.Runtime.Host\bin\Release\net10.0-windows10.0.19041.0\NiceWP.Runtime.Host.exe"
        };

        foreach (var candidate in relativeCandidates)
        {
            try
            {
                var resolved = Path.GetFullPath(Path.Combine(baseDir, candidate));
                if (File.Exists(resolved))
                {
                    return resolved;
                }
            }
            catch
            {
                // Ignore resolution path errors for candidates
            }
        }

        // 3. Hierarchical upward search for repo structure
        try
        {
            var current = new DirectoryInfo(baseDir);
            for (int i = 0; i < 7 && current != null; i++)
            {
                var probePaths = new[]
                {
                    Path.Combine(current.FullName, IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "src", "NiceWP.Runtime.Host", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "src", "NiceWP.Runtime.Host", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "src", "NiceWP.Runtime.Host", "bin", "Debug", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "src", "NiceWP.Runtime.Host", "bin", "Release", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "NiceWP.Runtime.Host", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, "NiceWP.Runtime.Host", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, ".artifacts", "dist", "NiceWP-0.0.6-win-x64", IpcConstants.HostExecutableName),
                    Path.Combine(current.FullName, ".artifacts", "dist", "NiceWP-0.0.5-win-x64", IpcConstants.HostExecutableName)
                };

                foreach (var probe in probePaths)
                {
                    if (File.Exists(probe))
                    {
                        return Path.GetFullPath(probe);
                    }
                }

                current = current.Parent;
            }
        }
        catch
        {
            // Ignore directory traversal errors
        }

        return null;
    }

    /// <summary>
    /// Launches the runtime host executable with a hidden, windowless process configuration.
    /// </summary>
    public static Process StartHostProcess(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Runtime host executable was not found.", fullPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start the runtime host process.");
        }

        return process;
    }

    /// <summary>
    /// Ensures that the Runtime Host process is running and accepting IPC requests.
    /// If already responsive, returns immediately without starting a new instance.
    /// If not running, launches the host in the background and polls IPC until ready.
    /// </summary>
    public async Task<IpcResponse> EnsureHostRunningAsync(
        int timeoutMs = IpcConstants.DefaultHostStartupTimeoutMs,
        int retryIntervalMs = IpcConstants.DefaultHostStartupRetryIntervalMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Fail(string.Empty, "Host startup check was cancelled.");
        }

        try
        {
            return await EnsureHostRunningCoreAsync(timeoutMs, retryIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ensureGate.Release();
        }
    }

    private async Task<IpcResponse> EnsureHostRunningCoreAsync(
        int timeoutMs,
        int retryIntervalMs,
        CancellationToken cancellationToken)
    {
        // 1. Initial quick check: Host might already be active
        var initialPing = await ipcClient.PingAsync(
            timeoutMs: IpcConstants.DefaultInitialPingTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (initialPing.Success)
        {
            return IpcResponse.Ok(initialPing.RequestId, "Runtime host is already running.");
        }

        // 2. Resolve executable path
        var exePath = ResolveHostExecutablePath(customHostExecutablePath);
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            var primaryPath = Path.Combine(AppContext.BaseDirectory, IpcConstants.HostExecutableName);
            return IpcResponse.Fail(
                string.Empty,
                $"Runtime host executable could not be found. Expected at: '{primaryPath}'.");
        }

        // 3. Start host process
        Process? process = null;
        try
        {
            process = StartHostProcess(exePath);
        }
        catch (Exception exception)
        {
            return IpcResponse.Fail(
                string.Empty,
                $"Failed to start runtime host process: {exception.Message}");
        }

        try
        {
            // 4. Poll IPC endpoint with backoff until ready or timeout
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeoutMs);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs && !linkedCts.IsCancellationRequested)
            {
                // If the process exited with a non-zero exit code, it crashed - fail immediately unless another host is responsive
                if (process.HasExited && process.ExitCode != 0)
                {
                    var fallbackPing = await ipcClient.PingAsync(timeoutMs: retryIntervalMs, cancellationToken: linkedCts.Token).ConfigureAwait(false);
                    if (fallbackPing.Success)
                    {
                        return IpcResponse.Ok(fallbackPing.RequestId, "Runtime host started and connected.");
                    }

                    return IpcResponse.Fail(
                        string.Empty,
                        $"Runtime host process exited unexpectedly with code {process.ExitCode} before becoming responsive.");
                }

                try
                {
                    await Task.Delay(retryIntervalMs, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (process.HasExited && process.ExitCode != 0)
                {
                    var fallbackPing = await ipcClient.PingAsync(timeoutMs: retryIntervalMs, cancellationToken: linkedCts.Token).ConfigureAwait(false);
                    if (fallbackPing.Success)
                    {
                        return IpcResponse.Ok(fallbackPing.RequestId, "Runtime host started and connected.");
                    }

                    return IpcResponse.Fail(
                        string.Empty,
                        $"Runtime host process exited unexpectedly with code {process.ExitCode} before becoming responsive.");
                }

                var ping = await ipcClient.PingAsync(
                    timeoutMs: retryIntervalMs,
                    cancellationToken: linkedCts.Token).ConfigureAwait(false);

                if (ping.Success)
                {
                    return IpcResponse.Ok(ping.RequestId, "Runtime host started and connected.");
                }
            }

            // 5. Timeout reached or cancelled
            if (process.HasExited)
            {
                return IpcResponse.Fail(
                    string.Empty,
                    $"Runtime host process exited with code {process.ExitCode} before becoming responsive.");
            }

            return IpcResponse.Fail(
                string.Empty,
                $"Runtime host started but did not respond to IPC within {timeoutMs}ms.");
        }
        finally
        {
            process.Dispose();
        }
    }
}
