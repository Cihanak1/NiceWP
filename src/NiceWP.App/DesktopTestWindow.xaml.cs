using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NiceWP.Runtime;

namespace NiceWP.App;

public sealed partial class DesktopTestWindow : Window
{
    private readonly DesktopHostService desktopHost = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly TextWriterTraceListener? diagnostics;
    private bool started;
    private bool closed;

    public DesktopTestWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 600));
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        closeTimer.Tick += OnTestCompleted;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"NiceWP-desktop-test-{Environment.ProcessId}.log");
            diagnostics = new TextWriterTraceListener(path);
            Trace.Listeners.Add(diagnostics);
            Trace.AutoFlush = true;
            Trace.WriteLine($"Desktop test started: {DateTimeOffset.Now:O}, process={Environment.ProcessId}");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Test log could not be opened: {exception.Message}";
            started = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (started)
        {
            return;
        }

        started = true;
        try
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (window == 0)
            {
                throw new InvalidOperationException("WinUI did not provide a valid window handle.");
            }

            var result = await desktopHost.AttachAsync(window, lifetime.Token);
            if (closed)
            {
                return;
            }

            Trace.WriteLine($"Host resolve: success={result.Success}, host=0x{result.Handle:X}, error={result.Error}, nativeError={result.NativeError}");
            if (!result.Success)
            {
                StatusText.Text = $"Desktop host unavailable: {result.Error} (Win32: {result.NativeError})";
                return;
            }

            closeTimer.Start();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Trace.WriteLine("Desktop test stopped during resolution.");
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private bool TryCleanup()
    {
        closeTimer.Stop();
        lifetime.Cancel();
        try
        {
            desktopHost.Stop();
            Trace.WriteLine("Runtime cleanup completed; host reference cleared.");
            return true;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    private void OnTestCompleted(object? sender, object args)
    {
        if (TryCleanup())
        {
            Close();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = !TryCleanup();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        if (TryCleanup())
        {
            desktopHost.Dispose();
        }

        Trace.WriteLine("Desktop test window closed.");
        if (diagnostics != null)
        {
            Trace.Listeners.Remove(diagnostics);
            diagnostics.Dispose();
        }

        lifetime.Dispose();
    }

    private void ReportError(Exception exception)
    {
        Trace.WriteLine(exception);
        if (!closed)
        {
            StatusText.Text = $"Desktop test failed: {exception.Message}";
        }
    }
}
