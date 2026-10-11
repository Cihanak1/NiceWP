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
    private AppWindow? appWindow;
    private MediaPlayerElement? element;
    private MediaPlayer? player;
    private MediaSource? source;
    private volatile WallpaperResult? playbackFailure;
    private bool loadStarted;
    private bool stopped;
    private bool disposed;

    public nint WindowHandle => window == null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(window);

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

            player = new MediaPlayer { AutoPlay = false, IsMuted = true, IsLoopingEnabled = true };
            player.CommandManager.IsEnabled = false;
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
            window.Closed += OnWindowClosed;
            // As with the image renderer, retain AppWindow before WorkerW reparenting.
            appWindow = window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            appWindow.MoveAndResize(DisplayArea.Primary.OuterBounds);
            source = MediaSource.CreateFromStorageFile(file);
            player.Source = source;
            var result = await opened.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!result.Success) return result;
            Trace.WriteLine($"Video opened: {fullPath}, H.264, window=0x{WindowHandle:X}");
            return playbackFailure ?? new WallpaperResult();
        }
        catch (OperationCanceledException)
        {
            return stopped ? new(WallpaperError.Stopped, "Video opening was stopped.")
                : new(WallpaperError.RendererFailed, "Video opening timed out.");
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            return new(WallpaperError.RendererFailed, "The video could not be opened.");
        }
        finally
        {
            if (!disposed) lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
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
        if (window != null) window.Closed -= OnWindowClosed;
        window = null;
        appWindow = null;
        try { Dispose(); }
        catch (Exception exception) { Trace.WriteLine($"Video window cleanup failed: {exception}"); }
    }

    public void Dispose()
    {
        if (disposed) return;
        stopped = true;
        opened.TrySetResult(new(WallpaperError.Stopped, "Video opening was stopped."));
        lifetime.Cancel();
        List<Exception> errors = [];
        void Release(Action action)
        {
            try { action(); }
            catch (Exception exception) { errors.Add(exception); Trace.WriteLine(exception); }
        }
        if (player != null)
        {
            Release(() => player.MediaOpened -= OnMediaOpened);
            Release(() => player.MediaFailed -= OnMediaFailed);
            Release(() => player.Pause());
            Release(() => player.Source = null);
        }
        Release(() => element?.SetMediaPlayer(null));
        Release(() => { player?.Dispose(); player = null; });
        Release(() => { source?.Dispose(); source = null; });
        Release(() =>
        {
            if (window == null) return;
            window.Closed -= OnWindowClosed;
            window.Content = null;
            window.Close();
            window = null;
        });
        if (errors.Count != 0) throw new AggregateException("Video cleanup failed; retry Stop.", errors);
        element = null;
        appWindow = null;
        lifetime.Dispose();
        disposed = true;
        Trace.WriteLine("Video renderer disposed; player, source and window released.");
    }
}
