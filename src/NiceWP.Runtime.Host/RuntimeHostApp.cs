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
    private readonly IRuntimeEventNotifier eventNotifier;
    private readonly RuntimeIpcServer ipcServer;
    private readonly RuntimePerformanceSampler sampler;
    private readonly ManualResetEventSlim shutdownEvent = new(false);
    private readonly Action? onShutdownAction;
    private readonly int restoreWaitTimeoutMs;
    private readonly int fullscreenPollIntervalMs;
    private readonly int fullscreenFallbackIntervalMs;
    private readonly int powerPollIntervalMs;
    private readonly int powerFallbackIntervalMs;
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
    private volatile bool isSessionLockedPaused;
    private volatile bool isDisplayOffPaused;
    private readonly RuntimeHealthTracker healthTracker = new();
    private readonly RuntimeAuditTrail auditTrail = new();
    private volatile EfficiencyProfile currentEfficiencyProfile = EfficiencyProfile.Balanced;
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
    public IRuntimeEventNotifier EventNotifier => eventNotifier;
    public RuntimeIpcServer IpcServer => ipcServer;
    public RuntimeWallpaperState CurrentState => currentState;
    public RuntimePerformanceSampler Sampler => sampler;
    public bool IsManualPaused => isManualPaused;
    public bool IsFullscreenPaused => isFullscreenPaused;
    public bool IsPowerPaused => isPowerPaused;
    public bool IsSessionLockedPaused => isSessionLockedPaused;
    public bool IsDisplayOffPaused => isDisplayOffPaused;
    public bool IsAutoPaused => !isManualPaused && (isFullscreenPaused || isPowerPaused || isSessionLockedPaused || isDisplayOffPaused);
    public bool ShouldBePaused => isManualPaused || isFullscreenPaused || isPowerPaused || isSessionLockedPaused || isDisplayOffPaused;
    public Task RestoreTask => restoreTask;
    public Task MonitorTask => monitorTask;
    public Task PowerMonitorTask => powerMonitorTask;
    public RuntimeHealthTracker HealthTracker => healthTracker;
    public RuntimeAuditTrail AuditTrail => auditTrail;
    public EfficiencyProfile CurrentEfficiencyProfile => currentEfficiencyProfile;

    public RuntimeHostApp(
        SingleInstanceGuard? guard = null,
        RuntimeCompositionRoot? composition = null,
        RuntimeIpcServer? server = null,
        IHostDispatcher? dispatcher = null,
        string? pipeName = null,
        int restoreWaitTimeoutMs = IpcConstants.DefaultRestoreWaitTimeoutMs,
        IFullscreenWindowDetector? fullscreenDetector = null,
        int fullscreenPollIntervalMs = IpcConstants.DefaultFullscreenPollIntervalMs,
        int fullscreenFallbackIntervalMs = IpcConstants.DefaultFullscreenFallbackIntervalMs,
        IPowerStatusProvider? powerStatusProvider = null,
        int powerPollIntervalMs = IpcConstants.DefaultPowerPollIntervalMs,
        int powerFallbackIntervalMs = IpcConstants.DefaultPowerFallbackIntervalMs,
        Action? onShutdown = null,
        RuntimePerformanceSampler? sampler = null,
        IRuntimeEventNotifier? eventNotifier = null)
    {
        singleInstanceGuard = guard ?? (pipeName != null
            ? new SingleInstanceGuard($@"Local\NiceWP.Runtime.Host.{pipeName.Replace('\\', '.')}")
            : new SingleInstanceGuard());
        compositionRoot = composition ?? new RuntimeCompositionRoot();
        this.dispatcher = dispatcher ?? new CurrentThreadHostDispatcher();
        this.fullscreenDetector = fullscreenDetector ?? compositionRoot.FullscreenDetector;
        this.powerStatusProvider = powerStatusProvider ?? compositionRoot.PowerStatusProvider;
        this.eventNotifier = eventNotifier ?? compositionRoot.EventNotifier;
        this.onShutdownAction = onShutdown;
        this.restoreWaitTimeoutMs = restoreWaitTimeoutMs > 0
            ? restoreWaitTimeoutMs
            : IpcConstants.DefaultRestoreWaitTimeoutMs;
        this.fullscreenPollIntervalMs = fullscreenPollIntervalMs > 0
            ? fullscreenPollIntervalMs
            : IpcConstants.DefaultFullscreenPollIntervalMs;
        this.fullscreenFallbackIntervalMs = fullscreenFallbackIntervalMs > 0
            ? fullscreenFallbackIntervalMs
            : IpcConstants.DefaultFullscreenFallbackIntervalMs;
        this.powerPollIntervalMs = powerPollIntervalMs > 0
            ? powerPollIntervalMs
            : IpcConstants.DefaultPowerPollIntervalMs;
        this.powerFallbackIntervalMs = powerFallbackIntervalMs > 0
            ? powerFallbackIntervalMs
            : IpcConstants.DefaultPowerFallbackIntervalMs;
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
        eventNotifier.ForegroundWindowChanged += OnForegroundWindowChanged;
        eventNotifier.PowerStatusChanged += OnPowerStatusChanged;
        eventNotifier.SessionLockChanged += OnSessionLockChanged;
        eventNotifier.DisplayPowerChanged += OnDisplayPowerChanged;
        ipcServer.Start();
        sampler.Start();
        restoreTask = RestoreLastWallpaperCoreAsync();
        monitorTask = MonitorFullscreenPolicyLoopAsync(policyCts.Token);
        powerMonitorTask = MonitorPowerPolicyLoopAsync(policyCts.Token);
        Trace.WriteLine($"NiceWP.Runtime.Host started, pid={Environment.ProcessId}");
    }

    private void OnForegroundWindowChanged(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        _ = EvaluateFullscreenPolicyAsync();
    }

    private void OnPowerStatusChanged(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        _ = EvaluatePowerPolicyAsync();
    }

    private void OnSessionLockChanged(bool isLocked)
    {
        if (IsShuttingDown) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || !currentState.HasActiveWallpaper) return;
            var shouldPause = EfficiencyPolicyMatrix.ShouldPauseForSessionLock(currentEfficiencyProfile, isLocked);
            isSessionLockedPaused = shouldPause;
            if (isLocked)
            {
                auditTrail.Record("SessionLocked", "Workstation locked; auto-pause evaluated.");
            }
            else
            {
                auditTrail.Record("SessionUnlocked", "Workstation unlocked.");
            }
            SynchronizeAutoPauseStateOnDispatcher();
        });
    }

    private void OnDisplayPowerChanged(bool isDisplayOn)
    {
        if (IsShuttingDown) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || !currentState.HasActiveWallpaper) return;
            var shouldPause = EfficiencyPolicyMatrix.ShouldPauseForDisplayOff(currentEfficiencyProfile, !isDisplayOn);
            isDisplayOffPaused = shouldPause;
            if (!isDisplayOn)
            {
                auditTrail.Record("DisplayOff", "Display power suspended; auto-pause evaluated.");
            }
            else
            {
                auditTrail.Record("DisplayOn", "Display power restored.");
            }
            SynchronizeAutoPauseStateOnDispatcher();
        });
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

        if (string.Equals(request.Command, IpcConstants.SetEfficiencyProfileCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSetEfficiencyProfileAsync(request).ConfigureAwait(false);
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
        string pauseReasons = "None";
        if (state.IsPaused)
        {
            var reasons = new List<string>();
            if (isManualPaused) reasons.Add("Manual");
            if (isFullscreenPaused) reasons.Add("Fullscreen");
            if (isPowerPaused) reasons.Add("Power");
            if (isSessionLockedPaused) reasons.Add("SessionLocked");
            if (isDisplayOffPaused) reasons.Add("DisplayOff");
            pauseReasons = reasons.Count > 0 ? string.Join(", ", reasons) : "Unknown";
        }

        return IpcResponse.Status(
            requestId: request.Id,
            isRunning: !IsShuttingDown,
            hasActiveWallpaper: state.HasActiveWallpaper,
            wallpaperType: state.Type,
            activePath: state.Path,
            playbackState: state.PlaybackState,
            isPaused: state.IsPaused,
            pauseReasons: pauseReasons);
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

    private async Task<IpcResponse> HandleSetEfficiencyProfileAsync(IpcRequest request)
    {
        if (IsShuttingDown)
        {
            return IpcResponse.Fail(request.Id, "Runtime host is shutting down.");
        }

        var profile = EfficiencyPolicyMatrix.ParseProfile(request.Path);
        currentEfficiencyProfile = profile;
        auditTrail.Record("EfficiencyProfileChanged", $"Switched to {profile.ToDisplayString()} profile.");

        try
        {
            var settings = await compositionRoot.SettingsService.LoadAsync().ConfigureAwait(false);
            var updated = new AppSettings
            {
                RestoreLastWallpaper = settings.RestoreLastWallpaper,
                LastWallpaperType = settings.LastWallpaperType,
                LastWallpaperPath = settings.LastWallpaperPath,
                Theme = settings.Theme,
                EfficiencyProfile = profile.ToDisplayString()
            };
            await compositionRoot.SettingsService.SaveAsync(updated).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error persisting efficiency profile: {ex.Message}");
        }

        _ = EvaluateFullscreenPolicyAsync();
        _ = EvaluatePowerPolicyAsync();
        NotifyPolicyStateChanged();

        return IpcResponse.Ok(request.Id, $"Efficiency profile set to {profile.ToDisplayString()}.");
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
            if (isSessionLockedPaused) reasons.Add("SessionLocked");
            if (isDisplayOffPaused) reasons.Add("DisplayOff");
            pauseReasons = reasons.Count > 0 ? string.Join(", ", reasons) : "Unknown";
        }

        var (healthStatus, _) = healthTracker.EvaluateHealth(state.HasActiveWallpaper, isConnected, isAlive);
        var currentAction = healthTracker.ComputeCurrentAction(
            healthTracker.IsCircuitBreakerTripped,
            state.HasActiveWallpaper,
            state.IsPaused,
            isManualPaused,
            isFullscreenPaused,
            isPowerPaused,
            isSessionLockedPaused,
            isDisplayOffPaused,
            state.Type);
        var recentEvents = auditTrail.GetRecentEvents(10);

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
            ManualPauseActive: isManualPaused,
            SessionLockedPauseActive: isSessionLockedPaused,
            DisplayOffPauseActive: isDisplayOffPaused,
            EfficiencyProfile: currentEfficiencyProfile.ToDisplayString(),
            RuntimeHealth: healthStatus.ToDisplayString(),
            CurrentAction: currentAction,
            RecentEvents: recentEvents);
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

        healthTracker.ResetCircuitBreaker();
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
                healthTracker.RecordSuccess();
                auditTrail.Record("WallpaperStarted", $"Image wallpaper set: {Path.GetFileName(fullPath)}");
                if (Volatile.Read(ref commandSequence) == seq && !IsShuttingDown)
                {
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeImage, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeImage, fullPath).ConfigureAwait(false);
                }
                return IpcResponse.Ok(request.Id, $"Image wallpaper set: {fullPath}");
            }

            var reason = result.Message ?? "Failed to set image wallpaper.";
            var tripped = healthTracker.RecordFailure(reason);
            auditTrail.Record("WallpaperFailed", $"Failed to set image wallpaper: {reason}");
            if (tripped)
            {
                auditTrail.Record("CircuitBreakerTripped", "Circuit breaker tripped after 3 consecutive failures. State moved to SafeStopped.");
            }

            return IpcResponse.Fail(request.Id, reason);
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

        healthTracker.ResetCircuitBreaker();
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
                healthTracker.RecordSuccess();
                auditTrail.Record("WallpaperStarted", $"Video wallpaper set: {Path.GetFileName(fullPath)}");
                if (Volatile.Read(ref commandSequence) == seq && !IsShuttingDown)
                {
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeVideo, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeVideo, fullPath).ConfigureAwait(false);
                }
                return IpcResponse.Ok(request.Id, $"Video wallpaper set: {fullPath}");
            }

            var reason = result.Message ?? "Failed to set video wallpaper.";
            var tripped = healthTracker.RecordFailure(reason);
            auditTrail.Record("WallpaperFailed", $"Failed to set video wallpaper: {reason}");
            if (tripped)
            {
                auditTrail.Record("CircuitBreakerTripped", "Circuit breaker tripped after 3 consecutive failures. State moved to SafeStopped.");
            }

            return IpcResponse.Fail(request.Id, reason);
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
                auditTrail.Record("WallpaperStopped", "Wallpaper stopped by request.");
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
                auditTrail.Record("WallpaperPaused", $"Wallpaper paused manually: {Path.GetFileName(currentState.Path)}");
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

        if (healthTracker.IsCircuitBreakerTripped)
        {
            return IpcResponse.Fail(request.Id, "Cannot resume wallpaper while circuit breaker is tripped (SafeStopped). Please apply a wallpaper to reset.");
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
                auditTrail.Record("WallpaperResumed", $"Wallpaper resumed manually: {Path.GetFileName(currentState.Path)}");
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
        isSessionLockedPaused = false;
        isDisplayOffPaused = false;
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

                var delayMs = eventNotifier.IsForegroundHookActive
                    ? fullscreenFallbackIntervalMs
                    : fullscreenPollIntervalMs;

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
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

                var delayMs = eventNotifier.IsPowerNotificationActive
                    ? powerFallbackIntervalMs
                    : powerPollIntervalMs;

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
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

        var shouldPauseForFullscreen = EfficiencyPolicyMatrix.ShouldPauseForFullscreen(currentEfficiencyProfile, isFullscreen);

        if (shouldPauseForFullscreen == isFullscreenPaused &&
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

            var previous = isFullscreenPaused;
            isFullscreenPaused = shouldPauseForFullscreen;
            if (previous != shouldPauseForFullscreen)
            {
                if (shouldPauseForFullscreen)
                {
                    auditTrail.Record("FullscreenEntered", "Fullscreen window active; auto-pause engaged.");
                }
                else
                {
                    auditTrail.Record("FullscreenExited", "Fullscreen window inactive; auto-pause released.");
                }
            }
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
            var powerStatus = powerStatusProvider.GetPowerStatus();
            shouldPauseForPower = EfficiencyPolicyMatrix.ShouldPauseForPower(currentEfficiencyProfile, in powerStatus);
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

            var previous = isPowerPaused;
            isPowerPaused = shouldPauseForPower;
            if (previous != shouldPauseForPower)
            {
                if (shouldPauseForPower)
                {
                    auditTrail.Record("BatterySaverEngaged", "Power policy active; auto-pause engaged.");
                }
                else
                {
                    auditTrail.Record("BatterySaverReleased", "Normal power restored; auto-pause released.");
                }
            }
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
                Trace.WriteLine($"Wallpaper auto-paused (fullscreen={isFullscreenPaused}, power={isPowerPaused}, sessionLocked={isSessionLockedPaused}, displayOff={isDisplayOffPaused}): {currentState.Path}");
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
            currentEfficiencyProfile = EfficiencyPolicyMatrix.ParseProfile(settings.EfficiencyProfile);
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

            const int maxRestoreAttempts = 3;
            var result = new WallpaperResult(WallpaperError.RendererFailed, "Startup restore not started.");
            for (var attempt = 1; attempt <= maxRestoreAttempts; attempt++)
            {
                if (IsShuttingDown || Volatile.Read(ref commandSequence) != 0)
                {
                    return;
                }

                result = await dispatcher.InvokeAsync(async () =>
                {
                    if (IsShuttingDown || Volatile.Read(ref commandSequence) != 0)
                    {
                        return new WallpaperResult(WallpaperError.Stopped, "Aborted");
                    }
                    return await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
                }).ConfigureAwait(false);

                if (result.Success || result.Error != WallpaperError.DesktopHostUnavailable || attempt == maxRestoreAttempts)
                {
                    break;
                }

                Trace.WriteLine($"Startup wallpaper restore attempt {attempt} failed (desktop host not ready); retrying with backoff...");
                await Task.Delay(attempt * 50).ConfigureAwait(false);
            }

            await dispatcher.InvokeAsync(() =>
            {
                if (result.Success && Volatile.Read(ref commandSequence) == 0 && !IsShuttingDown)
                {
                    healthTracker.RecordSuccess();
                    ClearPauseReasons();
                    currentState = new RuntimeWallpaperState(normalizedType, fullPath, IpcConstants.PlaybackStateRunning);
                    NotifyPolicyStateChanged();
                    auditTrail.Record("WallpaperRestored", $"Startup wallpaper restored: {Path.GetFileName(fullPath)}");
                    Trace.WriteLine($"Startup wallpaper restored: type={normalizedType}, path={fullPath}");
                }
                else if (result.Success)
                {
                    compositionRoot.WallpaperService.Stop();
                    Trace.WriteLine("Startup wallpaper restore completed after user command or shutdown; stopped restored wallpaper.");
                }
                else
                {
                    var reason = result.Message ?? "Startup restore failed";
                    var tripped = healthTracker.RecordFailure(reason);
                    auditTrail.Record("StartupRestoreFailed", $"Failed to restore startup wallpaper: {reason}");
                    if (tripped)
                    {
                        auditTrail.Record("CircuitBreakerTripped", "Circuit breaker tripped after 3 consecutive failures. State moved to SafeStopped.");
                    }
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
                Theme = currentSettings.Theme,
                EfficiencyProfile = currentEfficiencyProfile.ToDisplayString()
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
        eventNotifier.ForegroundWindowChanged -= OnForegroundWindowChanged;
        eventNotifier.PowerStatusChanged -= OnPowerStatusChanged;
        eventNotifier.SessionLockChanged -= OnSessionLockChanged;
        eventNotifier.DisplayPowerChanged -= OnDisplayPowerChanged;
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
