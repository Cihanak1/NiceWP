using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.App;

public sealed partial class DesktopTestWindow : Window
{
    private readonly RuntimeClient runtimeClient = new();
    private readonly TextWriterTraceListener? diagnostics;
    private string? activeWallpaperPath;
    private string activeWallpaperType = IpcConstants.WallpaperTypeNone;
    private bool closed;
    private bool selecting;

    public DesktopTestWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 260));
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;
        try
        {
            diagnostics = new TextWriterTraceListener(Path.Combine(Path.GetTempPath(), $"NiceWP-image-test-{Environment.ProcessId}.log"));
            Trace.Listeners.Add(diagnostics);
            Trace.AutoFlush = true;
            Trace.WriteLine($"NiceWP session started: pid={Environment.ProcessId}");
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            StatusText.Text = "Diagnostic log could not be opened.";
        }

        _ = SyncRuntimeStatusAsync();
    }

    public async Task SyncRuntimeStatusAsync()
    {
        try
        {
            var ping = await runtimeClient.PingAsync();
            if (closed) return;

            if (!ping.Success)
            {
                StatusText.Text = ping.Message ?? "Runtime host is unavailable.";
                PathText.Text = "No wallpaper";
                return;
            }

            var status = await runtimeClient.GetStatusAsync();
            if (closed) return;

            if (status.Success && status.HasActiveWallpaper && !string.IsNullOrWhiteSpace(status.ActivePath))
            {
                activeWallpaperPath = status.ActivePath;
                activeWallpaperType = status.WallpaperType;
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
                StatusText.Text = string.Empty;
            }
            else
            {
                activeWallpaperPath = null;
                activeWallpaperType = IpcConstants.WallpaperTypeNone;
                PathText.Text = "No wallpaper";
                StatusText.Text = status.Success ? string.Empty : (status.Message ?? "Failed to query runtime status.");
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error synchronizing runtime status: {exception}");
            if (!closed)
            {
                StatusText.Text = "Failed to synchronize status with runtime host.";
                PathText.Text = "No wallpaper";
            }
        }
    }

    private static string FormatWallpaperDisplay(string? path, string? wallpaperType = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase))
        {
            return "No wallpaper";
        }

        var fileName = Path.GetFileName(path);
        var isVideo = string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
        return isVideo ? $"Active video: {fileName}" : $"Active image: {fileName}";
    }

    private async void OnSelectImage(object sender, RoutedEventArgs args)
    {
        if (selecting || closed) return;
        selecting = true;
        SelectImageButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".mp4");
            var file = await picker.PickSingleFileAsync();
            if (file == null || closed) return;

            var isVideo = string.Equals(Path.GetExtension(file.Path), ".mp4", StringComparison.OrdinalIgnoreCase);
            var result = isVideo
                ? await runtimeClient.SetVideoAsync(file.Path)
                : await runtimeClient.SetImageAsync(file.Path);

            if (closed) return;

            if (result.Success)
            {
                activeWallpaperPath = file.Path;
                activeWallpaperType = isVideo ? IpcConstants.WallpaperTypeVideo : IpcConstants.WallpaperTypeImage;
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
                StatusText.Text = string.Empty;
            }
            else
            {
                StatusText.Text = result.Message ?? "Failed to set wallpaper.";
                PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed) StatusText.Text = "The wallpaper could not be selected or sent to runtime host.";
        }
        finally
        {
            selecting = false;
            if (!closed) SelectImageButton.IsEnabled = true;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (closed || args.WindowActivationState == WindowActivationState.Deactivated) return;
        PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // UI does not stop or shut down the wallpaper host when closing.
        // Wallpaper remains active in the background Runtime Host process.
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        Trace.WriteLine("Control window closed; runtime host continues in background.");
        if (diagnostics != null)
        {
            Trace.Listeners.Remove(diagnostics);
            diagnostics.Dispose();
        }
    }
}
