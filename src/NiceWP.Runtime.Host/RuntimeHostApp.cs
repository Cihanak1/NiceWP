using System.Diagnostics;
using Microsoft.UI.Xaml;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Manages runtime host lifecycle: single instance enforcement, WinUI dispatcher synchronization,
/// IPC server, and wallpaper service ownership.
/// </summary>
public sealed class RuntimeHostApp : IDisposable
{
    private readonly SingleInstanceGuard singleInstanceGuard;
    private readonly RuntimeCompositionRoot compositionRoot;
    private readonly IHostDispatcher dispatcher;
    private readonly IFullscreenWindowDetector fullscreenDetector;
    private readonly IPowerStatusProvider powerStatusProvider;
    private readonly RuntimeIpcServer ipcServer;
    private readonly RuntimePerformanceSampler sampler;
    private readonly ManualResetEventSlim shutdownEvent = new(false);
    private readonly Action? onShutdownAction;
    private readonly int restoreWaitTimeoutMs;
    private readonly int fullscreenPollIntervalMs;
    private readonly int powerPollIntervalMs;
    private CancellationTokenSource? policyCts;
    private volatile TaskCompletionSource<bool> policyWakeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RuntimeWallpaperState currentState = RuntimeWallpaperState.Empty;
    private Task restoreTask = Task.CompletedTask;
    private Task monitorTask = Task.CompletedTask;
    private Task powerMonitorTask = Task.CompletedTask;
    private volatile bool isManualPaused;
    private volatile bool isFullscreenPaused;
    private volatile bool isPowerPaused;
    private int commandSequence;
    private int shutdownRequested;
    private int wallpaperStoppedOnShutdown;
    private bool started;
    private bool disposed;

    public bool IsSingleInstance => singleInstanceGuard.IsSingleInstance;
    public bool IsShuttingDown => disposed || Volatile.Read(ref shutdownRequested) != 0;
    public RuntimeCompositionRoot Composition => compositionRoot;
    public SettingsService SettingsService => compositionRoot.SettingsService;
    public IHostDispatcher Dispatcher => dispatcher;
    public IFullscreenWindowDetector FullscreenDetector => fullscreenDetector;
    public IPowerStatusProvider PowerStatusProvider => powerStatusProvider;
    public RuntimeIpcServer IpcServer => ipcServer;
    public RuntimeWallpaperState CurrentState => currentState;
    public RuntimePerformanceSampler Sampler => sampler;
    public bool IsManualPaused => isManualPaused;
    public bool IsFullscreenPaused => isFullscreenPaused;
    public bool IsPowerPaused => isPowerPaused;
    public bool IsAutoPaused => !isManualPaused && (isFullscreenPaused || isPowerPaused);
    public bool ShouldBePaused => isManualPaused || isFullscreenPaused || isPowerPaused;
    public Task RestoreTask => restoreTask;
    public Task MonitorTask => monitorTask;
    public Task PowerMonitorTask => powerMonitorTask;

    public RuntimeHostApp(
        SingleInstanceGuard? guard = null,
        RuntimeCompositionRoot? composition = null,
        RuntimeIpcServer? server = null,
        IHostDispatcher? dispatcher = null,
        string? pipeName = null,
        int restoreWaitTimeoutMs = IpcConstants.DefaultRestoreWaitTimeoutMs,
        IFullscreenWindowDetector? fullscreenDetector = null,
        int fullscreenPollIntervalMs = IpcConstants.DefaultFullscreenPollIntervalMs,
        IPowerStatusProvider? powerStatusProvider = null,
        int powerPollIntervalMs = IpcConstants.DefaultPowerPollIntervalMs,
        Action? onShutdown = null,
        RuntimePerformanceSampler? sampler = null)
    {
        singleInstanceGuard = guard ?? (pipeName != null
            ? new SingleInstanceGuard($@"Local\NiceWP.Runtime.Host.{pipeName.Replace('\\', '.')}")
            : new SingleInstanceGuard());
        compositionRoot = composition ?? new RuntimeCompositionRoot();
        this.dispatcher = dispatcher ?? new CurrentThreadHostDispatcher();
        this.fullscreenDetector = fullscreenDetector ?? compositionRoot.FullscreenDetector;
        this.powerStatusProvider = powerStatusProvider ?? compositionRoot.PowerStatusProvider;
        this.onShutdownAction = onShutdown;
        this.restoreWaitTimeoutMs = restoreWaitTimeoutMs > 0
            ? restoreWaitTimeoutMs
            : IpcConstants.DefaultRestoreWaitTimeoutMs;
        this.fullscreenPollIntervalMs = fullscreenPollIntervalMs > 0
            ? fullscreenPollIntervalMs
            : IpcConstants.DefaultFullscreenPollIntervalMs;
        this.powerPollIntervalMs = powerPollIntervalMs > 0
            ? powerPollIntervalMs
            : IpcConstants.DefaultPowerPollIntervalMs;
        this.sampler = sampler ?? new RuntimePerformanceSampler(
            stateProvider: CaptureHostStateSnapshot);
        ipcServer = server ?? new RuntimeIpcServer(
            pipeName,
            onShutdownRequested: RequestShutdown,
            onCommandHandler: HandleCommandAsync);
    }

    public void Start()
    {
        if (!singleInstanceGuard.IsSingleInstance)
        {
            Trace.WriteLine("Another instance of NiceWP.Runtime.Host is already running. Controlled exit.");
            return;
        }

        if (started || IsShuttingDown)
        {
            return;
        }

        started = true;
        policyCts = new CancellationTokenSource();
        ipcServer.Start();
        sampler.Start();
        restoreTask = RestoreLastWallpaperCoreAsync();
        monitorTask = MonitorFullscreenPolicyLoopAsync(policyCts.Token);
        powerMonitorTask = MonitorPowerPolicyLoopAsync(policyCts.Token);
        Trace.WriteLine($"NiceWP.Runtime.Host started, pid={Environment.ProcessId}");
    }

    public int Run(CancellationToken cancellationToken = default)
    {
        if (!singleInstanceGuard.IsSingleInstance)
        {
            Trace.WriteLine("Another instance of NiceWP.Runtime.Host is already running. Controlled exit.");
            return 1;
        }

        Start();

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => RequestShutdown())
            : default;

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            RequestShutdown();
        }

        void OnProcessExit(object? sender, EventArgs args)
        {
            RequestShutdown();
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        try
        {
            shutdownEvent.Wait();
            Trace.WriteLine("NiceWP.Runtime.Host shutdown signaled.");
            StopWallpaperOnceForShutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Unexpected error in NiceWP.Runtime.Host: {exception}");
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    public async Task<IpcResponse> HandleCommandAsync(IpcRequest request)
    {
        if (request == null)
        {
            return IpcResponse.Fail(string.Empty, "Request cannot be null.");
        }

        if (string.Equals(request.Command, IpcConstants.GetStatusCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGetStatusAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.SetImageCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSetImageAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.SetVideoCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSetVideoAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.StopWallpaperCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleStopWallpaperAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.PauseWallpaperCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePauseWallpaperAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.ResumeWallpaperCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleResumeWallpaperAsync(request).ConfigureAwait(false);
        }

        if (string.Equals(request.Command, IpcConstants.GetPerformanceStatusCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGetPerformanceStatusAsync(request).ConfigureAwait(false);
        }

        return IpcResponse.Fail(request.Id, $"Unknown command: '{request.Command}'");
    }

    private async Task WaitForPendingRestoreAsync()
    {
        var pendingRestore = restoreTask;
        if (!pendingRestore.IsCompleted)
        {
            try
            {
                await Task.WhenAny(pendingRestore, Task.Delay(restoreWaitTimeoutMs)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error waiting for startup restore: {ex}");
            }
        }
    }

    private async Task CancelPendingRestoreAsync()
    {
        var pendingRestore = restoreTask;
        if (pendingRestore.IsCompleted)
        {
            return;
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (!currentState.HasActiveWallpaper)
                {
                    compositionRoot.WallpaperService.Stop();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error cancelling pending startup restore: {ex}");
        }

        await WaitForPendingRestoreAsync().ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleGetStatusAsync(IpcRequest request)
    {
        if (!IsShuttingDown)
        {
            await WaitForPendingRestoreAsync().ConfigureAwait(false);
        }

        var state = currentState;
        return IpcResponse.Status(
            requestId: request.Id,
            isRunning: !IsShuttingDown,
            hasActiveWallpaper: state.HasActiveWallpaper,
            wallpaperType: state.Type,
            activePath: state.Path,
            playbackState: state.PlaybackState,
            isPaused: state.IsPaused);
    }

    private async Task<IpcResponse> HandleGetPerformanceStatusAsync(IpcRequest request)
    {
        if (!IsShuttingDown)
        {
            await WaitForPendingRestoreAsync().ConfigureAwait(false);
        }

        try
        {
            var status = sampler.GetStatus();
            return IpcResponse.PerformanceStatus(request.Id, status);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error retrieving performance status: {ex}");
            return IpcResponse.Fail(request.Id, "Performance telemetry unavailable.");
        }
    }

    public RuntimeHostStateSnapshot CaptureHostStateSnapshot()
    {
        var state = currentState;
        var activeRenderer = compositionRoot.WallpaperService.ActiveRenderer;
        var isConnected = compositionRoot.WallpaperService.IsDesktopHostConnected;
        var isAlive = activeRenderer?.IsWindowAlive ?? false;

        string pauseReasons = "None";
        if (state.IsPaused)
        {
            var reasons = new List<string>();
            if (isManualPaused) reasons.Add("Manual");
            if (isFullscreenPaused) reasons.Add("Fullscreen");
            if (isPowerPaused) reasons.Add("Power");
            pauseReasons = reasons.Count > 0 ? string.Join(", ", reasons) : "Unknown";
        }

        return new RuntimeHostStateSnapshot(
            WallpaperType: state.Type,
            WallpaperState: state.PlaybackState,
            PauseReasons: pauseReasons,
            ActiveWallpaperPath: state.Path,
            DesktopHostConnected: isConnected,
            RendererWindowAlive: isAlive,
            SourceWidth: activeRenderer?.SourceWidth,
            SourceHeight: activeRenderer?.SourceHeight,
            DecodeWidth: activeRenderer?.DecodeWidth,
            DecodeHeight: activeRenderer?.DecodeHeight,
            ApproximateDecodedMemoryBytes: activeRenderer?.ApproximateDecodedMemoryBytes,
            VideoWidth: activeRenderer?.VideoWidth,
            VideoHeight: activeRenderer?.VideoHeight,
            PlaybackStatus: activeRenderer?.PlaybackStatus,
            PlaybackPosition: activeRenderer?.PlaybackPosition,
            PlaybackDuration: activeRenderer?.PlaybackDuration,
            FullscreenPauseActive: isFullscreenPaused,
            PowerPauseActive: isPowerPaused,
            ManualPauseActive: isManualPaused);
    }

    private async Task<IpcResponse> HandleSetImageAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return IpcResponse.Fail(request.Id, "Image path cannot be empty.");
        }

        var validation = ImageWallpaper.Validate(request.Path, out var fullPath);
        if (!validation.Success)
        {
            return IpcResponse.Fail(request.Id, validation.Message ?? "Invalid image path.");
        }

        var seq = Interlocked.Increment(ref commandSequence);
        await CancelPendingRestoreAsync().ConfigureAwait(false);

        return await dispatcher.InvokeAsync(async () =>
        {
            if (IsShuttingDown)
            {
                return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
            }

            var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
            if (result.Success)
            {
                if (Volatile.Read(ref commandSequence) == seq && !IsShuttingDown)
                {
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeImage, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeImage, fullPath).ConfigureAwait(false);
                }
                return IpcResponse.Ok(request.Id, $"Image wallpaper set: {fullPath}");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to set image wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleSetVideoAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return IpcResponse.Fail(request.Id, "Video path cannot be empty.");
        }

        var validation = VideoWallpaper.Validate(request.Path, out var fullPath);
        if (!validation.Success)
        {
            return IpcResponse.Fail(request.Id, validation.Message ?? "Invalid video path.");
        }

        var seq = Interlocked.Increment(ref commandSequence);
        await CancelPendingRestoreAsync().ConfigureAwait(false);

        return await dispatcher.InvokeAsync(async () =>
        {
            if (IsShuttingDown)
            {
                return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
            }

            var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
            if (result.Success)
            {
                if (Volatile.Read(ref commandSequence) == seq && !IsShuttingDown)
                {
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeVideo, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeVideo, fullPath).ConfigureAwait(false);
                }
                return IpcResponse.Ok(request.Id, $"Video wallpaper set: {fullPath}");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to set video wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleStopWallpaperAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Ok(request.Id, "Wallpaper stopped.");
        }

        var seq = Interlocked.Increment(ref commandSequence);

        var response = await dispatcher.InvokeAsync(async () =>
        {
            var result = compositionRoot.WallpaperService.Stop();
            if (result.Success)
            {
                ClearPauseReasons();
                currentState = RuntimeWallpaperState.Empty;
                if (Volatile.Read(ref commandSequence) == seq && !IsShuttingDown)
                {
                    await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeNone, null).ConfigureAwait(false);
                }
                return IpcResponse.Ok(request.Id, "Wallpaper stopped.");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to stop wallpaper.");
        }).ConfigureAwait(false);

        var pendingRestore = restoreTask;
        if (!pendingRestore.IsCompleted)
        {
            try
            {
                await Task.WhenAny(pendingRestore, Task.Delay(Math.Min(restoreWaitTimeoutMs, 250))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error waiting for startup restore after stop: {ex}");
            }
        }

        return response;
    }

    private async Task<IpcResponse> HandlePauseWallpaperAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
        }

        await WaitForPendingRestoreAsync().ConfigureAwait(false);
        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || !currentState.HasActiveWallpaper)
            {
                return IpcResponse.Fail(request.Id, "No active wallpaper to pause.");
            }

            var result = compositionRoot.WallpaperService.Pause();
            if (result.Success)
            {
                isManualPaused = true;
                isFullscreenPaused = false;
                isPowerPaused = false;
                currentState = currentState.AsPaused();
                return IpcResponse.Ok(request.Id, $"Wallpaper paused: {currentState.Path}");
            }

            if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                ClearPauseReasons();
                currentState = RuntimeWallpaperState.Empty;
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to pause wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleResumeWallpaperAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
        }

        await WaitForPendingRestoreAsync().ConfigureAwait(false);
        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || !currentState.HasActiveWallpaper)
            {
                return IpcResponse.Fail(request.Id, "No active wallpaper to resume.");
            }

            var result = compositionRoot.WallpaperService.Resume();
            if (result.Success)
            {
                ClearPauseReasons();
                currentState = currentState.AsRunning();
                NotifyPolicyStateChanged();
                return IpcResponse.Ok(request.Id, $"Wallpaper resumed: {currentState.Path}");
            }

            if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                ClearPauseReasons();
                currentState = RuntimeWallpaperState.Empty;
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to resume wallpaper.");
        }).ConfigureAwait(false);
    }

    private void ClearPauseReasons()
    {
        isManualPaused = false;
        isFullscreenPaused = false;
        isPowerPaused = false;
    }

    private void NotifyPolicyStateChanged()
    {
        var previous = Interlocked.Exchange(
            ref policyWakeSignal,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult(true);
    }

    private async Task<bool> WaitForPolicyEvaluationEligibleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !disposed && !shutdownEvent.IsSet)
        {
            if (currentState.HasActiveWallpaper && !isManualPaused)
            {
                return true;
            }

            var signal = policyWakeSignal.Task;
            if (currentState.HasActiveWallpaper && !isManualPaused)
            {
                return true;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task MonitorFullscreenPolicyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !disposed && !shutdownEvent.IsSet)
            {
                if (!await WaitForPolicyEvaluationEligibleAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(fullscreenPollIntervalMs, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || disposed || shutdownEvent.IsSet)
                {
                    break;
                }

                await EvaluateFullscreenPolicyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clean cancellation during host shutdown.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Unexpected error in fullscreen policy monitor loop: {ex}");
        }
    }

    private async Task MonitorPowerPolicyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !disposed && !shutdownEvent.IsSet)
            {
                if (!await WaitForPolicyEvaluationEligibleAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(powerPollIntervalMs, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || disposed || shutdownEvent.IsSet)
                {
                    break;
                }

                await EvaluatePowerPolicyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clean cancellation during host shutdown.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Unexpected error in power policy monitor loop: {ex}");
        }
    }

    public async Task EvaluateFullscreenPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || shutdownEvent.IsSet || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var snapshotState = currentState;
        if (!snapshotState.HasActiveWallpaper || isManualPaused)
        {
            return;
        }

        bool isFullscreen;
        try
        {
            isFullscreen = fullscreenDetector.IsFullscreenWindowActive();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error checking fullscreen state: {ex}");
            return;
        }

        if (isFullscreen == isFullscreenPaused &&
            ((ShouldBePaused && snapshotState.IsPaused) || (!ShouldBePaused && snapshotState.IsRunning)))
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (disposed || shutdownEvent.IsSet || !currentState.HasActiveWallpaper || isManualPaused)
            {
                return;
            }

            isFullscreenPaused = isFullscreen;
            SynchronizeAutoPauseStateOnDispatcher();
        }).ConfigureAwait(false);
    }

    public async Task EvaluatePowerPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || shutdownEvent.IsSet || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var snapshotState = currentState;
        if (!snapshotState.HasActiveWallpaper || isManualPaused)
        {
            return;
        }

        bool shouldPauseForPower;
        try
        {
            shouldPauseForPower = powerStatusProvider.GetPowerStatus().ShouldPauseWallpaper();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error checking power state: {ex}");
            return;
        }

        if (shouldPauseForPower == isPowerPaused &&
            ((ShouldBePaused && snapshotState.IsPaused) || (!ShouldBePaused && snapshotState.IsRunning)))
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (disposed || shutdownEvent.IsSet || !currentState.HasActiveWallpaper || isManualPaused)
            {
                return;
            }

            isPowerPaused = shouldPauseForPower;
            SynchronizeAutoPauseStateOnDispatcher();
        }).ConfigureAwait(false);
    }

    private void SynchronizeAutoPauseStateOnDispatcher()
    {
        if (ShouldBePaused && currentState.IsRunning)
        {
            var result = compositionRoot.WallpaperService.Pause();
            if (result.Success)
            {
                currentState = currentState.AsPaused();
                Trace.WriteLine($"Wallpaper auto-paused (fullscreen={isFullscreenPaused}, power={isPowerPaused}): {currentState.Path}");
            }
            else if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                ClearPauseReasons();
                currentState = RuntimeWallpaperState.Empty;
            }
        }
        else if (!ShouldBePaused && currentState.IsPaused)
        {
            var result = compositionRoot.WallpaperService.Resume();
            if (result.Success)
            {
                currentState = currentState.AsRunning();
                Trace.WriteLine($"Wallpaper auto-resumed: {currentState.Path}");
            }
            else if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                ClearPauseReasons();
                currentState = RuntimeWallpaperState.Empty;
            }
        }
    }

    private async Task RestoreLastWallpaperCoreAsync()
    {
        try
        {
            if (IsShuttingDown || Volatile.Read(ref commandSequence) != 0)
            {
                return;
            }

            var settings = await compositionRoot.SettingsService.LoadAsync().ConfigureAwait(false);
            if (!settings.RestoreLastWallpaper)
            {
                Trace.WriteLine("Startup wallpaper restore skipped: RestoreLastWallpaper is disabled.");
                return;
            }

            if (string.Equals(settings.LastWallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(settings.LastWallpaperPath))
            {
                Trace.WriteLine("Startup wallpaper restore skipped: no persisted wallpaper.");
                return;
            }

            string fullPath;
            string normalizedType;
            if (string.Equals(settings.LastWallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase))
            {
                var validation = ImageWallpaper.Validate(settings.LastWallpaperPath, out fullPath);
                if (!validation.Success)
                {
                    Trace.WriteLine($"Startup image wallpaper restore skipped: {validation.Message} (path='{settings.LastWallpaperPath}')");
                    return;
                }
                normalizedType = IpcConstants.WallpaperTypeImage;
            }
            else if (string.Equals(settings.LastWallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase))
            {
                var validation = VideoWallpaper.Validate(settings.LastWallpaperPath, out fullPath);
                if (!validation.Success)
                {
                    Trace.WriteLine($"Startup video wallpaper restore skipped: {validation.Message} (path='{settings.LastWallpaperPath}')");
                    return;
                }
                normalizedType = IpcConstants.WallpaperTypeVideo;
            }
            else
            {
                Trace.WriteLine($"Startup wallpaper restore skipped: unsupported wallpaper type '{settings.LastWallpaperType}'.");
                return;
            }

            if (IsShuttingDown || Volatile.Read(ref commandSequence) != 0)
            {
                return;
            }

            await dispatcher.InvokeAsync(async () =>
            {
                if (IsShuttingDown || Volatile.Read(ref commandSequence) != 0)
                {
                    return;
                }

                var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
                if (result.Success && Volatile.Read(ref commandSequence) == 0 && !IsShuttingDown)
                {
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(normalizedType, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    Trace.WriteLine($"Startup wallpaper restored: type={normalizedType}, path={fullPath}");
                }
                else if (result.Success)
                {
                    compositionRoot.WallpaperService.Stop();
                    Trace.WriteLine("Startup wallpaper restore completed after user command or shutdown; stopped restored wallpaper.");
                }
                else
                {
                    Trace.WriteLine($"Startup wallpaper restore failed: {result.Error} - {result.Message}");
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Unexpected error during startup wallpaper restore: {ex}");
        }
    }

    private async Task PersistWallpaperStateAsync(string wallpaperType, string? fullPath)
    {
        if (IsShuttingDown)
        {
            return;
        }

        try
        {
            var currentSettings = await compositionRoot.SettingsService.LoadAsync().ConfigureAwait(false);
            var updatedSettings = new AppSettings
            {
                RestoreLastWallpaper = currentSettings.RestoreLastWallpaper,
                LastWallpaperType = wallpaperType,
                LastWallpaperPath = string.IsNullOrWhiteSpace(fullPath) ? null : Path.GetFullPath(fullPath),
                Theme = currentSettings.Theme
            };

            var saved = await compositionRoot.SettingsService.SaveAsync(updatedSettings).ConfigureAwait(false);
            if (!saved)
            {
                Trace.WriteLine($"Failed to persist active wallpaper state (type={wallpaperType}, path={fullPath}).");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error persisting active wallpaper state: {ex}");
        }
    }

    private void CancelPolicyMonitor()
    {
        try
        {
            policyCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private void TrySetShutdownEvent()
    {
        try
        {
            shutdownEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private void StopWallpaperOnceForShutdown()
    {
        if (Interlocked.Exchange(ref wallpaperStoppedOnShutdown, 1) != 0)
        {
            return;
        }

        if (compositionRoot.IsDisposed)
        {
            return;
        }

        try
        {
            if (dispatcher.HasThreadAccess)
            {
                if (!compositionRoot.IsDisposed)
                {
                    compositionRoot.WallpaperService.Stop();
                }
            }
            else
            {
                try
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        if (!compositionRoot.IsDisposed)
                        {
                            compositionRoot.WallpaperService.Stop();
                        }
                    }).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Error stopping wallpaper on dispatcher during shutdown: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error stopping wallpaper: {ex}");
        }
    }

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref commandSequence);
        ClearPauseReasons();
        currentState = RuntimeWallpaperState.Empty;
        CancelPolicyMonitor();
        StopWallpaperOnceForShutdown();
        TrySetShutdownEvent();
        onShutdownAction?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Exchange(ref shutdownRequested, 1);
        Interlocked.Increment(ref commandSequence);
        ClearPauseReasons();
        currentState = RuntimeWallpaperState.Empty;
        CancelPolicyMonitor();
        StopWallpaperOnceForShutdown();
        TrySetShutdownEvent();
        try
        {
            Task.WaitAll([monitorTask, powerMonitorTask], TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error waiting for policy monitor shutdown: {ex}");
        }
        policyCts?.Dispose();
        sampler.Dispose();
        ipcServer.Dispose();
        compositionRoot.Dispose();
        singleInstanceGuard.Dispose();
        shutdownEvent.Dispose();
    }
}
