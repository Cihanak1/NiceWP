using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NiceWP.Core;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace NiceWP.Rendering;

public sealed class VideoWallpaperRenderer : IWallpaperRenderer
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<WallpaperResult> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Window? window;
    private nint windowHandle;
    private AppWindow? appWindow;
    private MediaPlayerElement? element;
    private MediaPlayer? player;
    private MediaSource? source;
    private volatile WallpaperResult? playbackFailure;
    private bool loadStarted;
    private bool stopped;
    private bool disposed;

    public RendererLifecycleState LifecycleState { get; private set; } = RendererLifecycleState.Alive;

    public uint? SourceWidth => VideoWidth;
    public uint? SourceHeight => VideoHeight;
    public uint? VideoWidth { get; private set; }
    public uint? VideoHeight { get; private set; }
    public uint? DecodeWidth { get; private set; }
    public uint? DecodeHeight { get; private set; }

    public TimeSpan? PlaybackPosition
    {
        get
        {
            try { return player?.PlaybackSession?.Position; }
            catch { return null; }
        }
    }

    public TimeSpan? PlaybackDuration
    {
        get
        {
            try { return player?.PlaybackSession?.NaturalDuration; }
            catch { return null; }
        }
    }

    public string? PlaybackStatus
    {
        get
        {
            try { return player?.PlaybackSession?.PlaybackState.ToString(); }
            catch { return null; }
        }
    }

    public bool IsWindowAlive =>
        !disposed &&
        LifecycleState == RendererLifecycleState.Alive &&
        windowHandle != 0 &&
        RendererNativeMethods.IsWindow(windowHandle);

    public nint WindowHandle
    {
        get
        {
            if (disposed || LifecycleState == RendererLifecycleState.Disposed)
            {
                return 0;
            }

            if (window != null && windowHandle != 0)
            {
                if (RendererNativeMethods.IsWindow(windowHandle))
                {
                    return windowHandle;
                }

                LifecycleState = RendererLifecycleState.NativeWindowLost;
            }

            return 0;
        }
    }

    public async Task<WallpaperResult> LoadAsync(string path)
    {
        if (stopped || loadStarted)
            return new(WallpaperError.RendererFailed, "Renderer cannot be loaded again.");
        loadStarted = true;
        var validation = VideoWallpaper.Validate(path, out var fullPath);
        if (!validation.Success) return validation;
        var cancellation = lifetime.Token;
        lifetime.CancelAfter(OpenTimeout);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(cancellation);
            MediaEncodingProfile profile;
            try
            {
                profile = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(cancellation);
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Video metadata could not be read: {exception}");
                return new(WallpaperError.RendererFailed, "The video metadata could not be read or decoded.");
            }
            cancellation.ThrowIfCancellationRequested();
            Trace.WriteLine($"Video profile: video={profile.Video?.Subtype}, container={profile.Container?.Subtype}");
            var profileResult = ValidateProfile(profile);
            if (!profileResult.Success) return profileResult;

            if (profile.Video != null)
            {
                VideoWidth = profile.Video.Width;
                VideoHeight = profile.Video.Height;
            }

            player = new MediaPlayer { AutoPlay = false, IsMuted = true, IsLoopingEnabled = true };
            player.CommandManager.IsEnabled = false;
            try
            {
                player.SystemMediaTransportControls.IsEnabled = false;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Disabling SystemMediaTransportControls failed: {exception}");
            }
            player.MediaOpened += OnMediaOpened;
            player.MediaFailed += OnMediaFailed;
            element = new MediaPlayerElement
            {
                AutoPlay = false,
                AreTransportControlsEnabled = false,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            element.SetMediaPlayer(player);
            window = new Window { Title = "NiceWP Video Wallpaper", Content = element };
            windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            window.Closed += OnWindowClosed;
            // As with the image renderer, retain AppWindow before WorkerW reparenting.
            appWindow = window.AppWindow;
            try
            {
                appWindow.IsShownInSwitchers = false;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Disabling IsShownInSwitchers failed: {ex.Message}");
            }
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            var primaryBounds = DisplayArea.Primary.OuterBounds;
            DecodeWidth = (uint)primaryBounds.Width;
            DecodeHeight = (uint)primaryBounds.Height;
            appWindow.MoveAndResize(primaryBounds);
            source = MediaSource.CreateFromStorageFile(file);
            player.Source = source;
            var result = await opened.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                Dispose();
                return result;
            }
            if (WindowHandle == 0)
            {
                Dispose();
                return new(WallpaperError.RendererFailed, "The video wallpaper window is no longer valid.");
            }
            Trace.WriteLine($"Video opened: {fullPath}, H.264, window=0x{WindowHandle:X}");
            return playbackFailure ?? new WallpaperResult();
        }
        catch (OperationCanceledException)
        {
            var cancelResult = stopped
                ? new WallpaperResult(WallpaperError.Stopped, "Video opening was stopped.")
                : new WallpaperResult(WallpaperError.RendererFailed, "Video opening timed out.");
            Dispose();
            return cancelResult;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            Dispose();
            return new(WallpaperError.RendererFailed, "The video could not be opened.");
        }
        finally
        {
            if (!stopped && !disposed)
            {
                try
                {
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Renderer was disposed concurrently during cancellation.
                }
            }
        }
    }

    // Media callbacks only complete thread-safe state; HWND/XAML work stays on the owner thread.
    private void OnMediaOpened(MediaPlayer sender, object args) => opened.TrySetResult(new());

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var failure = new WallpaperResult(WallpaperError.RendererFailed, $"Video playback failed: {args.Error}.");
        playbackFailure = failure;
        Trace.WriteLine($"{failure.Message} {args.ExtendedErrorCode} {args.ErrorMessage}");
        opened.TrySetResult(failure);
    }

    public static WallpaperResult ValidateProfile(MediaEncodingProfile? profile)
    {
        if (profile == null)
            return new(WallpaperError.RendererFailed, "The video metadata could not be read.");

        var container = profile.Container?.Subtype;
        var isMp4 = string.Equals(container, MediaEncodingSubtypes.Mpeg4, StringComparison.OrdinalIgnoreCase)
            || string.Equals(container, "MP4", StringComparison.OrdinalIgnoreCase);
        if (!isMp4)
            return new(WallpaperError.UnsupportedFormat, "The file container must be MP4.");

        if (profile.Video == null)
            return new(WallpaperError.UnsupportedFormat, "The file contains no video track.");

        var videoSubtype = profile.Video.Subtype;
        var isH264 = string.Equals(videoSubtype, MediaEncodingSubtypes.H264, StringComparison.OrdinalIgnoreCase)
            || string.Equals(videoSubtype, "AVC1", StringComparison.OrdinalIgnoreCase);
        if (!isH264)
            return new(WallpaperError.UnsupportedFormat, "The video codec must be H.264.");

        return new();
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        if (playbackFailure is { } failure) throw new InvalidOperationException(failure.Message);
        if (WindowHandle == 0) throw new InvalidOperationException("The video wallpaper window is no longer valid.");
        appWindow!.Show(false);
        player!.Play();
        Trace.WriteLine("Video playing: muted=True, looping=True, stretch=UniformToFill.");
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        if (playbackFailure is { } failure) throw new InvalidOperationException(failure.Message);
        player?.Pause();
        Trace.WriteLine("Video paused.");
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        if (playbackFailure is { } failure) throw new InvalidOperationException(failure.Message);
        player?.Play();
        Trace.WriteLine("Video resumed.");
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        window = null;
        windowHandle = 0;
        appWindow = null;
        try { Dispose(); }
        catch (Exception exception) { Trace.WriteLine($"Video window cleanup failed: {exception}"); }
    }

    public void Dispose()
    {
        if (disposed || LifecycleState == RendererLifecycleState.Disposed) return;
        disposed = true;
        stopped = true;
        opened.TrySetResult(new(WallpaperError.Stopped, "Video opening was stopped."));
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        void Release(Action action)
        {
            try { action(); }
            catch (Exception exception) { Trace.WriteLine(exception); }
        }

        var currentWindow = window;
        var currentHandle = windowHandle;
        var currentElement = element;
        var currentPlayer = player;
        var currentSource = source;

        window = null;
        windowHandle = 0;
        appWindow = null;
        element = null;
        player = null;
        source = null;
        VideoWidth = null;
        VideoHeight = null;
        DecodeWidth = null;
        DecodeHeight = null;

        var hwndAlive = currentWindow != null && currentHandle != 0 && RendererNativeMethods.IsWindow(currentHandle) && LifecycleState != RendererLifecycleState.NativeWindowLost;
        if (!hwndAlive && currentHandle != 0)
        {
            LifecycleState = RendererLifecycleState.NativeWindowLost;
        }

        if (currentPlayer != null)
        {
            Release(() => currentPlayer.MediaOpened -= OnMediaOpened);
            Release(() => currentPlayer.MediaFailed -= OnMediaFailed);
            Release(() => currentPlayer.Pause());
            Release(() => currentPlayer.Source = null);
        }

        if (hwndAlive && currentElement != null)
        {
            Release(() => currentElement.SetMediaPlayer(null));
        }

        if (currentPlayer != null)
        {
            Release(() => currentPlayer.Dispose());
        }

        if (currentSource != null)
        {
            Release(() => currentSource.Dispose());
        }

        if (currentWindow != null && hwndAlive && RendererNativeMethods.IsWindow(currentHandle))
        {
            Release(() => currentWindow.Closed -= OnWindowClosed);
            Release(() => currentWindow.Content = null);
            Release(() => currentWindow.Close());
        }
        else if (currentHandle != 0)
        {
            Trace.WriteLine($"Video window 0x{currentHandle:X} already destroyed; skipped XAML Window teardown.");
        }

        Release(() => lifetime.Dispose());
        LifecycleState = RendererLifecycleState.Disposed;
        Trace.WriteLine("Video renderer disposed; player, source and window released.");
    }
}
