using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private RuntimeHostApp? hostApp;
    private Window? anchorWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), $"NiceWP-runtime-host-{Environment.ProcessId}.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.AutoFlush = true;
        }
        catch
        {
            // Ignore diagnostic log setup errors
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var dispatcher = new WinUIHostDispatcher(dispatcherQueue);

        // Keep an unactivated anchor window so WinUI 3 does not terminate when a wallpaper window is closed.
        try
        {
            anchorWindow = new Window { Title = "NiceWP.Runtime.Host.Anchor" };
            if (anchorWindow.AppWindow != null)
            {
                anchorWindow.AppWindow.IsShownInSwitchers = false;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Anchor window initialization warning: {ex.Message}");
        }

        hostApp = new RuntimeHostApp(
            dispatcher: dispatcher,
            onShutdown: () =>
            {
                try
                {
                    hostApp?.Dispose();
                    anchorWindow?.Close();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Error during host disposal on shutdown: {ex}");
                }
                Environment.Exit(0);
            });

        if (!hostApp.IsSingleInstance)
        {
            Trace.WriteLine("NiceWP.Runtime.Host is already running. Exiting.");
            hostApp.Dispose();
            anchorWindow?.Close();
            Environment.Exit(0);
            return;
        }

        hostApp.Start();
    }
}
