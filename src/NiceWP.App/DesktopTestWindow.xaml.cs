using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NiceWP.Platform.Windows;
using NiceWP.Rendering;
using NiceWP.Runtime;
using Microsoft.Windows.Storage.Pickers;

namespace NiceWP.App;

public sealed partial class DesktopTestWindow : Window
{
    private readonly ImageWallpaperService wallpaper = new(new WindowsDesktopHost(),
        () => new ImageWallpaperRenderer(), () => new VideoWallpaperRenderer());
    private readonly TextWriterTraceListener? diagnostics;
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
            var result = await wallpaper.StartAsync(file.Path);
            if (closed) return;
            PathText.Text = wallpaper.ActivePath ?? "No wallpaper selected";
            StatusText.Text = result.Success ? string.Empty : result.Message;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed) StatusText.Text = "The wallpaper could not be selected or displayed.";
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
        var result = wallpaper.ValidateHost();
        if (!result.Success)
        {
            StatusText.Text = result.Message;
            PathText.Text = wallpaper.ActivePath ?? "No wallpaper selected";
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var result = wallpaper.Stop();
        args.Cancel = !result.Success;
        if (!result.Success) StatusText.Text = result.Message;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        var result = wallpaper.Stop();
        Trace.WriteLine($"Control window closed; cleanup={result.Success}");
        if (diagnostics != null)
        {
            Trace.Listeners.Remove(diagnostics);
            diagnostics.Dispose();
        }
    }
}
