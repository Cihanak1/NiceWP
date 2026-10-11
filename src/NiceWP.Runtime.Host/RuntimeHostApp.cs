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
    private readonly RuntimeIpcServer ipcServer;
    private readonly ManualResetEventSlim shutdownEvent = new(false);
    private readonly int restoreWaitTimeoutMs;
    private readonly int fullscreenPollIntervalMs;
    private CancellationTokenSource? policyCts;
    private RuntimeWallpaperState currentState = RuntimeWallpaperState.Empty;
    private Task restoreTask = Task.CompletedTask;
    private Task monitorTask = Task.CompletedTask;
    private volatile bool isAutoPaused;
    private int commandSequence;
    private bool started;
    private bool disposed;

    public bool IsSingleInstance => singleInstanceGuard.IsSingleInstance;
    public RuntimeCompositionRoot Composition => compositionRoot;
    public SettingsService SettingsService => compositionRoot.SettingsService;
    public IHostDispatcher Dispatcher => dispatcher;
    public IFullscreenWindowDetector FullscreenDetector => fullscreenDetector;
    public RuntimeIpcServer IpcServer => ipcServer;
    public RuntimeWallpaperState CurrentState => currentState;
    public bool IsAutoPaused => isAutoPaused;
    public Task RestoreTask => restoreTask;
    public Task MonitorTask => monitorTask;

    public RuntimeHostApp(
        SingleInstanceGuard? guard = null,
        RuntimeCompositionRoot? composition = null,
        RuntimeIpcServer? server = null,
        IHostDispatcher? dispatcher = null,
        string? pipeName = null,
        int restoreWaitTimeoutMs = IpcConstants.DefaultRestoreWaitTimeoutMs,
        IFullscreenWindowDetector? fullscreenDetector = null,
        int fullscreenPollIntervalMs = IpcConstants.DefaultFullscreenPollIntervalMs)
    {
        singleInstanceGuard = guard ?? (pipeName != null
            ? new SingleInstanceGuard($@"Local\NiceWP.Runtime.Host.{pipeName.Replace('\\', '.')}")
            : new SingleInstanceGuard());
        compositionRoot = composition ?? new RuntimeCompositionRoot();
        this.dispatcher = dispatcher ?? new CurrentThreadHostDispatcher();
        this.fullscreenDetector = fullscreenDetector ?? compositionRoot.FullscreenDetector;
        this.restoreWaitTimeoutMs = restoreWaitTimeoutMs > 0
            ? restoreWaitTimeoutMs
            : IpcConstants.DefaultRestoreWaitTimeoutMs;
        this.fullscreenPollIntervalMs = fullscreenPollIntervalMs > 0
            ? fullscreenPollIntervalMs
            : IpcConstants.DefaultFullscreenPollIntervalMs;
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

        if (started)
        {
            return;
        }

        started = true;
        policyCts = new CancellationTokenSource();
        ipcServer.Start();
        restoreTask = RestoreLastWallpaperCoreAsync();
        monitorTask = MonitorFullscreenPolicyLoopAsync(policyCts.Token);
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

            try
            {
                dispatcher.InvokeAsync(() => compositionRoot.WallpaperService.Stop()).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Error stopping wallpaper during shutdown: {exception}");
            }

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

    private async Task<IpcResponse> HandleGetStatusAsync(IpcRequest request)
    {
        await WaitForPendingRestoreAsync().ConfigureAwait(false);

        var state = currentState;
        return IpcResponse.Status(
            requestId: request.Id,
            isRunning: true,
            hasActiveWallpaper: state.HasActiveWallpaper,
            wallpaperType: state.Type,
            activePath: state.Path,
            playbackState: state.PlaybackState,
            isPaused: state.IsPaused);
    }

    private async Task<IpcResponse> HandleSetImageAsync(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return IpcResponse.Fail(request.Id, "Image path cannot be empty.");
        }

        var validation = ImageWallpaper.Validate(request.Path, out var fullPath);
        if (!validation.Success)
        {
            return IpcResponse.Fail(request.Id, validation.Message ?? "Invalid image path.");
        }

        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(async () =>
        {
            var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
            if (result.Success)
            {
                isAutoPaused = false;
                currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeImage, fullPath, IpcConstants.PlaybackStateRunning);
                await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeImage, fullPath).ConfigureAwait(false);
                return IpcResponse.Ok(request.Id, $"Image wallpaper set: {fullPath}");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to set image wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleSetVideoAsync(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return IpcResponse.Fail(request.Id, "Video path cannot be empty.");
        }

        var validation = VideoWallpaper.Validate(request.Path, out var fullPath);
        if (!validation.Success)
        {
            return IpcResponse.Fail(request.Id, validation.Message ?? "Invalid video path.");
        }

        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(async () =>
        {
            var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
            if (result.Success)
            {
                isAutoPaused = false;
                currentState = new RuntimeWallpaperState(IpcConstants.WallpaperTypeVideo, fullPath, IpcConstants.PlaybackStateRunning);
                await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeVideo, fullPath).ConfigureAwait(false);
                return IpcResponse.Ok(request.Id, $"Video wallpaper set: {fullPath}");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to set video wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleStopWallpaperAsync(IpcRequest request)
    {
        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(async () =>
        {
            var result = compositionRoot.WallpaperService.Stop();
            if (result.Success)
            {
                isAutoPaused = false;
                currentState = RuntimeWallpaperState.Empty;
                await PersistWallpaperStateAsync(IpcConstants.WallpaperTypeNone, null).ConfigureAwait(false);
                return IpcResponse.Ok(request.Id, "Wallpaper stopped.");
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to stop wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandlePauseWallpaperAsync(IpcRequest request)
    {
        await WaitForPendingRestoreAsync().ConfigureAwait(false);
        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(() =>
        {
            if (!currentState.HasActiveWallpaper)
            {
                return IpcResponse.Fail(request.Id, "No active wallpaper to pause.");
            }

            var result = compositionRoot.WallpaperService.Pause();
            if (result.Success)
            {
                isAutoPaused = false;
                currentState = currentState.AsPaused();
                return IpcResponse.Ok(request.Id, $"Wallpaper paused: {currentState.Path}");
            }

            if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                isAutoPaused = false;
                currentState = RuntimeWallpaperState.Empty;
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to pause wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task<IpcResponse> HandleResumeWallpaperAsync(IpcRequest request)
    {
        await WaitForPendingRestoreAsync().ConfigureAwait(false);
        Interlocked.Increment(ref commandSequence);

        return await dispatcher.InvokeAsync(() =>
        {
            if (!currentState.HasActiveWallpaper)
            {
                return IpcResponse.Fail(request.Id, "No active wallpaper to resume.");
            }

            var result = compositionRoot.WallpaperService.Resume();
            if (result.Success)
            {
                isAutoPaused = false;
                currentState = currentState.AsRunning();
                return IpcResponse.Ok(request.Id, $"Wallpaper resumed: {currentState.Path}");
            }

            if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
            {
                isAutoPaused = false;
                currentState = RuntimeWallpaperState.Empty;
            }

            return IpcResponse.Fail(request.Id, result.Message ?? "Failed to resume wallpaper.");
        }).ConfigureAwait(false);
    }

    private async Task MonitorFullscreenPolicyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !disposed && !shutdownEvent.IsSet)
            {
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

    public async Task EvaluateFullscreenPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || shutdownEvent.IsSet || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var snapshotState = currentState;
        if (!snapshotState.HasActiveWallpaper)
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

        if (isFullscreen && snapshotState.IsRunning)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed || shutdownEvent.IsSet || !currentState.HasActiveWallpaper || !currentState.IsRunning)
                {
                    return;
                }

                var result = compositionRoot.WallpaperService.Pause();
                if (result.Success)
                {
                    currentState = currentState.AsPaused();
                    isAutoPaused = true;
                    Trace.WriteLine($"Wallpaper auto-paused for fullscreen application: {currentState.Path}");
                }
                else if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
                {
                    currentState = RuntimeWallpaperState.Empty;
                    isAutoPaused = false;
                }
            }).ConfigureAwait(false);
        }
        else if (!isFullscreen && snapshotState.IsPaused && isAutoPaused)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed || shutdownEvent.IsSet || !currentState.HasActiveWallpaper || !currentState.IsPaused || !isAutoPaused)
                {
                    return;
                }

                var result = compositionRoot.WallpaperService.Resume();
                if (result.Success)
                {
                    currentState = currentState.AsRunning();
                    isAutoPaused = false;
                    Trace.WriteLine($"Wallpaper auto-resumed after fullscreen application closed: {currentState.Path}");
                }
                else if (string.IsNullOrWhiteSpace(compositionRoot.WallpaperService.ActivePath))
                {
                    currentState = RuntimeWallpaperState.Empty;
                    isAutoPaused = false;
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task RestoreLastWallpaperCoreAsync()
    {
        try
        {
            if (disposed || shutdownEvent.IsSet || Volatile.Read(ref commandSequence) != 0)
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

            if (disposed || shutdownEvent.IsSet || Volatile.Read(ref commandSequence) != 0)
            {
                return;
            }

            await dispatcher.InvokeAsync(async () =>
            {
                if (disposed || shutdownEvent.IsSet || Volatile.Read(ref commandSequence) != 0)
                {
                    return;
                }

                var result = await compositionRoot.WallpaperService.StartAsync(fullPath).ConfigureAwait(false);
                if (result.Success && Volatile.Read(ref commandSequence) == 0 && !shutdownEvent.IsSet && !disposed)
                {
                    isAutoPaused = false;
                    currentState = new RuntimeWallpaperState(normalizedType, fullPath, IpcConstants.PlaybackStateRunning);
                    Trace.WriteLine($"Startup wallpaper restored: type={normalizedType}, path={fullPath}");
                }
                else if (!result.Success)
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
        try
        {
            var currentSettings = await compositionRoot.SettingsService.LoadAsync().ConfigureAwait(false);
            var updatedSettings = new AppSettings
            {
                RestoreLastWallpaper = currentSettings.RestoreLastWallpaper,
                LastWallpaperType = wallpaperType,
                LastWallpaperPath = string.IsNullOrWhiteSpace(fullPath) ? null : Path.GetFullPath(fullPath)
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

    public void RequestShutdown()
    {
        Interlocked.Increment(ref commandSequence);
        isAutoPaused = false;
        currentState = RuntimeWallpaperState.Empty;
        shutdownEvent.Set();
        CancelPolicyMonitor();

        if (Application.Current != null)
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                try
                {
                    compositionRoot.WallpaperService.Stop();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Error stopping wallpaper: {ex}");
                }
                Application.Current?.Exit();
            });
        }
        else
        {
            try
            {
                dispatcher.InvokeAsync(() => compositionRoot.WallpaperService.Stop()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error stopping wallpaper: {ex}");
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Increment(ref commandSequence);
        isAutoPaused = false;
        shutdownEvent.Set();
        CancelPolicyMonitor();
        try
        {
            monitorTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error waiting for fullscreen policy monitor shutdown: {ex}");
        }
        policyCts?.Dispose();
        ipcServer.Dispose();
        compositionRoot.Dispose();
        singleInstanceGuard.Dispose();
        shutdownEvent.Dispose();
    }
}
